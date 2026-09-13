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

// O envelope abaixo saiu do portão, e não deste arquivo.
//
// Foi capturado do `signs_rider_reads_for_the_identity_audience`, em
// services/api-gateway/src/lib.rs, que exercita o caminho de assinatura real
// para uma rota de piloto. Um vetor que este pacote assinasse provaria só que o
// verificador concorda consigo mesmo -- e concordar consigo mesmo não é o
// risco. O risco é o portão assinar uma coisa e o identity conferir outra, e
// isso não aparece como build quebrado: aparece como alguém entrando.
const (
	goldSubject   = "rider-123"
	goldRoles     = "Admin"
	goldKeyID     = "local-v1"
	goldAudience  = "projecty.identity"
	goldPath      = "/api/riders?ids=a,b"
	goldIssuedAt  = int64(1_788_912_722)
	goldSignature = "v1=j_vpKiE7WOKIHlf5rAOnbm1ScmDVJz-sVc4-8jEKYzg"
)

var goldKey = bytes.Repeat([]byte("x"), 32)

// O envelope v2 abaixo é o que `signs_the_v2_envelopes_the_verifiers_pin`, em
// services/api-gateway/src/auth.rs, prova que o portão produz. Os valores foram
// calculados à parte, com openssl, a partir da string canônica do ADR 0008; o
// portão os fixa do lado dele e este arquivo do lado do identity.
//
// A rota é a do #191: a foto da CNH, o único corpo da plataforma que é ao mesmo
// tempo sensível e útil a quem o trocasse.
const (
	v2Path      = "/update-image"
	v2Roles     = "Rider"
	v2Body      = "cnh-image:original"
	v2IssuedAt  = int64(1_789_300_000)
	v2Signature = "v2=V6Nzvgn7pJMZjCeBhFND7JmHakPWzdkexDLY8Xk89u8"
	// v2AlsoV1 é o `v1` que o mesmo portão manda junto, para quem ainda não
	// lê o `v2`.
	v2AlsoV1 = "v1=WfqfOycgzRAvAjHP2W7VvzA6AJCQVrxU1GEY4KvWQJM"
)

// captured é o envelope v2 como o portão o manda: as duas assinaturas.
func captured(body io.Reader) *http.Request {
	request := httptest.NewRequest(http.MethodPut, v2Path, body)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, v2Roles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(v2IssuedAt, 10))
	request.Header.Set(SignatureHeader, v2AlsoV1)
	request.Header.Set(SignatureV2Header, v2Signature)
	return request
}

func TestAcceptsAV2EnvelopeTheGatewaySigned(t *testing.T) {
	verifier := frozenAt(t, v2IssuedAt)
	request := captured(strings.NewReader(v2Body))

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
//
// O `v1` que o portão mandou junto continua válido para esse corpo -- ele não
// cobre corpo nenhum. É por isso que, presente o `v2`, só ele decide.
func TestACapturedEnvelopeIsRefusedWithAnotherBody(t *testing.T) {
	verifier := frozenAt(t, v2IssuedAt)

	for name, body := range map[string]string{
		"outra foto":       "cnh-image:attacker",
		"um byte a mais":   v2Body + " ",
		"corpo vazio":      "",
		"prefixo do corpo": v2Body[:len(v2Body)-1],
	} {
		t.Run(name, func(t *testing.T) {
			if verifier.Verify(captured(strings.NewReader(body))) != nil {
				t.Fatal("um envelope capturado serviu para outro corpo")
			}
		})
	}
}

// TestAV1EnvelopeFromAGatewayNotYetOnV2IsStillAccepted: o portão antigo e este
// verificador. É o `TestAcceptsAnEnvelopeTheGatewaySigned` de antes, com o
// corpo que ele não assinava; sem isto, publicar o identity antes do portão
// trancaria as rotas de piloto, que foi a falha do #136.
func TestAV1EnvelopeFromAGatewayNotYetOnV2IsStillAccepted(t *testing.T) {
	verifier := frozen(t)

	request := httptest.NewRequest(http.MethodGet, goldPath, strings.NewReader("qualquer"))
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, goldRoles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(goldIssuedAt, 10))
	request.Header.Set(SignatureHeader, goldSignature)

	if verifier.Verify(request) == nil {
		t.Fatal("um envelope v1 foi recusado durante a transição")
	}
}

