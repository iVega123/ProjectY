package revocations

import (
	"context"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/redis/go-redis/v9"

	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
)

// TestTheKeyIsTheOneTheGatewayReads: o identity em Go grava, o portão em Rust
// lê. Uma chave com outro formato seria uma revogação que parece feita e não
// recusa nada. A outra ponta é `the_denylist_key_is_the_one_identity_writes`,
// em services/api-gateway/src/revocation.rs.
func TestTheKeyIsTheOneTheGatewayReads(t *testing.T) {
	got := Key("de3004f4-ef5c-42d7-9e37-ca0f425b73d7")
	if got != "projecty:revoked:jti:de3004f4-ef5c-42d7-9e37-ca0f425b73d7" {
		t.Fatalf("chave: %q", got)
	}
}

// TestTheMarginIsTheOneTheGatewayCapsItsSkewAt: o portão aceita um token até a
// folga de relógio depois do `exp`, e recusa subir com folga maior que 60 s
// (`DENYLIST_KEY_MARGIN`, em services/api-gateway/src/config.rs). Encurtar esta
// margem sem baixar aquele teto deixaria um token revogado voltar a criar
// aluguel no fim da folga.
func TestTheMarginIsTheOneTheGatewayCapsItsSkewAt(t *testing.T) {
	if Margin != 60*time.Second {
		t.Fatalf("margem %v; o portão limita a folga de relógio a 60 s", Margin)
	}
}

func TestSyncOnceCopiesWhatTheDatabaseRevoked(t *testing.T) {
	alive := sessions.AccessToken{ID: "vivo", ExpiresAt: time.Now().Add(4 * time.Minute)}
	source := fakeSource{tokens: []sessions.AccessToken{alive}, asked: &struct{ after time.Time }{}}
	denylist := &recording{}

	if err := SyncOnce(context.Background(), source, denylist); err != nil {
		t.Fatal(err)
	}
	if len(denylist.denied) != 1 || denylist.denied[0] != alive {
		t.Fatalf("negado: %+v", denylist.denied)
	}
	// O corte vai uma margem para trás: um token que expirou há dez segundos o
	// portão ainda aceita, pela folga de relógio.
	if time.Since(source.asked.after) < Margin-time.Second {
		t.Fatalf("a consulta cortou em %v", source.asked.after)
	}
}

// TestAnUnreachableRedisIsAnErrorAndNotAWait: a gravação acontece dentro do
// logout. Um Redis fora do ar precisa virar erro rápido, e não uma resposta de
// logout pendurada.
func TestAnUnreachableRedisIsAnErrorAndNotAWait(t *testing.T) {
	denylist, err := NewRedis("redis://127.0.0.1:1/0")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = denylist.Close() }()

	started := time.Now()
	err = denylist.Deny(context.Background(), []sessions.AccessToken{
		{ID: uuid.NewString(), ExpiresAt: time.Now().Add(time.Minute)},
	})
	if err == nil {
		t.Fatal("um Redis inalcançável não devolveu erro")
	}
	if elapsed := time.Since(started); elapsed > time.Second {
		t.Fatalf("a falha levou %v", elapsed)
	}
}

// TestTheKeyOutlivesTheTokenByTheMargin, contra um Redis de verdade.
func TestTheKeyOutlivesTheTokenByTheMargin(t *testing.T) {
	endpoint := os.Getenv("IDENTITY_TEST_REDIS")
	if endpoint == "" {
		t.Skip("defina IDENTITY_TEST_REDIS=host:porta para rodar contra um Redis")
	}
	denylist, err := NewRedis("redis://" + endpoint + "/0")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = denylist.Close() }()
	reader := redis.NewClient(&redis.Options{Addr: endpoint})
	defer func() { _ = reader.Close() }()

	ctx := context.Background()
	alive := sessions.AccessToken{ID: uuid.NewString(), ExpiresAt: time.Now().Add(3 * time.Minute)}
	// Expirou há dois minutos: nem o portão o aceitaria, e gravá-lo seria lixo.
	dead := sessions.AccessToken{ID: uuid.NewString(), ExpiresAt: time.Now().Add(-2 * time.Minute)}

	if err := denylist.Deny(ctx, []sessions.AccessToken{alive, dead}); err != nil {
		t.Fatal(err)
	}

	ttl, err := reader.TTL(ctx, Key(alive.ID)).Result()
	if err != nil {
		t.Fatal(err)
	}
	expected := time.Until(alive.ExpiresAt.Add(Margin))
	if ttl <= 0 || ttl > expected+time.Second || ttl < expected-3*time.Second {
		t.Fatalf("TTL %v, esperava perto de %v", ttl, expected)
	}
	if exists, _ := reader.Exists(ctx, Key(dead.ID)).Result(); exists != 0 {
		t.Fatal("um token já morto foi gravado")
	}

	// Regravar não empurra o fim: a ressincronização passa a cada cinco
	// segundos, e uma chave que ganhasse vida nova a cada passada nunca sairia.
	if err := denylist.Deny(ctx, []sessions.AccessToken{alive}); err != nil {
		t.Fatal(err)
	}
	again, _ := reader.TTL(ctx, Key(alive.ID)).Result()
	if again > ttl+time.Second {
		t.Fatalf("a regravação alongou a chave: %v depois de %v", again, ttl)
	}
}

type fakeSource struct {
	tokens []sessions.AccessToken
	asked  *struct{ after time.Time }
}

func (f fakeSource) Revoked(_ context.Context, after time.Time) ([]sessions.AccessToken, error) {
	f.asked.after = after
	return f.tokens, nil
}

type recording struct {
	denied []sessions.AccessToken
	fail   bool
}

func (r *recording) Deny(_ context.Context, revoked []sessions.AccessToken) error {
	if r.fail {
		return errors.New("fora do ar")
	}
	r.denied = append(r.denied, revoked...)
	return nil
}
