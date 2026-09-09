// Package api é a borda HTTP do identity.
//
// Duas metades, e a divisão é deliberada.
//
// As rotas de credencial -- cadastrar, entrar, renovar, sair -- e o JWKS são
// públicas, porque são exatamente o que alguém faz ANTES de ter um token, e o
// JWKS precisa ser legível por quem ainda não confia em nada.
//
// As rotas do piloto exigem o envelope de identidade do ADR 0008, verificado em
// `riders.go` contra o que o portão assinou. O identity NÃO valida token: o
// portão é a única fronteira que faz isso, e um segundo lugar onde a validação
// acontece é um segundo lugar onde ela pode divergir.
package api

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/accounts"
	"github.com/iVega123/ProjectY/services/identity/internal/documents"
	"github.com/iVega123/ProjectY/services/identity/internal/facts"
	"github.com/iVega123/ProjectY/services/identity/internal/gateway"
	"github.com/iVega123/ProjectY/services/identity/internal/keys"
	"github.com/iVega123/ProjectY/services/identity/internal/media"
	"github.com/iVega123/ProjectY/services/identity/internal/passwords"
	"github.com/iVega123/ProjectY/services/identity/internal/riders"
	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
	"github.com/iVega123/ProjectY/services/identity/internal/tokens"
)

// maxBodyBytes é pequeno porque todo corpo aqui é um punhado de campos. Sem
// teto, um POST de cadastro é um jeito de gastar memória do processo que emite
// os tokens de todo mundo.
const maxBodyBytes = 16 * 1024

// minimumPasswordLength segue a orientação corrente: comprimento em vez de
// composição. Exigir maiúscula, dígito e símbolo produz `Senha@123` e uma
// anotação no monitor; exigir doze caracteres produz uma frase.
const minimumPasswordLength = 12

// dummyHash existe para o login gastar o mesmo tempo quando o e-mail não
// existe. Sem ele, "usuário inexistente" responde muito mais rápido que "senha
// errada", e a diferença enumera quem tem conta.
var dummyHash string

func init() {
	hash, err := passwords.Hash("uma senha que ninguém tem, só para gastar o mesmo tempo")
	if err != nil {
		panic(fmt.Sprintf("derivando o hash de comparação: %v", err))
	}
	dummyHash = hash
}

// API costura as peças que respondem HTTP.
type API struct {
	accounts  *accounts.Store
	sessions  *sessions.Store
	riders    *riders.Store
	gateway   *gateway.Verifier
	guard     *media.Guard
	objects   ObjectStore
	ring      *keys.Ring
	minter    tokens.Minter
	issuer    string
	publicURL string
	logger    *slog.Logger
}

// ObjectStore é o que a borda precisa do armazenamento de objetos.
//
// Uma interface e não o tipo concreto porque a borda não deve saber que existe
// MinIO do outro lado -- e porque um teste da rota não deveria precisar de um
// armazenamento de objetos de pé para provar quem pode ler o quê.
type ObjectStore interface {
	Put(ctx context.Context, riderID string, sanitized media.Sanitized) (string, error)
	Presign(ctx context.Context, key string, expiry time.Duration) (string, error)
}

// Dependencies são as peças que a borda precisa ter na mão.
//
// Uma struct e não oito parâmetros posicionais: a lista já passou do ponto em
// que trocar dois argumentos do mesmo tipo compila e faz outra coisa.
type Dependencies struct {
	Accounts  *accounts.Store
	Sessions  *sessions.Store
	Riders    *riders.Store
	Gateway   *gateway.Verifier
	Guard     *media.Guard
	Objects   ObjectStore
	Ring      *keys.Ring
	Minter    tokens.Minter
	Issuer    string
	PublicURL string
	Logger    *slog.Logger
}

// New monta a borda com tudo já resolvido.
func New(dependencies Dependencies) *API {
	return &API{
		accounts:  dependencies.Accounts,
		sessions:  dependencies.Sessions,
		riders:    dependencies.Riders,
		gateway:   dependencies.Gateway,
		guard:     dependencies.Guard,
		objects:   dependencies.Objects,
		ring:      dependencies.Ring,
		minter:    dependencies.Minter,
		issuer:    dependencies.Issuer,
		publicURL: dependencies.PublicURL,
		logger:    dependencies.Logger,
	}
}

