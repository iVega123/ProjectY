package api

import (
	"bytes"
	"crypto/ed25519"
	cryptorand "crypto/rand"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"log/slog"
	"math/rand"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/accounts"
	"github.com/iVega123/ProjectY/services/identity/internal/keys"
	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
	"github.com/iVega123/ProjectY/services/identity/internal/testdb"
	"github.com/iVega123/ProjectY/services/identity/internal/tokens"
)

const password = "uma senha longa o bastante"

func TestRegistrationAndLogin(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)

	response := fixture.post(t, "/api/auth/login", map[string]string{
		"email": email, "password": password,
	})
	if response.Code != http.StatusOK {
		t.Fatalf("login recusado: %d %s", response.Code, response.Body.String())
	}

	var issued struct {
		TokenType    string `json:"tokenType"`
		AccessToken  string `json:"accessToken"`
		ExpiresIn    int    `json:"expiresIn"`
		RefreshToken string `json:"refreshToken"`
	}
	decodeInto(t, response.Body.Bytes(), &issued)
	if issued.TokenType != "Bearer" || issued.RefreshToken == "" || issued.ExpiresIn != 300 {
		t.Fatalf("sessão emitida incompleta: %+v", issued)
	}

	claims := claimsOf(t, fixture, issued.AccessToken)
	if claims["sub"] == "" {
		t.Fatal("o token saiu sem sujeito")
	}
	roles, _ := claims["roles"].([]any)
	if len(roles) != 1 || roles[0] != accounts.RoleRider {
		t.Fatalf("papéis inesperados no token: %v", claims["roles"])
	}

	// O e-mail é comparado na forma normalizada: entrar com a mesma conta
	// escrita em maiúsculas é entrar na mesma conta.
	upper := fixture.post(t, "/api/auth/login", map[string]string{
		"email": strings.ToUpper(email), "password": password,
	})
	if upper.Code != http.StatusOK {
		t.Fatalf("o e-mail em maiúsculas não entrou: %d", upper.Code)
	}
}

// TestBadCredentialsAreIndistinguishable: a resposta para "não existe" e para
// "senha errada" precisa ser a mesma, ou o login vira um verificador de contas.
func TestBadCredentialsAreIndistinguishable(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)

	wrongPassword := fixture.post(t, "/api/auth/login", map[string]string{
		"email": email, "password": "outra senha qualquer",
	})
	unknownEmail := fixture.post(t, "/api/auth/login", map[string]string{
		"email": "ninguem-" + fmt.Sprint(rand.Int63()) + "@example.test", "password": password,
	})

	if wrongPassword.Code != http.StatusUnauthorized || unknownEmail.Code != http.StatusUnauthorized {
		t.Fatalf("códigos diferentes: %d e %d", wrongPassword.Code, unknownEmail.Code)
	}
	if wrongPassword.Body.String() != unknownEmail.Body.String() {
		t.Fatalf("corpos diferentes:\n%s\n%s", wrongPassword.Body, unknownEmail.Body)
	}
}

func TestRegistrationRefusesWhatItCannotStore(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)

	// O mesmo e-mail e o mesmo CNPJ são recusados pelo índice único, e não por
	// uma consulta prévia: entre consultar e inserir cabe outra requisição.
	same := fixture.body(email, fixture.lastCnpj)
	if got := fixture.post(t, "/api/auth/register/rider", same).Code; got != http.StatusConflict {
		t.Fatalf("e-mail repetido devolveu %d", got)
	}
	repeatedCnpj := fixture.body(uniqueEmail(), fixture.lastCnpj)
	if got := fixture.post(t, "/api/auth/register/rider", repeatedCnpj).Code; got != http.StatusConflict {
		t.Fatalf("CNPJ repetido devolveu %d", got)
	}

	for name, mutate := range map[string]func(map[string]string){
		"CNPJ inválido":    func(b map[string]string) { b["cnpj"] = "11111111111111" },
		"CNH curta":        func(b map[string]string) { b["cnhNumber"] = "123" },
		"tipo de CNH":      func(b map[string]string) { b["cnhType"] = "C" },
		"data mal formada": func(b map[string]string) { b["dateOfBirth"] = "31/01/1990" },
		"senha curta":      func(b map[string]string) { b["password"] = "curta" },
		"sem nome":         func(b map[string]string) { b["name"] = "" },
	} {
		t.Run(name, func(t *testing.T) {
			body := fixture.body(uniqueEmail(), validCnpj())
			mutate(body)
			if got := fixture.post(t, "/api/auth/register/rider", body).Code; got != http.StatusBadRequest {
				t.Fatalf("devolveu %d", got)
			}
		})
	}
}

