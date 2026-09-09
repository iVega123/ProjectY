package riders

import (
	"context"
	"database/sql"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/facts"
	"github.com/iVega123/ProjectY/services/identity/internal/testdb"
)

// TestTheVerdictLandsInOneTransaction é a promessa do ADR 0009 na forma mais
// afiada que este serviço tem: ou a mensagem ficou marcada como tratada E o
// piloto foi atualizado E o fato foi enfileirado, ou nada disso aconteceu.
func TestTheVerdictLandsInOneTransaction(t *testing.T) {
	store, database := fresh(t)
	id := seed(t, database, "AB", true)
	ctx := context.Background()
	message := uuid.NewString()

	outcome, err := store.MarkVerified(ctx, message, id, false, time.Now().UTC())
	if err != nil || outcome != Applied {
		t.Fatalf("veredito não aplicado: %v %v", outcome, err)
	}

	if verified(t, database, id) {
		t.Fatal("o documento não conferiu e o piloto continua verificado")
	}
	if pending(t, database, id, facts.TopicVerified) != 1 ||
		pending(t, database, id, facts.TopicVerifiedV2) != 1 {
		t.Fatal("o veredito não virou fato nos dois tópicos")
	}
	if !claimed(t, database, message) {
		t.Fatal("a mensagem não ficou marcada no inbox")
	}
}

// TestTheSameMessageTwiceChangesNothing: a entrega do Kafka é ao-menos-uma-vez,
// e o inbox é o que transforma isso em efeito de uma vez só.
func TestTheSameMessageTwiceChangesNothing(t *testing.T) {
	store, database := fresh(t)
	id := seed(t, database, "AB", true)
	ctx := context.Background()
	message := uuid.NewString()

	if _, err := store.MarkVerified(ctx, message, id, false, time.Now().UTC()); err != nil {
		t.Fatal(err)
	}
	outcome, err := store.MarkVerified(ctx, message, id, false, time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	if outcome != Duplicate {
		t.Fatalf("a segunda entrega foi tratada como %v", outcome)
	}
	if got := pending(t, database, id, facts.TopicVerifiedV2); got != 1 {
		t.Fatalf("a repetição produziu %d fatos", got)
	}
}

// TestTheOcrCanOnlyTakeAway: deixar o OCR CONCEDER faria um piloto com
// habilitação categoria B passar a alugar por ter mandado uma foto legível.
func TestTheOcrCanOnlyTakeAway(t *testing.T) {
	store, database := fresh(t)
	id := seed(t, database, "B", false)

	outcome, err := store.MarkVerified(
		context.Background(), uuid.NewString(), id, true, time.Now().UTC())
	if err != nil || outcome != Applied {
		t.Fatalf("%v %v", outcome, err)
	}
	if verified(t, database, id) {
		t.Fatal("um documento conferido promoveu uma CNH categoria B")
	}
}

// TestAVerdictAboutSomebodyElsesRiderIsStillConsumed: reprocessar não traria o
// piloto de volta, e não avançar o offset prenderia a partição para sempre.
func TestAVerdictAboutAnUnknownRiderIsStillConsumed(t *testing.T) {
	store, database := fresh(t)
	message := uuid.NewString()

	outcome, err := store.MarkVerified(
		context.Background(), message, uuid.NewString(), true, time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	if outcome != Unknown {
		t.Fatalf("veredito sobre desconhecido tratado como %v", outcome)
	}
	if !claimed(t, database, message) {
		t.Fatal("a mensagem voltaria para sempre")
	}
}

func TestTheBatchRefusesWhatItCannotServe(t *testing.T) {
	store, _ := fresh(t)
	ctx := context.Background()

	if _, err := store.ByIDs(ctx, nil); err == nil {
		t.Fatal("um lote vazio foi aceito")
	}
	oversized := make([]string, MaxBatchSize+1)
	for index := range oversized {
		oversized[index] = uuid.NewString()
	}
	if _, err := store.ByIDs(ctx, oversized); err == nil {
		t.Fatal("um lote acima do teto foi aceito")
	}
	if _, err := store.ByIDs(ctx, []string{"não-é-um-uuid"}); err == nil {
		t.Fatal("um identificador ilegível foi aceito")
	}
}

func TestParseIDs(t *testing.T) {
	id := uuid.NewString()
	parsed, err := ParseIDs(" " + id + " , " + id + " ")
	if err != nil || len(parsed) != 2 {
		t.Fatalf("%v %v", parsed, err)
	}
	for _, raw := range []string{"", "   ", ",,,", "a,b"} {
		if _, err := ParseIDs(raw); err == nil {
			t.Fatalf("aceitou %q", raw)
		}
	}
}

// ---------------------------------------------------------------- ferramentas

func fresh(t *testing.T) (*Store, *sql.DB) {
	t.Helper()
	database := testdb.Open(t)
	return NewStore(database), database
}

func seed(t *testing.T, database *sql.DB, cnhType string, entitled bool) string {
	t.Helper()
	id := uuid.NewString()
	if _, err := database.Exec(
		`INSERT INTO users (id, email, email_normalized, password_hash, name, user_type)
		 VALUES ($1, $2, $3, 'x', 'Ada Lovelace', 'rider')`,
		id, "piloto-"+id+"@example.test", "piloto-"+id+"@example.test",
	); err != nil {
		t.Fatal(err)
	}
	if _, err := database.Exec(
		`INSERT INTO riders (user_id, cnpj, date_of_birth, cnh_number, cnh_type, verified)
		 VALUES ($1, $2, '1990-01-31', '12345678901', $3, $4)`,
		id, id[:14], cnhType, entitled,
	); err != nil {
		t.Fatal(err)
	}
	return id
}

func verified(t *testing.T, database *sql.DB, id string) bool {
	t.Helper()
	var found bool
	if err := database.QueryRow(
		`SELECT verified FROM riders WHERE user_id = $1`, id).Scan(&found); err != nil {
		t.Fatal(err)
	}
	return found
}

func pending(t *testing.T, database *sql.DB, id, topic string) int {
	t.Helper()
	var count int
	if err := database.QueryRow(
		`SELECT count(*) FROM outbox
		  WHERE aggregate_type = 'rider' AND aggregate_id = $1 AND topic = $2`,
		id, topic).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count
}

func claimed(t *testing.T, database *sql.DB, message string) bool {
	t.Helper()
	var count int
	if err := database.QueryRow(
		`SELECT count(*) FROM inbox WHERE message_id = $1 AND consumer = $2`,
		message, Consumer).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count == 1
}