// TestWhatTheV2GatewaySendsStillPassesAV1Verifier: o portão novo e um
// verificador antigo.
//
// O verificador antigo lê cinco cabeçalhos e ignora os outros, então o que ele
// vê do portão novo é o envelope sem `X-Identity-Signature-V2`. Isso tem de
// passar, e passa pela `v1`.
//
// É também a janela que continua aberta: quem remove o cabeçalho `v2` de um
// envelope capturado volta à `v1`, que não cobre o corpo. Ela fecha quando os
// verificadores deixarem de aceitar `v1`, que é o passo seguinte do #191.
func TestWhatTheV2GatewaySendsStillPassesAV1Verifier(t *testing.T) {
	verifier := frozenAt(t, v2IssuedAt)
	request := captured(strings.NewReader(v2Body))
	request.Header.Del(SignatureV2Header)

	if verifier.Verify(request) == nil {
		t.Fatal("o `v1` que o portão v2 manda junto não confere")
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
			request := signedV2(t, http.MethodGet, "/api/riders/rider-1", "", now, body)
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

	request := signedV2(t, http.MethodPut, v2Path, whole, time.Now().Unix(),
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

	request := signedV2(t, http.MethodPut, v2Path, "", time.Now().Unix(), huge)
	if verifier.Verify(request) != nil {
		t.Fatal("um corpo acima do teto foi aceito")
	}
}

func TestRejectsWhatIsNotExactlyOneSignature(t *testing.T) {
	cases := map[string]func(http.Header){
		"nenhuma assinatura": func(h http.Header) {
			h.Del(SignatureHeader)
			h.Del(SignatureV2Header)
		},
		"v2 repetida":        func(h http.Header) { h.Add(SignatureV2Header, v2Signature) },
		"v1 repetida":        func(h http.Header) { h.Add(SignatureHeader, v2AlsoV1) },
		"v2 com a versão v1": func(h http.Header) { h.Set(SignatureV2Header, "v1="+v2Signature[3:]) },
		"v2 mexida": func(h http.Header) {
			h.Set(SignatureV2Header, v2Signature[:len(v2Signature)-1]+"A")
		},
	}

	for name, tamper := range cases {
		t.Run(name, func(t *testing.T) {
			verifier := frozenAt(t, v2IssuedAt)
			request := captured(strings.NewReader(v2Body))
			tamper(request.Header)

			if verifier.Verify(request) != nil {
				t.Fatal("aceito")
			}
		})
	}
}

func TestAcceptsAnEnvelopeTheGatewaySigned(t *testing.T) {
	verifier := frozen(t)

	request := httptest.NewRequest(http.MethodGet, goldPath, nil)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, goldRoles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(goldIssuedAt, 10))
	request.Header.Set(SignatureHeader, goldSignature)

	caller := verifier.Verify(request)
	if caller == nil {
		t.Fatal("o identity recusou um envelope que o portão assinou")
	}
	if caller.Subject != goldSubject {
		t.Fatalf("sujeito lido: %q", caller.Subject)
	}
	if !caller.IsAdmin() {
		t.Fatal("o papel Admin não atravessou o envelope")
	}
}

// TestTheSignatureIsBoundToThePath: reapresentar num caminho diferente é o
// ataque que a assinatura existe para impedir.
func TestTheSignatureIsBoundToThePath(t *testing.T) {
	verifier := frozen(t)

	request := httptest.NewRequest(http.MethodGet, "/api/riders?ids=a,c", nil)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, goldRoles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(goldIssuedAt, 10))
	request.Header.Set(SignatureHeader, goldSignature)

	if verifier.Verify(request) != nil {
		t.Fatal("um envelope assinado para outro caminho foi aceito")
	}
}

func TestTheSignatureIsBoundToTheMethod(t *testing.T) {
	verifier := frozen(t)

	request := httptest.NewRequest(http.MethodDelete, goldPath, nil)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, goldRoles)
	request.Header.Set(IssuedAtHeader, strconv.FormatInt(goldIssuedAt, 10))
	request.Header.Set(SignatureHeader, goldSignature)

	// Sem o método na string canônica, um envelope de leitura serviria para
	// apagar o piloto no mesmo caminho.
	if verifier.Verify(request) != nil {
		t.Fatal("um envelope de GET foi aceito num DELETE")
	}
}

