package api

import (
	"context"
	"errors"
	"net/http"
	"sync"
	"testing"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/revocations"
	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
)

// TestLogoutDeniesTheAccessTokensTheSessionHandedOut é o #59: sair encerra a
// renovação E nega os access tokens que a sessão emitiu, para o portão
// recusá-los já na próxima criação de aluguel -- e não cinco minutos depois.
func TestLogoutDeniesTheAccessTokensTheSessionHandedOut(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)
	first := fixture.signIn(t, email)
	second := fixture.rotate(t, first.RefreshToken)
	elsewhere := fixture.signIn(t, email)

	if code := fixture.logout(t, second.RefreshToken); code != http.StatusNoContent {
		t.Fatalf("logout devolveu %d", code)
	}

	// Os dois: o da renovação e o do login, que continua valendo até expirar
	// mesmo com o refresh token dele já trocado.
	for _, token := range []string{first.AccessToken, second.AccessToken} {
		if !fixture.denied.holds(fixture.jti(t, token)) {
			t.Fatal("um access token da sessão encerrada não foi negado")
		}
	}
	// A expiração gravada é a do token, e não uma inventada aqui.
	claims := claimsOf(t, fixture, second.AccessToken)
	if got := fixture.denied.expiry(fixture.jti(t, second.AccessToken)); got.Unix() != int64(claims["exp"].(float64)) {
		t.Fatalf("expiração negada %v, o token diz %v", got, claims["exp"])
	}

	// Sair de um aparelho não derruba o outro.
	if fixture.denied.holds(fixture.jti(t, elsewhere.AccessToken)) {
		t.Fatal("o logout negou o token de outra sessão")
	}
	fixture.refresh(t, elsewhere.RefreshToken, http.StatusOK)
}

// TestAReplayedRefreshTokenDeniesTheFamilysAccessTokens: reapresentar um
// refresh token já trocado revoga a família, e o access token que o ladrão tem
// na mão precisa cair junto -- é o caso exato de token roubado.
func TestAReplayedRefreshTokenDeniesTheFamilysAccessTokens(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)
	stolen := fixture.signIn(t, email)
	renewed := fixture.rotate(t, stolen.RefreshToken)

	fixture.refresh(t, stolen.RefreshToken, http.StatusUnauthorized)

	for _, token := range []string{stolen.AccessToken, renewed.AccessToken} {
		if !fixture.denied.holds(fixture.jti(t, token)) {
			t.Fatal("a reapresentação revogou a família e não negou os access tokens dela")
		}
	}
}

func TestRevokingEverySessionOfAUser(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)
	userID := fixture.lastID
	phone := fixture.signIn(t, email)
	laptop := fixture.signIn(t, email)
	someoneElse := fixture.signIn(t, fixture.register(t, http.StatusCreated))

	if code := fixture.revokeEverySession(t, userID, userID, "Rider"); code != http.StatusNoContent {
		t.Fatalf("revogar as próprias sessões devolveu %d", code)
	}

	for _, session := range []issued{phone, laptop} {
		fixture.refresh(t, session.RefreshToken, http.StatusUnauthorized)
		if !fixture.denied.holds(fixture.jti(t, session.AccessToken)) {
			t.Fatal("uma sessão revogada ficou com o access token de pé")
		}
	}
	if fixture.denied.holds(fixture.jti(t, someoneElse.AccessToken)) {
		t.Fatal("revogar um usuário negou o token de outro")
	}
	fixture.refresh(t, someoneElse.RefreshToken, http.StatusOK)

	// Repetir é 204: o portão reenvia DELETE quando o transporte falha.
	if code := fixture.revokeEverySession(t, userID, userID, "Rider"); code != http.StatusNoContent {
		t.Fatalf("revogar de novo devolveu %d", code)
	}
}

// TestOnlyTheUserOrAnAdministratorRevokesEverySession: qualquer outro recebe
// 404, e a sessão alheia fica intacta.
func TestOnlyTheUserOrAnAdministratorRevokesEverySession(t *testing.T) {
	fixture := start(t)
	email := fixture.register(t, http.StatusCreated)
	victim := fixture.lastID
	session := fixture.signIn(t, email)
	fixture.register(t, http.StatusCreated)
	stranger := fixture.lastID

	if code := fixture.revokeEverySession(t, victim, stranger, "Rider"); code != http.StatusNotFound {
		t.Fatalf("um estranho revogando devolveu %d", code)
	}
	next := fixture.refresh(t, session.RefreshToken, http.StatusOK)
	if fixture.denied.holds(fixture.jti(t, session.AccessToken)) {
		t.Fatal("a tentativa recusada negou o token da vítima")
	}

	if code := fixture.revokeEverySession(t, victim, "um-administrador", "Admin"); code != http.StatusNoContent {
		t.Fatalf("o administrador revogando devolveu %d", code)
	}
	fixture.refresh(t, next, http.StatusUnauthorized)
}

