package kafka

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"log/slog"
	"sort"
	"strconv"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/twmb/franz-go/pkg/kgo"
)

// BatchSize é quantas linhas uma passada reivindica.
const BatchSize = 100

// ClaimLease é quanto uma linha reivindicada pertence a esta relay antes que
// outra possa tomá-la. É maior que sendTimeout de propósito: um lease que
// vencesse com o envio ainda em voo entregaria a linha a outra réplica, e ela
// sairia duas vezes.
const ClaimLease = 30 * time.Second

// sendTimeout limita o envio de um lote inteiro, registry incluído.
const sendTimeout = 15 * time.Second

// Outgoing é uma linha do outbox a caminho do Kafka.
type Outgoing struct {
	ID          string
	Key         string
	Topic       string
	Payload     []byte
	TraceParent string
}

// Sender entrega um lote de uma vez. Devolve um erro por linha, na mesma
// ordem: nil é o broker ter confirmado aquela linha.
type Sender interface {
	Send(ctx context.Context, batch []Outgoing) []error
}

// Pass é o que uma passada fez.
type Pass struct {
	Claimed   int
	Published int
}

// Relay envia ao Kafka o que as transações deixaram no outbox.
//
// Três garantias, as mesmas das relays do rental-core e do billing, cada uma
// com um teste em relay_test.go (ADR 0009):
//
//   - Duas réplicas publicam cada linha uma vez. A linha é reivindicada antes do
//     envio, num UPDATE sobre FOR UPDATE SKIP LOCKED: a outra réplica pula o que
//     esta segura em vez de esperar ou enviar de novo.
//   - Os fatos de um piloto saem em ordem. Uma linha só é reivindicável quando
//     nenhuma linha anterior do mesmo piloto está pendente, reivindicada ou não.
//     Por isso um lote tem no máximo a cabeça de cada piloto, e pode ir inteiro
//     ao broker de uma vez sem trocar a ordem de nada.
//   - Uma relay que morre no meio do envio não prende as linhas. A reivindicação
//     é um lease; vencido, a próxima passada as leva.
type Relay struct {
	db            *sql.DB
	sender        Sender
	close         func()
	log           *slog.Logger
	aggregateType string
	interval      time.Duration
	lease         time.Duration
}

// NewRelay abre o produtor.
func NewRelay(
	db *sql.DB,
	brokers []string,
	registry *Registry,
	logger *slog.Logger,
) (*Relay, error) {
	client, err := kgo.NewClient(
		kgo.SeedBrokers(brokers...),
		// Idempotência ligada: uma reentrega da própria biblioteca não pode
		// virar dois eventos com o mesmo `event_id` na fita.
		kgo.RequiredAcks(kgo.AllISRAcks()),
		kgo.ProducerLinger(10*time.Millisecond),
		// Sem isto um registro espera o broker para sempre, e o lease vence
		// com a linha ainda em voo.
		kgo.RecordDeliveryTimeout(5*time.Second),
	)
	if err != nil {
		return nil, err
	}
	return &Relay{
		db:            db,
		sender:        kafkaSender{client: client, registry: registry},
		close:         client.Close,
		log:           logger,
		aggregateType: "rider",
		interval:      2 * time.Second,
		lease:         ClaimLease,
	}, nil
}

// Close fecha o produtor.
func (r *Relay) Close() {
	if r.close != nil {
		r.close()
	}
}

// Run esvazia o outbox até o contexto acabar.
//
// Drena enquanto as passadas vêm cheias e só espera quando o outbox esvazia ou
// o broker falha. Esperar depois de toda passada é o que limitava uma relay a
// um lote por intervalo.
func (r *Relay) Run(ctx context.Context) {
	for {
		pass, err := r.DispatchOnce(ctx)
		if ctx.Err() != nil {
			return
		}
		if err != nil {
			r.log.Warn("relay adiada; os fatos do piloto ficam retidos", slog.Any("error", err))
		} else if pass.Claimed == BatchSize {
			continue
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(r.interval):
		}
	}
}

// DispatchOnce reivindica um lote, envia e marca o que o broker confirmou.
//
// Marcar como publicado vem DEPOIS do envio. Cair entre os dois republica o
// mesmo evento com o mesmo `event_id`, que é o que o inbox de quem consome
// reconhece. Marcar antes perderia o evento em silêncio, e essa é a única das
// duas falhas que ninguém percebe.
func (r *Relay) DispatchOnce(ctx context.Context) (Pass, error) {
	token := uuid.NewString()
	batch, err := r.claim(ctx, token)
	if err != nil || len(batch) == 0 {
		return Pass{}, err
	}

	sending, cancel := context.WithTimeout(ctx, sendTimeout)
	results := r.sender.Send(sending, batch)
	cancel()

	sent := make([]string, 0, len(batch))
	var failure error
	for index, row := range batch {
		var result error
		if index < len(results) {
			result = results[index]
		} else {
			result = errors.New("o sender não respondeu por esta linha")
		}
		if result == nil {
			sent = append(sent, row.ID)
		} else if failure == nil {
			failure = result
		}
	}
	pass := Pass{Claimed: len(batch), Published: len(sent)}

	// Fora do contexto de quem chamou: um evento já confirmado e deixado sem
	// marca sai de novo depois do lease, e uma parada no meio do lote não
	// precisa custar essa duplicata.
	settling, settled := context.WithTimeout(context.WithoutCancel(ctx), 5*time.Second)
	defer settled()
	if err := r.mark(settling, token, sent); err != nil {
		return pass, err
	}
	if ctx.Err() != nil {
		// Parada no meio do envio: o que não foi confirmado espera o lease, como
		// esperaria se o processo tivesse morrido.
		return pass, ctx.Err()
	}
	if failure != nil {
		// Devolvida, e não presa ao lease: a próxima passada tenta de novo já.
		return pass, errors.Join(failure, r.release(settling, token))
	}
	return pass, nil
}

