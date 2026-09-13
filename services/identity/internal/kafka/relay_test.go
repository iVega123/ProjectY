package kafka

import (
	"context"
	"database/sql"
	"errors"
	"io"
	"log/slog"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/testdb"
)

// Os testes da relay rodam contra o outbox de verdade, com o Kafka trocado por
// um sender que pode ser desligado, atrasado ou morto no meio do envio. O que
// eles provam é a metade do contrato que mora no banco: o que é reivindicado,
// o que é marcado, e o que sobrevive a uma falha.
//
// Cada teste usa um aggregate_type próprio, e é isso que o deixa dividir a
// tabela com os fatos que os testes dos outros pacotes deixam pendentes.

// TestTwoRelaysPublishEachRowOnce: sem a reivindicação, as duas selecionam o
// mesmo lote e toda linha sai duas vezes.
func TestTwoRelaysPublishEachRowOnce(t *testing.T) {
	database := testdb.Open(t)
	aggregate := isolated()
	start := time.Now().Add(-5 * time.Minute)
	for index := range 250 {
		insert(t, database, aggregate, uuid.NewString(), "rider.registered", start.Add(time.Duration(index)*time.Millisecond))
	}

	sender := &fakeSender{delay: 20 * time.Millisecond}
	drainWithTwo(t, database, aggregate, sender)

	published := sender.all()
	if len(published) != 250 {
		t.Fatalf("publicadas %d, esperadas 250", len(published))
	}
	if distinct := unique(published); distinct != 250 {
		t.Fatalf("%d linhas distintas em 250 envios: houve duplicata", distinct)
	}
	if left := pendingCount(t, database, aggregate); left != 0 {
		t.Fatalf("%d linhas ficaram pendentes", left)
	}
}

// TestTheFactsOfOneRiderLeaveInOrderWithTwoRelays: o cadastro e três vereditos
// de um piloto, disputados por duas relays.
func TestTheFactsOfOneRiderLeaveInOrderWithTwoRelays(t *testing.T) {
	database := testdb.Open(t)
	aggregate := isolated()
	rider := uuid.NewString()
	start := time.Now().Add(-5 * time.Minute)
	topics := []string{"rider.registered", "rider.verified", "rider.verified", "rider.verified"}
	for index, topic := range topics {
		insert(t, database, aggregate, rider, topic, start.Add(time.Duration(index)*time.Second))
	}

	sender := &fakeSender{delay: 10 * time.Millisecond}
	drainWithTwo(t, database, aggregate, sender)

	published := sender.all()
	if len(published) != len(topics) {
		t.Fatalf("publicadas %d, esperadas %d", len(published), len(topics))
	}
	for index, row := range published {
		if row.Topic != topics[index] {
			t.Fatalf("posição %d saiu como %s, esperado %s", index, row.Topic, topics[index])
		}
	}
}

// TestARelayKilledMidSendDoesNotStrandItsRows: o processo morre com o envio em
// voo. Nada é marcado, nada é devolvido, e a reivindicação fica na linha até
// o lease vencer.
func TestARelayKilledMidSendDoesNotStrandItsRows(t *testing.T) {
	database := testdb.Open(t)
	aggregate := isolated()
	insert(t, database, aggregate, uuid.NewString(), "rider.registered", time.Now().Add(-time.Minute))

	dying, die := context.WithCancel(context.Background())
	killed := relayFor(database, aggregate, &fakeSender{onSend: die})
	killed.lease = time.Second
	if _, err := killed.DispatchOnce(dying); !errors.Is(err, context.Canceled) {
		t.Fatalf("a relay morta devolveu %v", err)
	}
	if held := claimedCount(t, database, aggregate); held != 1 {
		t.Fatalf("%d linhas reivindicadas depois da morte, esperada 1", held)
	}

	survivor := &fakeSender{}
	next := relayFor(database, aggregate, survivor)
	whileLeased, err := next.DispatchOnce(context.Background())
	if err != nil || whileLeased.Claimed != 0 {
		t.Fatalf("a linha saiu antes do lease vencer: %+v %v", whileLeased, err)
	}

	time.Sleep(1500 * time.Millisecond)
	afterLease, err := next.DispatchOnce(context.Background())
	if err != nil || afterLease.Published != 1 {
		t.Fatalf("a linha não foi retomada depois do lease: %+v %v", afterLease, err)
	}
	if left := pendingCount(t, database, aggregate); left != 0 {
		t.Fatalf("%d linhas ficaram pendentes", left)
	}
}