func TestRejectsWhatIsNotExactlyOneEnvelope(t *testing.T) {
	cases := map[string]func(http.Header){
		"cabeçalho ausente": func(h http.Header) { h.Del(RolesHeader) },
		// Com dois valores, esta camada e a próxima podem ler coisas
		// diferentes do mesmo envelope.
		"cabeçalho repetido": func(h http.Header) { h.Add(SubjectHeader, "outro-piloto") },
		"chave desconhecida": func(h http.Header) { h.Set(KeyIDHeader, "local-v2") },
		"assinatura mexida": func(h http.Header) {
			h.Set(SignatureHeader, goldSignature[:len(goldSignature)-1]+"A")
		},
		"assinatura sem versão": func(h http.Header) {
			h.Set(SignatureHeader, strings.TrimPrefix(goldSignature, "v1="))
		},
		"assinatura ilegível":  func(h http.Header) { h.Set(SignatureHeader, "v1=não é base64!") },
		"carimbo com sinal":    func(h http.Header) { h.Set(IssuedAtHeader, "+1788912722") },
		"carimbo não numérico": func(h http.Header) { h.Set(IssuedAtHeader, "ontem") },
		"sujeito com espaço":   func(h http.Header) { h.Set(SubjectHeader, "rider 123") },
	}

	for name, tamper := range cases {
		t.Run(name, func(t *testing.T) {
			verifier := frozen(t)
			request := httptest.NewRequest(http.MethodGet, goldPath, nil)
			request.Header.Set(KeyIDHeader, goldKeyID)
			request.Header.Set(SubjectHeader, goldSubject)
			request.Header.Set(RolesHeader, goldRoles)
			request.Header.Set(IssuedAtHeader, strconv.FormatInt(goldIssuedAt, 10))
			request.Header.Set(SignatureHeader, goldSignature)
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
			request := signed(t, http.MethodGet, "/api/riders/rider-1", goldSubject, "Rider", issuedAt)

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
	request := signed(t, http.MethodGet, "/api/riders", goldSubject,
		"Rider,Admin", time.Now().Unix())

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
	return frozenAt(t, goldIssuedAt)
}

func frozenAt(t *testing.T, issuedAt int64) *Verifier {
	t.Helper()
	verifier := signing(t)
	verifier.now = func() time.Time { return time.Unix(issuedAt, 0) }
	return verifier
}

// signedV2 assina só o `v2`, sobre `signedBody`, e manda `body` -- que é o
// mesmo conteúdo em outra forma, ou nada.
func signedV2(
	t *testing.T,
	method, pathAndQuery, signedBody string,
	issuedAt int64,
	body io.Reader,
) *http.Request {
	t.Helper()
	stamp := strconv.FormatInt(issuedAt, 10)
	digest := sha256.Sum256([]byte(signedBody))
	canonical := strings.Join([]string{
		"v2", goldKeyID, goldSubject, v2Roles, stamp, method, pathAndQuery, goldAudience,
		hex.EncodeToString(digest[:]),
	}, "\n")
	mac := hmac.New(sha256.New, goldKey)
	mac.Write([]byte(canonical))

	request := httptest.NewRequest(method, pathAndQuery, body)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, goldSubject)
	request.Header.Set(RolesHeader, v2Roles)
	request.Header.Set(IssuedAtHeader, stamp)
	request.Header.Set(SignatureV2Header, "v2="+base64.RawURLEncoding.EncodeToString(mac.Sum(nil)))
	return request
}

type zeros struct{}

func (zeros) Read(buffer []byte) (int, error) {
	clear(buffer)
	return len(buffer), nil
}

func signing(t *testing.T) *Verifier {
	t.Helper()
	verifier, err := New(goldKey, goldKeyID, goldAudience)
	if err != nil {
		t.Fatal(err)
	}
	return verifier
}

func signed(
	t *testing.T,
	method, pathAndQuery, subject, roles string,
	issuedAt int64,
) *http.Request {
	t.Helper()
	stamp := strconv.FormatInt(issuedAt, 10)
	canonical := strings.Join([]string{
		"v1", goldKeyID, subject, roles, stamp, method, pathAndQuery, goldAudience,
	}, "\n")
	mac := hmac.New(sha256.New, goldKey)
	mac.Write([]byte(canonical))

	request := httptest.NewRequest(method, pathAndQuery, nil)
	request.Header.Set(KeyIDHeader, goldKeyID)
	request.Header.Set(SubjectHeader, subject)
	request.Header.Set(RolesHeader, roles)
	request.Header.Set(IssuedAtHeader, stamp)
	request.Header.Set(SignatureHeader, "v1="+base64.RawURLEncoding.EncodeToString(mac.Sum(nil)))
	return request
}