func (r *Relay) claim(ctx context.Context, token string) ([]Outgoing, error) {
	rows, err := r.db.QueryContext(ctx,
		// O filtro por aggregate_type é o que permite dividir a tabela com o
		// rental-core e o billing sem que uma relay publique os fatos da outra.
		`WITH candidates AS (
		     SELECT candidate.id
		       FROM outbox AS candidate
		      WHERE candidate.published_at IS NULL
		        AND candidate.aggregate_type = $1
		        AND (candidate.claimed_until IS NULL OR candidate.claimed_until < now())
		        AND NOT EXISTS (
		             SELECT 1 FROM outbox AS earlier
		              WHERE earlier.aggregate_type = candidate.aggregate_type
		                AND earlier.aggregate_id = candidate.aggregate_id
		                AND earlier.published_at IS NULL
		                AND earlier.occurred_at < candidate.occurred_at)
		      ORDER BY candidate.occurred_at
		      LIMIT $2
		      FOR UPDATE SKIP LOCKED)
		 UPDATE outbox
		    SET claim_token = $3, claimed_until = now() + $4::INTERVAL
		   FROM candidates
		  WHERE outbox.id = candidates.id
		 RETURNING outbox.id, outbox.aggregate_id, outbox.topic, outbox.payload,
		           outbox.trace_parent, outbox.occurred_at`,
		r.aggregateType, BatchSize, token, fmt.Sprintf("%d milliseconds", r.lease.Milliseconds()))
	if err != nil {
		return nil, err
	}
	defer func() { _ = rows.Close() }()

	type claimed struct {
		row        Outgoing
		occurredAt time.Time
	}
	var batch []claimed
	for rows.Next() {
		var item claimed
		var trace sql.NullString
		if err := rows.Scan(&item.row.ID, &item.row.Key, &item.row.Topic, &item.row.Payload,
			&trace, &item.occurredAt); err != nil {
			return nil, err
		}
		item.row.TraceParent = trace.String
		batch = append(batch, item)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	// RETURNING não promete a ordem da CTE; a ordem de envio volta aqui.
	sort.SliceStable(batch, func(a, b int) bool { return batch[a].occurredAt.Before(batch[b].occurredAt) })
	ordered := make([]Outgoing, len(batch))
	for index, item := range batch {
		ordered[index] = item.row
	}
	return ordered, nil
}

// mark marca o lote numa instrução só. A condição sobre claim_token é o que
// impede uma relay que perdeu o lease de marcar a linha que outra tomou.
func (r *Relay) mark(ctx context.Context, token string, ids []string) error {
	if len(ids) == 0 {
		return nil
	}
	_, err := r.db.ExecContext(ctx,
		`UPDATE outbox
		    SET published_at = now(), claim_token = NULL, claimed_until = NULL
		  WHERE claim_token = $1 AND published_at IS NULL AND id = ANY($2::UUID[])`,
		token, ids)
	return err
}

func (r *Relay) release(ctx context.Context, token string) error {
	_, err := r.db.ExecContext(ctx,
		`UPDATE outbox
		    SET claim_token = NULL, claimed_until = NULL
		  WHERE claim_token = $1 AND published_at IS NULL`,
		token)
	return err
}

// kafkaSender põe o lote inteiro no produtor e espera uma vez.
//
// ProduceSync por registro esperava o broker cem vezes por lote e anulava o
// agrupamento do próprio produtor. Aqui os registros entram todos e o linger
// os junta; cada promessa responde pela sua linha.
type kafkaSender struct {
	client   *kgo.Client
	registry *Registry
}

func (s kafkaSender) Send(ctx context.Context, batch []Outgoing) []error {
	results := make([]error, len(batch))
	var pending sync.WaitGroup
	for index, row := range batch {
		schemaID, err := s.registry.Resolve(ctx, row.Topic)
		if err != nil {
			results[index] = err
			continue
		}
		record := &kgo.Record{
			Topic: row.Topic,
			Key:   []byte(row.Key),
			Value: row.Payload,
			Headers: []kgo.RecordHeader{
				{Key: "schema-id", Value: []byte(strconv.Itoa(schemaID))},
			},
		}
		if row.TraceParent != "" {
			record.Headers = append(record.Headers,
				kgo.RecordHeader{Key: "traceparent", Value: []byte(row.TraceParent)})
		}
		pending.Add(1)
		s.client.Produce(ctx, record, func(_ *kgo.Record, err error) {
			results[index] = err
			pending.Done()
		})
	}
	pending.Wait()
	return results
}