// TestAFailedSendReleasesWhatTheBrokerRefused: o broker confirma parte do lote.
// O confirmado fica marcado; o resto volta sem esperar o lease.
func TestAFailedSendReleasesWhatTheBrokerRefused(t *testing.T) {
	database := testdb.Open(t)
	aggregate := isolated()
	start := time.Now().Add(-time.Minute)
	for index := range 4 {
		insert(t, database, aggregate, uuid.NewString(), "rider.registered", start.Add(time.Duration(index)*time.Millisecond))
	}

	relay := relayFor(database, aggregate, &fakeSender{refuse: 2})
	pass, err := relay.DispatchOnce(context.Background())
	if err == nil || pass.Claimed != 4 || pass.Published != 2 {
		t.Fatalf("passada com o broker recusando metade: %+v %v", pass, err)
	}
	if left := pendingCount(t, database, aggregate); left != 2 {
		t.Fatalf("%d linhas pendentes, esperadas 2", left)
	}
	if held := claimedCount(t, database, aggregate); held != 0 {
		t.Fatalf("%d linhas presas ao lease depois de uma recusa", held)
	}

	recovered, err := relayFor(database, aggregate, &fakeSender{}).DispatchOnce(context.Background())
	if err != nil || recovered.Published != 2 {
		t.Fatalf("a nova tentativa não levou o resto: %+v %v", recovered, err)
	}
}

// TestTwoRelaysDrainAtTheStatedRate é o piso de vazão do ADR 0009, como teste.
//
// O sender não custa nada, então o que se mede é o banco: uma reivindicação e
// uma marcação por lote, não duas idas e voltas por linha. Nos 50 eventos/s da
// relay que enviava e marcava linha a linha, este volume levava quase dois minutos.
func TestTwoRelaysDrainAtTheStatedRate(t *testing.T) {
	database := testdb.Open(t)
	aggregate := isolated()
	const events = 5000
	insertMany(t, database, aggregate, events)
	// O que a estatística automática faz depois de uma rajada. Sem ela o
	// otimizador espera um outbox vazio e compara cada linha pendente com todas
	// as outras -- uma propriedade de uma tabela com segundos de vida, não da
	// reivindicação.
	if _, err := database.Exec(`ANALYZE outbox`); err != nil {
		t.Fatal(err)
	}

	sender := &fakeSender{}
	started := time.Now()
	drainWithTwo(t, database, aggregate, sender)
	elapsed := time.Since(started)

	if published := len(sender.all()); published != events {
		t.Fatalf("publicadas %d, esperadas %d", published, events)
	}
	rate := float64(events) / elapsed.Seconds()
	t.Logf("%d eventos em %s: %.0f eventos/s", events, elapsed.Round(time.Millisecond), rate)
	if rate < minimumDrainRate {
		t.Fatalf("vazão de %.0f eventos/s, abaixo do piso de %d", rate, minimumDrainRate)
	}
}

// minimumDrainRate é o piso declarado no ADR 0009 para duas relays contra um
// banco de um nó.
const minimumDrainRate = 500

// ------------------------------------------------------------------ apoio

func isolated() string { return "rider-test-" + uuid.NewString() }

