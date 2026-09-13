package api

import (
	"context"
	"log/slog"
	"net/http"
	"time"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
	"github.com/iVega123/ProjectY/services/identity/internal/tokens"
)

// denyTimeout limita a gravação na denylist dentro de uma requisição. Passado
// isso a revogação segue valendo no banco, e a ressincronização a leva ao Redis.
const denyTimeout = 500 * time.Millisecond

// revokeEverySession encerra todas as sessões de um usuário: o caso de conta
// comprometida, ou de quem quer sair de todos os aparelhos.
//
// Pode o próprio usuário ou um administrador. Qualquer outro recebe 404, e não
// 403, pela mesma razão da leitura de piloto: a diferença entre as duas
// respostas diria a quem pergunta que o identificador existe. Para quem pode, a
// resposta é 204 também quando não havia sessão -- revogar é idempotente, e o
// portão repete DELETE quando o transporte falha.
func (a *API) revokeEverySession(writer http.ResponseWriter, request *http.Request) {
	caller := a.caller(writer, request)
	if caller == nil {
		return
	}
	userID := request.PathValue("id")
	if caller.Subject != userID && !caller.IsAdmin() {
		fail(writer, http.StatusNotFound, "usuário não encontrado")
		return
	}

	// Um identificador que não é UUID não tem sessão nenhuma, e perguntar ao
	// banco só produziria um erro de tipo.
	if _, err := uuid.Parse(userID); err == nil {
		revoked, err := a.sessions.RevokeUser(request.Context(), userID)
		if err != nil {
			a.fatal(writer, "revogando as sessões", err)
			return
		}
		a.deny(request.Context(), revoked)
	}
	writer.WriteHeader(http.StatusNoContent)
}

// deny copia para o Redis os access tokens que a revogação acabou de gravar no
// banco, para o portão recusá-los já na próxima operação de alto valor.
//
// Uma falha aqui NÃO falha a requisição. A revogação já está no CockroachDB, que
// é a fonte da verdade; a ressincronização regrava a cópia quando o Redis
// voltar, e enquanto ele está fora o portão recusa as operações de alto valor de
// todo mundo. Responder erro a quem está saindo não protegeria nada, e deixaria a
// pessoa sem saber se saiu.
func (a *API) deny(ctx context.Context, revoked []sessions.AccessToken) {
	if len(revoked) == 0 {
		return
	}
	bounded, cancel := context.WithTimeout(context.WithoutCancel(ctx), denyTimeout)
	defer cancel()
	if err := a.denylist.Deny(bounded, revoked); err != nil {
		a.logger.Warn("denylist indisponível; a revogação segue no banco e será regravada",
			slog.Int("tokens", len(revoked)), slog.Any("error", err))
	}
}

// accessOf é o que a sessão guarda de um access token reservado.
func accessOf(reservation tokens.Reservation) sessions.AccessToken {
	return sessions.AccessToken{ID: reservation.ID, ExpiresAt: reservation.ExpiresAt}
}
