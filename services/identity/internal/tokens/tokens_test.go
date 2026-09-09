package tokens

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/keys"
)

// O token só serve se o portão o aceitar, e o portão é um programa em Rust que
// não participa deste teste. O que dá para provar aqui é a metade que é
// verificável de um lado só: que o formato na fita é exatamente o que
// services/api-gateway/src/auth.rs lê.
//
// A outra metade -- que aquele Rust realmente aceita ISTO -- está fixada lá, no
// teste que carrega um token emitido por este código.
func TestTokenCarriesExactlyWhatTheGatewayRequires(t *testing.T) {
	key := fixedKey(t)
	issuedAt := time.Unix(1_790_000_000, 0).UTC()
	minter := NewMinter("projecty.identity", []string{"projecty.rental-core", "projecty.billing"}, 5*time.Minute)

	access, err := minter.Mint(key, "8a1f9c2e-0000-4000-8000-000000000001", []string{"Rider"}, issuedAt)
	if err != nil {
		t.Fatalf("emitindo: %v", err)
	}

	header, claims := parse(t, access.Token)

	if header["alg"] != "EdDSA" {
		t.Fatalf("o portão só aceita EdDSA; veio %v", header["alg"])
	}
	// Sem `kid` o portão não sabe qual chave pública do JWKS usar, e recusa
	// antes de olhar a assinatura -- ou seja, rotação deixa de existir.
	if header["kid"] != key.ID {
		t.Fatalf("kid errado no cabeçalho: %v", header["kid"])
	}

	for _, claim := range []string{"sub", "iss", "aud", "exp", "iat", "jti"} {
		if _, present := claims[claim]; !present {
			t.Fatalf("o portão exige %q e ele não está no token", claim)
		}
	}
	if claims["iss"] != "projecty.identity" {
		t.Fatalf("emissor inesperado: %v", claims["iss"])
	}

	// A audiência é uma lista, e é isso que permite uma tela ler aluguel e nota
	// com um token só -- sem dois serviços responderem pelo mesmo nome.
	audiences, ok := claims["aud"].([]any)
	if !ok || len(audiences) != 2 {
		t.Fatalf("aud precisa ser uma lista com as duas audiências: %v", claims["aud"])
	}

	// O portão recusa qualquer token cuja vida passe de
	// GATEWAY_JWT_MAX_LIFETIME_SECS, que vale 300. Emitir com mais que isso
	// produz um token que este serviço assina e ninguém aceita.
	lifetime := claims["exp"].(float64) - claims["iat"].(float64)
	if lifetime != 300 {
		t.Fatalf("vida do token = %v segundos", lifetime)
	}
	if access.ExpiresIn != 300 {
		t.Fatalf("expiresIn = %d", access.ExpiresIn)
	}
}

func TestSignatureIsOverTheHeaderAndPayload(t *testing.T) {
	key := fixedKey(t)
	minter := NewMinter("projecty.identity", []string{"projecty.rental-core"}, 5*time.Minute)

	access, err := minter.Mint(key, "rider-1", nil, time.Now())
	if err != nil {
		t.Fatal(err)
	}

	parts := strings.Split(access.Token, ".")
	if len(parts) != 3 {
		t.Fatalf("um JWT tem três partes, veio %d", len(parts))
	}
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		t.Fatalf("assinatura não está em base64url sem padding: %v", err)
	}
	signed := parts[0] + "." + parts[1]
	if !ed25519.Verify(key.Public, []byte(signed), signature) {
		t.Fatal("a assinatura não fecha sobre cabeçalho e corpo")
	}

	// Um byte mexido no corpo precisa quebrar a assinatura. É o teste que
	// separa "assinado" de "codificado em base64".
	tampered := []byte(signed)
	tampered[len(tampered)-1] ^= 0x01
	if ed25519.Verify(key.Public, tampered, signature) {
		t.Fatal("assinatura aceitou um corpo adulterado")
	}
}

func TestEveryTokenGetsItsOwnIdentifier(t *testing.T) {
	key := fixedKey(t)
	minter := NewMinter("projecty.identity", []string{"projecty.rental-core"}, 5*time.Minute)

	first, err := minter.Mint(key, "rider-1", nil, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	second, err := minter.Mint(key, "rider-1", nil, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	// O `jti` é o que a denylist do ADR 0017 revoga. Dois tokens com o mesmo
	// identificador transformariam uma revogação em duas.
	if first.TokenID == second.TokenID {
		t.Fatal("dois tokens receberam o mesmo jti")
	}
}

func fixedKey(t *testing.T) keys.Key {
	t.Helper()
	seed := make([]byte, ed25519.SeedSize)
	for index := range seed {
		seed[index] = byte(index)
	}
	key, err := keys.NewKey("20260908-testkey", seed)
	if err != nil {
		t.Fatal(err)
	}
	return key
}

func parse(t *testing.T, token string) (header, claims map[string]any) {
	t.Helper()
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		t.Fatalf("um JWT tem três partes, veio %d", len(parts))
	}
	return decodeSegment(t, parts[0]), decodeSegment(t, parts[1])
}

func decodeSegment(t *testing.T, segment string) map[string]any {
	t.Helper()
	raw, err := base64.RawURLEncoding.DecodeString(segment)
	if err != nil {
		t.Fatalf("segmento não está em base64url sem padding: %v", err)
	}
	var decoded map[string]any
	if err := json.Unmarshal(raw, &decoded); err != nil {
		t.Fatalf("segmento não é JSON: %v", err)
	}
	return decoded
}
