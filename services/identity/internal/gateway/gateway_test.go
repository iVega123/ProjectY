package gateway

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"
)

const (
	goldSubject  = "rider-123"
	goldKeyID    = "local-v1"
	goldAudience = "projecty.identity"
)

var goldKey = bytes.Repeat([]byte("x"), 32)

// O envelope abaixo é o que `signs_the_v2_envelopes_the_verifiers_pin`, em
// services/api-gateway/src/auth.rs, prova que o portão produz. Os valores foram
// calculados à parte, com openssl, a partir da string canônica do ADR 0008; o
// portão os fixa do lado dele e este arquivo do lado do identity.
//
// Um vetor que este pacote assinasse provaria só que o verificador concorda
// consigo mesmo -- e concordar consigo mesmo não é o risco. O risco é o portão
// assinar uma coisa e o identity conferir outra, e isso não aparece como build
// quebrado: aparece como alguém entrando.
//
// A rota é a do #191: a foto da CNH, o único corpo da plataforma que é ao mesmo
// tempo sensível e útil a quem o trocasse.
const (
	v2Path      = "/update-image"
	v2Roles     = "Rider"
	v2Body      = "cnh-image:original"
	v2IssuedAt  = int64(1_789_300_000)
	v2Signature = "v2=V6Nzvgn7pJMZjCeBhFND7JmHakPWzdkexDLY8Xk89u8"

	// previousV1 é o `v1` que o portão anterior ao #274 mandava junto com este
	// envelope, em legacySignatureHeader. Não cobre corpo nenhum.
	previousV1            = "v1=WfqfOycgzRAvAjHP2W7VvzA6AJCQVrxU1GEY4KvWQJM"
	legacySignatureHeader = "X-Identity-Signature"
)

// O envelope `v1` abaixo saiu de um portão anterior ao #274, capturado do
// `signs_rider_reads_for_the_identity_audience` de então. Era aceito; é o que
// este verificador não aceita mais.
const (
	v1Path      = "/api/riders?ids=a,b"
	v1Roles     = "Admin"
	v1IssuedAt  = int64(1_788_912_722)
	v1Signature = "v1=j_vpKiE7WOKIHlf5rAOnbm1ScmDVJz-sVc4-8jEKYzg"
)

// captured é o envelope como o portão o manda, com o corpo dado.
func captured(body string) *http.Request {
	return capturedAs(http.MethodPut, v2Path, body)
}

// capturedAs é o mesmo envelope, reapresentado com outro método ou caminho.
func capturedAs(method, pathAndQuery, body string) *http.Request {
	request := httptest.NewRequest(method, pathAndQuery, strings.NewReader(body))
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, v2Roles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(v2IssuedAt, 10))
	request.Header.Set(SignatureV2Header, v2Signature)
	return request
}

func TestAcceptsAV2EnvelopeTheGatewaySigned(t *testing.T) {
	verifier := frozen(t)
	request := captured(v2Body)

	caller := verifier.Verify(request)
	if caller == nil {
		t.Fatal("o identity recusou um envelope v2 que o portão assinou")
	}
	if caller.Subject != goldSubject {
		t.Fatalf("sujeito lido: %q", caller.Subject)
	}
	// O handler lê o corpo depois; conferir não pode consumi-lo.
	rest, err := io.ReadAll(request.Body)
	if err != nil || string(rest) != v2Body {
		t.Fatalf("o corpo não voltou intacto: %q, %v", rest, err)
	}
}

// TestACapturedEnvelopeIsRefusedWithAnotherBody é o #191: o mesmo envelope, na
// mesma rota, dentro da janela, com outra foto.
func TestACapturedEnvelopeIsRefusedWithAnotherBody(t *testing.T) {
	verifier := frozen(t)

	for name, body := range map[string]string{
		"outra foto":       "cnh-image:attacker",
		"um byte a mais":   v2Body + " ",
		"corpo vazio":      "",
		"prefixo do corpo": v2Body[:len(v2Body)-1],
	} {
		t.Run(name, func(t *testing.T) {
			if verifier.Verify(captured(body)) != nil {
				t.Fatal("um envelope capturado serviu para outro corpo")
			}
		})
	}
}

