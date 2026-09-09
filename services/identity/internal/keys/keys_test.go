package keys

import (
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"testing"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/testdb"
)

func TestFirstStartCreatesTheSigningKey(t *testing.T) {
	store := fresh(t)

	ring, err := store.Load(context.Background())
	if err != nil {
		t.Fatalf("carregando: %v", err)
	}
	if ring.Active().ID == "" {
		t.Fatal("subiu sem chave ativa")
	}

	// Subir de novo não pode gerar outra: uma chave nova a cada reinício
	// invalidaria todo token em voo, o que é a rotação acidental que o ADR 0013
	// existe para tornar deliberada.
	again, err := store.Load(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if again.Active().ID != ring.Active().ID {
		t.Fatalf("a segunda subida trocou a chave: %s -> %s", ring.Active().ID, again.Active().ID)
	}
}

// TestRotationKeepsTokensInFlightValid é a definição de pronto do #136 escrita
// como teste: rotacionar não pode deslogar quem já tem token.
func TestRotationKeepsTokensInFlightValid(t *testing.T) {
	store := fresh(t)
	ctx := context.Background()

	before, err := store.Load(ctx)
	if err != nil {
		t.Fatal(err)
	}
	previous := before.Active()

	// Um token já emitido, representado pela assinatura que ele carrega.
	message := []byte("um access token emitido antes da rotação")
	signature := ed25519.Sign(previous.Signer().(ed25519.PrivateKey), message)

	after, err := store.Rotate(ctx, time.Hour)
	if err != nil {
		t.Fatalf("rotacionando: %v", err)
	}
	if after.Active().ID == previous.ID {
		t.Fatal("a rotação não trocou a chave ativa")
	}

	// O portão escolhe a pública pelo `kid` do token. Se a anterior sumisse do
	// JWKS agora, aquele token viraria 401 no meio da sessão de alguém.
	published := parse(t, after.JWKS())
	old, present := published[previous.ID]
	if !present {
		t.Fatal("a chave anterior saiu do JWKS antes de o token expirar")
	}
	if !ed25519.Verify(old, message, signature) {
		t.Fatal("a pública publicada não valida o token emitido antes da rotação")
	}
	if _, present := published[after.Active().ID]; !present {
		t.Fatal("a chave nova não foi publicada")
	}
}

func TestRetiredKeysLeaveTheDocument(t *testing.T) {
	store := fresh(t)
	ctx := context.Background()

	before, err := store.Load(ctx)
	if err != nil {
		t.Fatal(err)
	}
	retired := before.Active().ID

	// Sobreposição negativa: a anterior já nasce vencida. É o mesmo caminho de
	// uma rotação normal depois que o tempo passou, sem esperar de verdade.
	after, err := store.Rotate(ctx, -time.Second)
	if err != nil {
		t.Fatal(err)
	}

	// Um JWKS que só cresce acaba maior que o teto que o portão aceita baixar,
	// e aí a rotação seguinte derruba a autenticação inteira.
	if _, present := parse(t, after.JWKS())[retired]; present {
		t.Fatal("uma chave vencida continua publicada")
	}
}

func TestJwksIsWhatTheGatewayKnowsHowToRead(t *testing.T) {
	store := fresh(t)

	ring, err := store.Load(context.Background())
	if err != nil {
		t.Fatal(err)
	}

	var document struct {
		Keys []map[string]string `json:"keys"`
	}
	if err := json.Unmarshal(ring.JWKS(), &document); err != nil {
		t.Fatalf("JWKS não é JSON: %v", err)
	}
	if len(document.Keys) != 1 {
		t.Fatalf("esperava uma chave, veio %d", len(document.Keys))
	}

	// Os quatro campos que services/api-gateway/src/auth.rs exige antes de
	// aceitar uma chave: kty/crv identificam Ed25519, kid seleciona, e alg/use
	// precisam ou combinar ou estar ausentes.
	key := document.Keys[0]
	for field, expected := range map[string]string{
		"kty": "OKP", "crv": "Ed25519", "alg": "EdDSA", "use": "sig",
	} {
		if key[field] != expected {
			t.Fatalf("%s = %q, esperava %q", field, key[field], expected)
		}
	}
	if key["kid"] != ring.Active().ID {
		t.Fatalf("kid publicado (%q) não é o da chave ativa (%q)", key["kid"], ring.Active().ID)
	}
	raw, err := base64.RawURLEncoding.DecodeString(key["x"])
	if err != nil || len(raw) != ed25519.PublicKeySize {
		t.Fatalf("x não é uma pública Ed25519 em base64url: %v", err)
	}
	// A privada não pode ter vazado para o documento por descuido de campo.
	if _, present := key["d"]; present {
		t.Fatal("o JWKS publicou material privado")
	}
}

// TestTheDatabaseAloneCannotSign é a afirmação do ADR 0013 posta à prova: quem
// lê o banco não vira emissor.
func TestTheDatabaseAloneCannotSign(t *testing.T) {
	store := fresh(t)
	if _, err := store.Load(context.Background()); err != nil {
		t.Fatal(err)
	}

	var sealed, nonce []byte
	if err := store.db.QueryRow(
		`SELECT sealed_seed, seal_nonce FROM signing_keys WHERE status = 'active'`,
	).Scan(&sealed, &nonce); err != nil {
		t.Fatal(err)
	}

	// A semente está no banco, e sem a chave do ambiente ela não abre.
	var wrong [32]byte
	wrong[0] = 0xff
	if _, err := unseal(wrong, nonce, sealed); err == nil {
		t.Fatal("a semente abriu com a chave errada")
	}
	if _, err := unseal(store.sealing, nonce, sealed); err != nil {
		t.Fatalf("a semente não abriu com a chave certa: %v", err)
	}
}

// fresh devolve um store com a tabela de chaves vazia. `signing_keys` é global
// no banco compartilhado, e um teste que herda a chave de outro não testa o que
// diz testar.
func fresh(t *testing.T) *Store {
	t.Helper()
	database := testdb.Open(t)
	if _, err := database.Exec(`DELETE FROM signing_keys`); err != nil {
		t.Fatalf("limpando as chaves: %v", err)
	}
	var sealing [32]byte
	for index := range sealing {
		sealing[index] = byte(index + 7)
	}
	return NewStore(database, sealing)
}

func parse(t *testing.T, document []byte) map[string]ed25519.PublicKey {
	t.Helper()
	var set struct {
		Keys []struct {
			Kid string `json:"kid"`
			X   string `json:"x"`
		} `json:"keys"`
	}
	if err := json.Unmarshal(document, &set); err != nil {
		t.Fatalf("JWKS ilegível: %v", err)
	}
	published := map[string]ed25519.PublicKey{}
	for _, key := range set.Keys {
		raw, err := base64.RawURLEncoding.DecodeString(key.X)
		if err != nil {
			t.Fatalf("chave %s com x ilegível: %v", key.Kid, err)
		}
		published[key.Kid] = ed25519.PublicKey(raw)
	}
	return published
}