func TestRefreshRotatesAndTheOldTokenDies(t *testing.T) {
	fixture := start(t)
	first := fixture.session(t)

	second := fixture.refresh(t, first, http.StatusOK)
	if second == first {
		t.Fatal("a renovação devolveu o mesmo token")
	}

	// Reapresentar o token já consumido é o sinal de roubo, e a reação é
	// revogar a família inteira -- não dá para saber se quem reapresentou é a
	// vítima ou o ladrão, e recusar só aquela chamada deixaria o ladrão
	// renovando com o token seguinte.
	fixture.refresh(t, first, http.StatusUnauthorized)
	fixture.refresh(t, second, http.StatusUnauthorized)
}

func TestLogoutEndsTheSession(t *testing.T) {
	fixture := start(t)
	token := fixture.session(t)

	logout := fixture.post(t, "/api/auth/logout", map[string]string{"refreshToken": token})
	if logout.Code != http.StatusNoContent {
		t.Fatalf("logout devolveu %d", logout.Code)
	}
	fixture.refresh(t, token, http.StatusUnauthorized)

	// Sair duas vezes é sair. Uma resposta diferente para um token que já não
	// existe diria a quem tentou se aquela sessão era real.
	again := fixture.post(t, "/api/auth/logout", map[string]string{"refreshToken": token})
	if again.Code != http.StatusNoContent {
		t.Fatalf("o segundo logout devolveu %d", again.Code)
	}
	unknown := fixture.post(t, "/api/auth/logout", map[string]string{"refreshToken": "nunca existiu"})
	if unknown.Code != http.StatusNoContent {
		t.Fatalf("logout de token desconhecido devolveu %d", unknown.Code)
	}
}

func TestJwksAndDiscoveryAreServedWithoutCredentials(t *testing.T) {
	fixture := start(t)

	jwks := fixture.get(t, "/.well-known/jwks.json")
	if jwks.Code != http.StatusOK {
		t.Fatalf("JWKS devolveu %d", jwks.Code)
	}
	if !strings.Contains(jwks.Body.String(), fixture.key.ID) {
		t.Fatal("o JWKS não publica a chave ativa")
	}

	discovery := fixture.get(t, "/.well-known/openid-configuration")
	if discovery.Code != http.StatusOK {
		t.Fatalf("descoberta devolveu %d", discovery.Code)
	}
	var document map[string]any
	decodeInto(t, discovery.Body.Bytes(), &document)
	if document["issuer"] != "projecty.identity" {
		t.Fatalf("emissor divulgado: %v", document["issuer"])
	}
	if !strings.HasSuffix(document["jwks_uri"].(string), "/.well-known/jwks.json") {
		t.Fatalf("jwks_uri divulgado: %v", document["jwks_uri"])
	}
}

func TestOnlyTheDeclaredMethodsAnswer(t *testing.T) {
	fixture := start(t)

	// Não há rota de escrita além das quatro declaradas, e um GET no login não
	// pode virar um jeito de mandar senha na URL.
	request := httptest.NewRequest(http.MethodGet, "/api/auth/login", nil)
	response := httptest.NewRecorder()
	fixture.routes.ServeHTTP(response, request)
	if response.Code != http.StatusMethodNotAllowed {
		t.Fatalf("GET no login devolveu %d", response.Code)
	}
}

// ---------------------------------------------------------------- ferramentas

type fixture struct {
	routes    *http.ServeMux
	key       keys.Key
	lastCnpj  string
	lastEmail string
}

func start(t *testing.T) *fixture {
	t.Helper()
	database := testdb.Open(t)

	seed := make([]byte, ed25519.SeedSize)
	if _, err := cryptorand.Read(seed); err != nil {
		t.Fatal(err)
	}
	key, err := keys.NewKey("20260908-apitest", seed)
	if err != nil {
		t.Fatal(err)
	}
	ring, err := keys.NewRing(key)
	if err != nil {
		t.Fatal(err)
	}

	service := New(
		accounts.NewStore(database),
		sessions.NewStore(database, 7*24*time.Hour),
		ring,
		tokens.NewMinter("projecty.identity", []string{"projecty.rental-core"}, 5*time.Minute),
		"projecty.identity",
		"http://identity:8095",
		slog.New(slog.DiscardHandler),
	)
	return &fixture{routes: service.Routes(), key: key}
}