// TestAV1OnlyEnvelopeIsRefused: um envelope que um portão anterior assinou, e
// que este verificador aceitava até o #274. O relógio fica no instante da
// assinatura, para que a recusa seja pela versão e não pela janela.
func TestAV1OnlyEnvelopeIsRefused(t *testing.T) {
	verifier := frozenAt(t, v1IssuedAt)

	request := httptest.NewRequest(http.MethodGet, v1Path, nil)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, v1Roles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(v1IssuedAt, 10))
	request.Header.Set(legacySignatureHeader, v1Signature)

	if verifier.Verify(request) != nil {
		t.Fatal("um envelope só com v1 foi aceito")
	}
}

// TestStrippingV2FromACapturedEnvelopeDoesNotFallBackToV1 é o rebaixamento que
// o #274 fecha: o envelope que o portão anterior mandava, com as duas
// assinaturas, sem o cabeçalho `v2` e com outro corpo. O `v1` que sobra é
// válido para esse corpo, porque não cobre corpo nenhum.
func TestStrippingV2FromACapturedEnvelopeDoesNotFallBackToV1(t *testing.T) {
	verifier := frozen(t)

	for name, body := range map[string]string{
		"outra foto":      "cnh-image:attacker",
		"o mesmo corpo":   v2Body,
		"sem corpo algum": "",
	} {
		t.Run(name, func(t *testing.T) {
			request := captured(body)
			request.Header.Set(legacySignatureHeader, previousV1)
			request.Header.Del(SignatureV2Header)

			if verifier.Verify(request) != nil {
				t.Fatal("sem a v2, o envelope voltou a valer pela v1")
			}
		})
	}
}

// TestTheV1OfAPreviousGatewayIsIgnored: durante o rollout do #274, o portão
// anterior ainda manda as duas assinaturas a um verificador novo. Isso passa
// pela `v2`, e o `v1` que veio junto não salva um corpo trocado.
func TestTheV1OfAPreviousGatewayIsIgnored(t *testing.T) {
	verifier := frozen(t)

	original := captured(v2Body)
	original.Header.Set(legacySignatureHeader, previousV1)
	if verifier.Verify(original) == nil {
		t.Fatal("o envelope do portão anterior foi recusado")
	}

	substituted := captured("cnh-image:attacker")
	substituted.Header.Set(legacySignatureHeader, previousV1)
	if verifier.Verify(substituted) != nil {
		t.Fatal("o v1 que veio junto fez valer um corpo trocado")
	}
}

// TestNoBodyAndAnEmptyBodyAreTheSameDigest: a regra do corpo vazio, escrita,
// para que um GET não dependa de cada implementação adivinhar.
func TestNoBodyAndAnEmptyBodyAreTheSameDigest(t *testing.T) {
	verifier := signing(t)
	now := time.Now().Unix()

	for name, body := range map[string]io.Reader{
		"sem corpo":   nil,
		"corpo vazio": bytes.NewReader(nil),
	} {
		t.Run(name, func(t *testing.T) {
			request := signed(t, http.MethodGet, "/api/riders/rider-1", v2Roles, "", now, body)
			if verifier.Verify(request) == nil {
				t.Fatal("recusado")
			}
		})
	}
}

// TestAStreamedBodyIsHashedToItsEnd: sem `Content-Length`, o digest é do corpo
// lido até o fim, que é o que o portão assinou.
func TestAStreamedBodyIsHashedToItsEnd(t *testing.T) {
	verifier := signing(t)
	whole := "cnh-image:in three chunks"

	request := signed(t, http.MethodPut, v2Path, v2Roles, whole, time.Now().Unix(),
		io.MultiReader(strings.NewReader("cnh-image:"), strings.NewReader("in "),
			strings.NewReader("three chunks")))
	request.ContentLength = -1
	request.Header.Del("Content-Length")

	if verifier.Verify(request) == nil {
		t.Fatal("um corpo em streaming foi recusado")
	}
	rest, _ := io.ReadAll(request.Body)
	if string(rest) != whole {
		t.Fatalf("o corpo não voltou intacto: %q", rest)
	}
}