// Routes devolve o mux pronto.
func (a *API) Routes() *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/auth/register/rider", a.registerRider)
	mux.HandleFunc("POST /api/auth/login", a.login)
	mux.HandleFunc("POST /api/auth/refresh", a.refresh)
	mux.HandleFunc("POST /api/auth/logout", a.logout)
	mux.HandleFunc("GET /.well-known/jwks.json", a.jwks)
	mux.HandleFunc("GET /.well-known/openid-configuration", a.discovery)

	// O domínio do piloto. Diferente das rotas acima, estas exigem o envelope
	// que o portão assina -- ver ADR 0008 e internal/gateway.
	if a.riders != nil {
		mux.HandleFunc("GET /api/riders", a.listRiders)
		mux.HandleFunc("GET /api/riders/{id}", a.getRider)
		mux.HandleFunc("DELETE /api/riders/{id}", a.deleteRider)
	}
	if a.guard != nil && a.objects != nil {
		mux.HandleFunc("PUT /update-image", a.updateImage)
	}
	return mux
}

type registration struct {
	Email       string `json:"email"`
	Password    string `json:"password"`
	Name        string `json:"name"`
	CNPJ        string `json:"cnpj"`
	DateOfBirth string `json:"dateOfBirth"`
	CNHNumber   string `json:"cnhNumber"`
	CNHType     string `json:"cnhType"`
}

func (a *API) registerRider(writer http.ResponseWriter, request *http.Request) {
	var body registration
	if !decode(writer, request, &body) {
		return
	}

	if strings.TrimSpace(body.Email) == "" || strings.TrimSpace(body.Name) == "" {
		fail(writer, http.StatusBadRequest, "e-mail e nome são obrigatórios")
		return
	}
	if len(body.Password) < minimumPasswordLength {
		fail(writer, http.StatusBadRequest,
			fmt.Sprintf("a senha precisa de ao menos %d caracteres", minimumPasswordLength))
		return
	}
	if !documents.ValidCnpj(body.CNPJ) {
		fail(writer, http.StatusBadRequest, "CNPJ inválido")
		return
	}
	if !documents.ValidCnhNumber(body.CNHNumber) {
		fail(writer, http.StatusBadRequest, "o número da CNH precisa ter 11 dígitos")
		return
	}
	cnhType, err := documents.ParseCnhType(body.CNHType)
	if err != nil {
		fail(writer, http.StatusBadRequest, "tipo de CNH inválido")
		return
	}
	dateOfBirth, err := time.Parse(time.DateOnly, strings.TrimSpace(body.DateOfBirth))
	if err != nil {
		fail(writer, http.StatusBadRequest, "data de nascimento precisa estar em AAAA-MM-DD")
		return
	}

	hash, err := passwords.Hash(body.Password)
	if err != nil {
		a.fatal(writer, "derivando o hash da senha", err)
		return
	}

	// Os fatos entram na MESMA transação do cadastro. Um piloto gravado sem
	// `rider.registered` some da projeção do rental-core -- ele se cadastra, e
	// depois não consegue alugar, sem nenhum erro em lugar nenhum.
	ctx := riders.WithTraceParent(request.Context(), traceParent(request))
	announce := func(inner context.Context, transaction *sql.Tx, user accounts.User) error {
		if a.riders == nil {
			return nil
		}
		return a.riders.RegisterFacts(inner, transaction, facts.Rider{
			ID:        user.ID,
			Name:      user.Name,
			CNHNumber: strings.TrimSpace(body.CNHNumber),
			CNHType:   cnhType,
		})
	}

	user, err := a.accounts.RegisterRider(ctx, accounts.Registration{
		Email:        body.Email,
		Name:         body.Name,
		PasswordHash: hash,
		CNPJ:         documents.NormalizeCnpj(body.CNPJ),
		DateOfBirth:  dateOfBirth,
		CNHNumber:    strings.TrimSpace(body.CNHNumber),
		CNHType:      cnhType,
		Verified:     facts.Entitled(cnhType),
	}, announce)
	switch {
	case errors.Is(err, accounts.ErrEmailTaken):
		fail(writer, http.StatusConflict, "e-mail já cadastrado")
		return
	case errors.Is(err, accounts.ErrCnpjTaken):
		fail(writer, http.StatusConflict, "CNPJ já cadastrado")
		return
	case err != nil:
		a.fatal(writer, "cadastrando piloto", err)
		return
	}

	reply(writer, http.StatusCreated, map[string]string{"id": user.ID})
}