func relayFor(database *sql.DB, aggregate string, sender Sender) *Relay {
	return &Relay{
		db:            database,
		sender:        sender,
		log:           slog.New(slog.NewTextHandler(io.Discard, nil)),
		aggregateType: aggregate,
		interval:      50 * time.Millisecond,
		lease:         ClaimLease,
	}
}

// drainWithTwo roda duas relays até as duas acharem o outbox vazio três vezes
// seguidas: uma vez só pode ser a outra ainda segurando o resto.
func drainWithTwo(t *testing.T, database *sql.DB, aggregate string, sender Sender) {
	t.Helper()
	var group sync.WaitGroup
	failures := make(chan error, 2)
	for range 2 {
		group.Add(1)
		go func() {
			defer group.Done()
			relay := relayFor(database, aggregate, sender)
			for idle := 0; idle < 3; {
				pass, err := relay.DispatchOnce(context.Background())
				if err != nil {
					failures <- err
					return
				}
				if pass.Claimed == 0 {
					idle++
					time.Sleep(20 * time.Millisecond)
				} else {
					idle = 0
				}
			}
		}()
	}
	group.Wait()
	close(failures)
	for err := range failures {
		t.Fatal(err)
	}
}

type fakeSender struct {
	delay  time.Duration
	refuse int
	onSend func()

	mutex     sync.Mutex
	published []Outgoing
}

func (s *fakeSender) Send(ctx context.Context, batch []Outgoing) []error {
	if s.onSend != nil {
		s.onSend()
	}
	results := make([]error, len(batch))
	if ctx.Err() != nil {
		for index := range results {
			results[index] = ctx.Err()
		}
		return results
	}
	if s.delay > 0 {
		time.Sleep(s.delay)
	}
	s.mutex.Lock()
	defer s.mutex.Unlock()
	for index, row := range batch {
		if index >= len(batch)-s.refuse {
			results[index] = errors.New("broker recusou")
			continue
		}
		s.published = append(s.published, row)
	}
	return results
}

func (s *fakeSender) all() []Outgoing {
	s.mutex.Lock()
	defer s.mutex.Unlock()
	return append([]Outgoing(nil), s.published...)
}

func unique(rows []Outgoing) int {
	seen := map[string]bool{}
	for _, row := range rows {
		seen[row.ID] = true
	}
	return len(seen)
}

func insert(t *testing.T, database *sql.DB, aggregateType, aggregateID, topic string, occurredAt time.Time) {
	t.Helper()
	if _, err := database.Exec(
		`INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, occurred_at)
		 VALUES ($1, $2, $3, $3, $4, $5)`,
		aggregateType, aggregateID, topic, []byte{1}, occurredAt.UTC()); err != nil {
		t.Fatal(err)
	}
}

func insertMany(t *testing.T, database *sql.DB, aggregateType string, count int) {
	t.Helper()
	if _, err := database.Exec(
		`INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, occurred_at)
		 SELECT $1, gen_random_uuid()::TEXT, 'rider.registered', 'rider.registered', decode('01', 'hex'),
		        now() - INTERVAL '5 minutes' + (n * INTERVAL '1 millisecond')
		   FROM generate_series(1, $2) AS n`,
		aggregateType, count); err != nil {
		t.Fatal(err)
	}
}

func pendingCount(t *testing.T, database *sql.DB, aggregateType string) int {
	t.Helper()
	return count(t, database,
		`SELECT count(*) FROM outbox WHERE aggregate_type = $1 AND published_at IS NULL`, aggregateType)
}

func claimedCount(t *testing.T, database *sql.DB, aggregateType string) int {
	t.Helper()
	return count(t, database,
		`SELECT count(*) FROM outbox WHERE aggregate_type = $1 AND claim_token IS NOT NULL`, aggregateType)
}

func count(t *testing.T, database *sql.DB, query string, arguments ...any) int {
	t.Helper()
	var value int
	if err := database.QueryRow(query, arguments...).Scan(&value); err != nil {
		t.Fatal(err)
	}
	return value
}