// TestABodyThatTheGatewayCouldNotHaveSignedIsRefused: acima do teto do portão
// não há envelope legítimo, e ler além dele é memória de graça para quem
// alcança a porta.
func TestABodyThatTheGatewayCouldNotHaveSignedIsRefused(t *testing.T) {
	verifier := signing(t)
	huge := io.LimitReader(zeros{}, MaxSignedBodyBytes+1)

	request := signed(t, http.MethodPut, v2Path, v2Roles, "", time.Now().Unix(), huge)
	if verifier.Verify(request) != nil {
		t.Fatal("um corpo acima do teto foi aceito")
	}
}

func TestRejectsWhatIsNotExactlyOneSignature(t *testing.T) {
	cases := map[string]func(http.Header){
		"nenhuma assinatura": func(h http.Header) { h.Del(SignatureV2Header) },
		"v2 repetida":        func(h http.Header) { h.Add(SignatureV2Header, v2Signature) },
		"v2 com a versão v1": func(h http.Header) { h.Set(SignatureV2Header, "v1="+v2Signature[3:]) },
		"o v1 no lugar da v2": func(h http.Header) {
			h.Set(SignatureV2Header, previousV1)
		},
		"v2 mexida": func(h http.Header) {
			h.Set(SignatureV2Header, v2Signature[:len(v2Signature)-1]+"A")
		},
	}

	for name, tamper := range cases {
		t.Run(name, func(t *testing.T) {
			verifier := frozen(t)
			request := captured(v2Body)
			tamper(request.Header)

			if verifier.Verify(request) != nil {
				t.Fatal("aceito")
			}
		})
	}
}

// TestTheSignatureIsBoundToThePath: reapresentar num caminho diferente é o
// ataque que a assinatura existe para impedir.
func TestTheSignatureIsBoundToThePath(t *testing.T) {
	verifier := frozen(t)

	for _, path := range []string{"/update-image?rider=outro", "/update-image/", "/api/riders"} {
		if verifier.Verify(capturedAs(http.MethodPut, path, v2Body)) != nil {
			t.Fatalf("um envelope assinado para outro caminho foi aceito em %s", path)
		}
	}
}

func TestTheSignatureIsBoundToTheMethod(t *testing.T) {
	verifier := frozen(t)

	// Sem o método na string canônica, o envelope de uma troca de foto serviria
	// para qualquer outro verbo no mesmo caminho.
	if verifier.Verify(capturedAs(http.MethodPost, v2Path, v2Body)) != nil {
		t.Fatal("um envelope de PUT foi aceito num POST")
	}
}

func TestRejectsWhatIsNotExactlyOneEnvelope(t *testing.T) {
	cases := map[string]func(http.Header){
		"cabeçalho ausente": func(h http.Header) { h.Del(RolesHeader) },
		// Com dois valores, esta camada e a próxima podem ler coisas
		// diferentes do mesmo envelope.
		"cabeçalho repetido": func(h http.Header) { h.Add(SubjectHeader, "outro-piloto") },
		"chave desconhecida": func(h http.Header) { h.Set(KeyIDHeader, "local-v2") },
		"assinatura sem versão": func(h http.Header) {
			h.Set(SignatureV2Header, strings.TrimPrefix(v2Signature, "v2="))
		},
		"assinatura ilegível":  func(h http.Header) { h.Set(SignatureV2Header, "v2=não é base64!") },
		"carimbo com sinal":    func(h http.Header) { h.Set(IssuedAtHeader, "+1789300000") },
		"carimbo não numérico": func(h http.Header) { h.Set(IssuedAtHeader, "ontem") },
		"sujeito com espaço":   func(h http.Header) { h.Set(SubjectHeader, "rider 123") },
	}

	for name, tamper := range cases {
		t.Run(name, func(t *testing.T) {
			verifier := frozen(t)
			request := captured(v2Body)
			tamper(request.Header)

			if verifier.Verify(request) != nil {
				t.Fatal("aceito")
			}
		})
	}
}