type credentials struct {
	Email    string `json:"email"`
	Password string `json:"password"`
}

type issuedSession struct {
	TokenType    string `json:"tokenType"`
	AccessToken  string `json:"accessToken"`
	ExpiresIn    int    `json:"expiresIn"`
	RefreshToken string `json:"refreshToken"`
}

func (a *API) login(writer http.ResponseWriter, request *http.Request) {
	var body credentials
	if !decode(writer, request, &body) {
		return
	}

	user, err := a.accounts.FindByEmail(request.Context(), body.Email)
	if errors.Is(err, accounts.ErrNotFound) {
		// Gasta o mesmo trabalho e responde a mesma coisa. Um 401 diferente
		// para e-mail inexistente transforma o login num verificador de contas.
		_, _, _ = passwords.Verify(dummyHash, body.Password)
		fail(writer, http.StatusUnauthorized, "credenciais inválidas")
		return
	}
	if err != nil {
		a.fatal(writer, "procurando o usuário", err)
		return
	}

	ok, needsRehash, err := passwords.Verify(user.PasswordHash, body.Password)
	if err != nil {
		// Hash ilegível é dado corrompido, não senha errada. Responder 401
		// esconderia uma linha quebrada atrás de "tentou de novo e não entrou".
		a.fatal(writer, "conferindo a senha", err)
		return
	}
	if !ok {
		fail(writer, http.StatusUnauthorized, "credenciais inválidas")
		return
	}

	// O segundo passo da verificação dupla, e ele acontece aqui porque é o
	// único momento em que a senha em claro existe neste processo.
	if needsRehash {
		if rehashed, err := passwords.Hash(body.Password); err == nil {
			if err := a.accounts.ReplacePasswordHash(request.Context(), user.ID, rehashed); err != nil {
				// Não é motivo para recusar um login que já foi aprovado: a
				// migração daquele usuário simplesmente acontece na próxima vez.
				a.logger.Warn("não foi possível regravar o hash legado",
					slog.String("user_id", user.ID), slog.Any("error", err))
			}
		}
	}

	a.issue(writer, request.Context(), user)
}

type refreshRequest struct {
	RefreshToken string `json:"refreshToken"`
}

func (a *API) refresh(writer http.ResponseWriter, request *http.Request) {
	var body refreshRequest
	if !decode(writer, request, &body) {
		return
	}

	userID, next, err := a.sessions.Rotate(request.Context(), body.RefreshToken)
	switch {
	case errors.Is(err, sessions.ErrReplayed):
		// A família já foi revogada dentro de Rotate. A resposta é a mesma de
		// um token desconhecido: quem reapresentou não precisa saber que foi
		// detectado.
		a.logger.Warn("refresh token reapresentado; sessão revogada")
		fail(writer, http.StatusUnauthorized, "sessão inválida")
		return
	case errors.Is(err, sessions.ErrUnknown):
		fail(writer, http.StatusUnauthorized, "sessão inválida")
		return
	case err != nil:
		a.fatal(writer, "renovando a sessão", err)
		return
	}

	user, err := a.accounts.FindByID(request.Context(), userID)
	if err != nil {
		a.fatal(writer, "procurando o usuário da sessão", err)
		return
	}

	access, err := a.mint(user)
	if err != nil {
		a.fatal(writer, "emitindo o access token", err)
		return
	}
	reply(writer, http.StatusOK, issuedSession{
		TokenType:    "Bearer",
		AccessToken:  access.Token,
		ExpiresIn:    access.ExpiresIn,
		RefreshToken: next,
	})
}