// TestWithRedisDownNobodyIsLoggedOutAndLogoutStillEnds é a linha do Redis na
// tabela de degradação, afirmada: sem Redis entra-se, renova-se e sai-se, e a
// revogação não se perde. Ela está no banco, e volta ao Redis na primeira
// passada da ressincronização.
func TestWithRedisDownNobodyIsLoggedOutAndLogoutStillEnds(t *testing.T) {
	fixture := start(t)
	fixture.denied.failing(true)

	email := fixture.register(t, http.StatusCreated)
	session := fixture.signIn(t, email)
	renewed := fixture.rotate(t, session.RefreshToken)
	if code := fixture.logout(t, renewed.RefreshToken); code != http.StatusNoContent {
		t.Fatalf("logout sem Redis devolveu %d", code)
	}
	fixture.refresh(t, renewed.RefreshToken, http.StatusUnauthorized)
	if fixture.denied.holds(fixture.jti(t, renewed.AccessToken)) {
		t.Fatal("o Redis de mentira estava fora e mesmo assim gravou")
	}

	fixture.denied.failing(false)
	store := sessions.NewStore(fixture.database, time.Hour)
	if err := revocations.SyncOnce(context.Background(), store, fixture.denied); err != nil {
		t.Fatal(err)
	}
	for _, token := range []string{session.AccessToken, renewed.AccessToken} {
		if !fixture.denied.holds(fixture.jti(t, token)) {
			t.Fatal("a ressincronização não levou ao Redis o que o banco revogou")
		}
	}
}

// TestAnUnreachableRedisDoesNotHoldUpLogout: o mesmo, com o cliente de verdade
// apontado para uma porta onde não há nada.
func TestAnUnreachableRedisDoesNotHoldUpLogout(t *testing.T) {
	denylist, err := revocations.NewRedis("redis://127.0.0.1:1/0")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = denylist.Close() }()
	fixture := startWith(t, denylist)

	email := fixture.register(t, http.StatusCreated)
	session := fixture.signIn(t, email)
	fixture.rotate(t, session.RefreshToken)

	started := time.Now()
	if code := fixture.logout(t, session.RefreshToken); code != http.StatusNoContent {
		t.Fatalf("logout devolveu %d", code)
	}
	if elapsed := time.Since(started); elapsed > 2*time.Second {
		t.Fatalf("o logout esperou o Redis por %v", elapsed)
	}
}

// ---------------------------------------------------------------- ferramentas

// denylister é a denylist que [startWith] recebe.
type denylister = revocations.Denylist

// fakeDenylist é a denylist sem Redis: guarda o que foi negado, ou falha.
type fakeDenylist struct {
	mutex  sync.Mutex
	denied map[string]time.Time
	fail   bool
}

func (f *fakeDenylist) Deny(_ context.Context, revoked []sessions.AccessToken) error {
	f.mutex.Lock()
	defer f.mutex.Unlock()
	if f.fail {
		return errors.New("redis fora do ar")
	}
	for _, token := range revoked {
		f.denied[token.ID] = token.ExpiresAt
	}
	return nil
}

func (f *fakeDenylist) holds(tokenID string) bool {
	f.mutex.Lock()
	defer f.mutex.Unlock()
	_, found := f.denied[tokenID]
	return found
}

func (f *fakeDenylist) expiry(tokenID string) time.Time {
	f.mutex.Lock()
	defer f.mutex.Unlock()
	return f.denied[tokenID]
}

func (f *fakeDenylist) failing(fail bool) {
	f.mutex.Lock()
	defer f.mutex.Unlock()
	f.fail = fail
}

type issued struct {
	AccessToken  string `json:"accessToken"`
	RefreshToken string `json:"refreshToken"`
}

func (f *fixture) signIn(t *testing.T, email string) issued {
	t.Helper()
	response := f.post(t, "/api/auth/login", map[string]string{"email": email, "password": password})
	if response.Code != http.StatusOK {
		t.Fatalf("login devolveu %d", response.Code)
	}
	var session issued
	decodeInto(t, response.Body.Bytes(), &session)
	return session
}

func (f *fixture) rotate(t *testing.T, refreshToken string) issued {
	t.Helper()
	response := f.post(t, "/api/auth/refresh", map[string]string{"refreshToken": refreshToken})
	if response.Code != http.StatusOK {
		t.Fatalf("renovação devolveu %d", response.Code)
	}
	var session issued
	decodeInto(t, response.Body.Bytes(), &session)
	return session
}

func (f *fixture) logout(t *testing.T, refreshToken string) int {
	t.Helper()
	return f.post(t, "/api/auth/logout", map[string]string{"refreshToken": refreshToken}).Code
}

func (f *fixture) revokeEverySession(t *testing.T, userID, subject, roles string) int {
	t.Helper()
	return f.send(f.envelope(t, http.MethodDelete, "/api/auth/users/"+userID+"/sessions", subject, roles)).Code
}

func (f *fixture) jti(t *testing.T, accessToken string) string {
	t.Helper()
	id, _ := claimsOf(t, f, accessToken)["jti"].(string)
	if id == "" {
		t.Fatal("o access token saiu sem jti")
	}
	return id
}