// TestTheWindowIsClosedOnBothSides: um envelope velho é um envelope capturado,
// e um envelope do futuro é um relógio adulterado.
func TestTheWindowIsClosedOnBothSides(t *testing.T) {
	for name, offset := range map[string]time.Duration{
		"velho demais":    -31 * time.Second,
		"futuro demais":   6 * time.Second,
		"dentro, atrás":   -29 * time.Second,
		"dentro, adiante": 4 * time.Second,
	} {
		t.Run(name, func(t *testing.T) {
			verifier := signing(t)
			issuedAt := time.Now().Add(offset).Unix()
			request := signed(t, http.MethodGet, "/api/riders/rider-1", "Rider", "", issuedAt, nil)

			accepted := verifier.Verify(request) != nil
			expected := strings.HasPrefix(name, "dentro")
			if accepted != expected {
				t.Fatalf("aceito=%v, esperava %v", accepted, expected)
			}
		})
	}
}

func TestRolesArriveSeparated(t *testing.T) {
	verifier := signing(t)
	request := signed(t, http.MethodGet, "/api/riders", "Rider,Admin", "", time.Now().Unix(), nil)

	caller := verifier.Verify(request)
	if caller == nil {
		t.Fatal("recusado")
	}
	if len(caller.Roles) != 2 || caller.Roles[0] != "Rider" || caller.Roles[1] != "Admin" {
		t.Fatalf("papéis lidos: %v", caller.Roles)
	}
	if !caller.IsAdmin() {
		t.Fatal("Admin no meio da lista não foi reconhecido")
	}
}

func TestAdminIsRecognizedRegardlessOfCase(t *testing.T) {
	// O lado .NET compara sem diferenciar maiúsculas. Duas camadas discordando
	// sobre quem é administrador seria pior que qualquer das duas regras.
	caller := Caller{Subject: "someone", Roles: []string{"admin"}}
	if !caller.IsAdmin() {
		t.Fatal("`admin` em minúsculas não foi reconhecido")
	}
	if (Caller{Subject: "someone", Roles: []string{"Administrator"}}).IsAdmin() {
		t.Fatal("`Administrator` não é `Admin`")
	}
}

func TestRefusesAConfigurationThatCannotProtectAnything(t *testing.T) {
	if _, err := New(bytes.Repeat([]byte("x"), 31), goldKeyID, goldAudience); err == nil {
		t.Fatal("aceitou uma chave curta demais")
	}
	if _, err := New(goldKey, goldKeyID, "  "); err == nil {
		t.Fatal("aceitou uma audiência em branco")
	}
}

// ---------------------------------------------------------------- ferramentas

// frozen prende o relógio ao instante em que o portão assinou o vetor. Sem
// isso o teste passaria hoje e quebraria em trinta segundos.
func frozen(t *testing.T) *Verifier {
	t.Helper()
	return frozenAt(t, v2IssuedAt)
}

func frozenAt(t *testing.T, issuedAt int64) *Verifier {
	t.Helper()
	verifier := signing(t)
	verifier.now = func() time.Time { return time.Unix(issuedAt, 0) }
	return verifier
}

func signing(t *testing.T) *Verifier {
	t.Helper()
	verifier, err := New(goldKey, goldKeyID, goldAudience)
	if err != nil {
		t.Fatal(err)
	}
	return verifier
}

// signed assina o envelope sobre `signedBody` e manda `body` -- que é o mesmo
// conteúdo em outra forma, ou nada.
func signed(
	t *testing.T,
	method, pathAndQuery, roles, signedBody string,
	issuedAt int64,
	body io.Reader,
) *http.Request {
	t.Helper()
	stamp := strconv.FormatInt(issuedAt, 10)
	digest := sha256.Sum256([]byte(signedBody))
	canonical := strings.Join([]string{
		"v2", goldKeyID, goldSubject, roles, stamp, method, pathAndQuery, goldAudience,
		hex.EncodeToString(digest[:]),
	}, "\n")
	mac := hmac.New(sha256.New, goldKey)
	mac.Write([]byte(canonical))

	request := httptest.NewRequest(method, pathAndQuery, body)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, roles)
	request.Header.Set(IssuedAtHeader, stamp)
	request.Header.Set(SignatureV2Header, "v2="+base64.RawURLEncoding.EncodeToString(mac.Sum(nil)))
	return request
}

type zeros struct{}

func (zeros) Read(buffer []byte) (int, error) {
	clear(buffer)
	return len(buffer), nil
}