func (a *API) logout(writer http.ResponseWriter, request *http.Request) {
	var body refreshRequest
	if !decode(writer, request, &body) {
		return
	}

	// Sair é apresentar o refresh token, e não o access token.
	//
	// O achado A7 era um logout que recebia um e-mail no corpo, não exigia
	// autenticação nenhuma e limpava um cookie que este fluxo nunca usou -- ou
	// seja, qualquer pessoa deslogava qualquer outra, e o token continuava
	// valendo a hora inteira. Aqui a posse do refresh token É a prova de que a
	// sessão é sua, e revogá-la encerra a renovação de verdade.
	err := a.sessions.Revoke(request.Context(), body.RefreshToken)
	if err != nil && !errors.Is(err, sessions.ErrUnknown) {
		a.fatal(writer, "encerrando a sessão", err)
		return
	}
	// Mesmo 204 para token desconhecido: sair é idempotente, e responder
	// diferente diria a quem tentou se aquela sessão existia.
	writer.WriteHeader(http.StatusNoContent)
}

func (a *API) issue(writer http.ResponseWriter, ctx context.Context, user accounts.User) {
	access, err := a.mint(user)
	if err != nil {
		a.fatal(writer, "emitindo o access token", err)
		return
	}
	refresh, err := a.sessions.Issue(ctx, user.ID)
	if err != nil {
		a.fatal(writer, "abrindo a sessão", err)
		return
	}
	reply(writer, http.StatusOK, issuedSession{
		TokenType:    "Bearer",
		AccessToken:  access.Token,
		ExpiresIn:    access.ExpiresIn,
		RefreshToken: refresh,
	})
}

func (a *API) mint(user accounts.User) (tokens.Access, error) {
	return a.minter.Mint(a.ring.Active(), user.ID, user.Roles, time.Now())
}

func (a *API) jwks(writer http.ResponseWriter, _ *http.Request) {
	writer.Header().Set("Content-Type", "application/json")
	// Cache curto e explícito. O portão já guarda a sua própria cópia por
	// GATEWAY_JWKS_CACHE_TTL_SECS; este cabeçalho é para os intermediários no
	// meio, e precisa ser menor que a sobreposição de rotação ou uma chave nova
	// demora a aparecer para quem ainda está servindo a resposta antiga.
	writer.Header().Set("Cache-Control", "public, max-age=60")
	_, _ = writer.Write(a.ring.JWKS())
}

// discovery serve um documento com a forma da descoberta OIDC.
//
// Ele NÃO é um provedor OIDC: não há endpoint de autorização, nem consentimento,
// nem clientes registrados, e o `issuer` é um nome opaco em vez de uma URL,
// porque é assim que o portão o compara. O ADR 0013 pediu o documento porque ele
// é barato e torna o serviço legível para ferramenta padrão -- e é exatamente
// isso que ele entrega, sem prometer o resto.
func (a *API) discovery(writer http.ResponseWriter, _ *http.Request) {
	reply(writer, http.StatusOK, map[string]any{
		"issuer":                                a.issuer,
		"jwks_uri":                              a.publicURL + "/.well-known/jwks.json",
		"token_endpoint":                        a.publicURL + "/api/auth/login",
		"id_token_signing_alg_values_supported": []string{"EdDSA"},
		"subject_types_supported":               []string{"public"},
		"grant_types_supported":                 []string{"password", "refresh_token"},
		"response_types_supported":              []string{},
		"claims_supported":                      []string{"sub", "iss", "aud", "exp", "iat", "jti", "roles"},
	})
}

func decode(writer http.ResponseWriter, request *http.Request, target any) bool {
	request.Body = http.MaxBytesReader(writer, request.Body, maxBodyBytes)
	decoder := json.NewDecoder(request.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		fail(writer, http.StatusBadRequest, "corpo inválido")
		return false
	}
	return true
}

func reply(writer http.ResponseWriter, status int, body any) {
	writer.Header().Set("Content-Type", "application/json")
	writer.WriteHeader(status)
	_ = json.NewEncoder(writer).Encode(body)
}

func fail(writer http.ResponseWriter, status int, message string) {
	reply(writer, status, map[string]string{"error": message})
}

// fatal registra o erro de verdade e devolve uma frase que não descreve nada.
//
// É o achado A9 ao contrário: o que ajuda a depurar ajuda a atacar quando sai
// pela resposta. O detalhe vai para o log, que já está correlacionado por
// trace, e o cliente recebe 500 e ponto.
func (a *API) fatal(writer http.ResponseWriter, what string, err error) {
	a.logger.Error(what, slog.Any("error", err))
	fail(writer, http.StatusInternalServerError, "erro interno")
}
