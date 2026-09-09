package gateway

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
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
	verifier := signing(t)
	verifier.now = func() time.Time { return time.Unix(goldIssuedAt, 0) }
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