func (f *fixture) body(email, cnpj string) map[string]string {
	return map[string]string{
		"email":       email,
		"password":    password,
		"name":        "Ada Lovelace",
		"cnpj":        cnpj,
		"dateOfBirth": "1990-01-31",
		"cnhNumber":   "12345678901",
		"cnhType":     "AB",
	}
}

func (f *fixture) register(t *testing.T, expected int) string {
	t.Helper()
	f.lastEmail, f.lastCnpj = uniqueEmail(), validCnpj()
	response := f.post(t, "/api/auth/register/rider", f.body(f.lastEmail, f.lastCnpj))
	if response.Code != expected {
		t.Fatalf("cadastro devolveu %d: %s", response.Code, response.Body)
	}
	return f.lastEmail
}

func (f *fixture) session(t *testing.T) string {
	t.Helper()
	email := f.register(t, http.StatusCreated)
	response := f.post(t, "/api/auth/login", map[string]string{"email": email, "password": password})
	if response.Code != http.StatusOK {
		t.Fatalf("login devolveu %d", response.Code)
	}
	var issued struct {
		RefreshToken string `json:"refreshToken"`
	}
	decodeInto(t, response.Body.Bytes(), &issued)
	return issued.RefreshToken
}

func (f *fixture) refresh(t *testing.T, token string, expected int) string {
	t.Helper()
	response := f.post(t, "/api/auth/refresh", map[string]string{"refreshToken": token})
	if response.Code != expected {
		t.Fatalf("renovação devolveu %d, esperava %d: %s", response.Code, expected, response.Body)
	}
	if expected != http.StatusOK {
		return ""
	}
	var issued struct {
		RefreshToken string `json:"refreshToken"`
	}
	decodeInto(t, response.Body.Bytes(), &issued)
	return issued.RefreshToken
}

func (f *fixture) post(t *testing.T, path string, body any) *httptest.ResponseRecorder {
	t.Helper()
	encoded, err := json.Marshal(body)
	if err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodPost, path, bytes.NewReader(encoded))
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	f.routes.ServeHTTP(response, request)
	return response
}

func (f *fixture) get(t *testing.T, path string) *httptest.ResponseRecorder {
	t.Helper()
	response := httptest.NewRecorder()
	f.routes.ServeHTTP(response, httptest.NewRequest(http.MethodGet, path, nil))
	return response
}

func claimsOf(t *testing.T, f *fixture, token string) map[string]any {
	t.Helper()
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		t.Fatalf("o access token não é um JWT: %q", token)
	}
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		t.Fatal(err)
	}
	if !ed25519.Verify(f.key.Public, []byte(parts[0]+"."+parts[1]), signature) {
		t.Fatal("o token não fecha com a chave ativa")
	}
	raw, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		t.Fatal(err)
	}
	var claims map[string]any
	decodeInto(t, raw, &claims)
	return claims
}

func decodeInto(t *testing.T, raw []byte, target any) {
	t.Helper()
	if err := json.Unmarshal(raw, target); err != nil {
		t.Fatalf("resposta ilegível: %v (%s)", err, raw)
	}
}

func uniqueEmail() string {
	return fmt.Sprintf("piloto-%d@example.test", rand.Int63())
}

// validCnpj monta um CNPJ com dígitos verificadores corretos. Cada cadastro
// precisa do seu: `one_rider_per_cnpj` é um índice único, e os testes dividem o
// mesmo banco.
func validCnpj() string {
	digits := make([]int, 14)
	for index := range 12 {
		digits[index] = rand.Intn(10)
	}
	digits[12] = weightedDigit(digits[:12], []int{5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2})
	digits[13] = weightedDigit(digits[:13], []int{6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2})

	var builder strings.Builder
	for _, digit := range digits {
		fmt.Fprintf(&builder, "%d", digit)
	}
	return builder.String()
}

func weightedDigit(digits []int, weights []int) int {
	sum := 0
	for index, digit := range digits {
		sum += digit * weights[index]
	}
	if remainder := sum % 11; remainder >= 2 {
		return 11 - remainder
	}
	return 0
}
