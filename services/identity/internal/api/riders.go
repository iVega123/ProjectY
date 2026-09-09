package api

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/gateway"
	"github.com/iVega123/ProjectY/services/identity/internal/media"
	"github.com/iVega123/ProjectY/services/identity/internal/riders"
)

// presignedFor é quanto tempo a URL da CNH vale. Curto porque o objeto é um
// documento de identificação: uma URL sem prazo é permissão permanente
// entregue a quem vir a resposta uma vez.
const presignedFor = 5 * time.Minute

type riderView struct {
	UserID      string  `json:"userId"`
	Name        string  `json:"name"`
	Email       string  `json:"email"`
	CNPJ        string  `json:"cnpj"`
	DateOfBirth string  `json:"dateOfBirth"`
	CNHNumber   string  `json:"cnhNumber"`
	CNHType     string  `json:"cnhType"`
	Verified    bool    `json:"verified"`
	CNHUrl      *string `json:"cnhUrl,omitempty"`
}

func view(rider riders.Rider) riderView {
	return riderView{
		UserID:      rider.UserID,
		Name:        rider.Name,
		Email:       rider.Email,
		CNPJ:        rider.CNPJ,
		DateOfBirth: rider.DateOfBirth.Format(time.DateOnly),
		CNHNumber:   rider.CNHNumber,
		CNHType:     rider.CNHType,
		Verified:    rider.Verified,
	}
}

// caller confere o envelope do portão e devolve quem chamou, ou nil -- já tendo
// respondido 401.
//
// O identity não valida token: o portão é a única fronteira que faz isso, e
// para trás dele o que chega é o envelope assinado do ADR 0008. Um serviço que
// também aceitasse token seria um segundo lugar onde a validação pode divergir.
func (a *API) caller(writer http.ResponseWriter, request *http.Request) *gateway.Caller {
	found := a.gateway.Verify(request)
	if found == nil {
		fail(writer, http.StatusUnauthorized, "envelope de identidade ausente ou inválido")
		return nil
	}
	return found
}

// listRiders serve o lote que o #138 pede.
//
// Em lote porque a tela do console precisa de N pilotos, e sem isto o N+1 não
// desaparece: ele se muda para dentro do BFF.
func (a *API) listRiders(writer http.ResponseWriter, request *http.Request) {
	caller := a.caller(writer, request)
	if caller == nil {
		return
	}
	// Listar pilotos é rota de administrador, e o portão já recusa quem não é.
	// A conferência aqui é a segunda: o portão pode ser reconfigurado, e esta
	// linha é o que impede uma mudança de rota lá virar vazamento aqui.
	if !caller.IsAdmin() {
		fail(writer, http.StatusForbidden, "somente administradores leem pilotos em lote")
		return
	}

	ids, err := riders.ParseIDs(request.URL.Query().Get("ids"))
	if err != nil {
		fail(writer, http.StatusBadRequest, err.Error())
		return
	}
	found, err := a.riders.ByIDs(request.Context(), ids)
	if err != nil {
		a.fatal(writer, "lendo pilotos em lote", err)
		return
	}

	views := make([]riderView, 0, len(found))
	for _, rider := range found {
		views = append(views, view(rider))
	}
	reply(writer, http.StatusOK, views)
}

// getRider serve um piloto. Um piloto lê o próprio registro; um administrador
// lê o de qualquer um.
func (a *API) getRider(writer http.ResponseWriter, request *http.Request) {
	caller := a.caller(writer, request)
	if caller == nil {
		return
	}
	id := request.PathValue("id")
	if !caller.IsAdmin() && caller.Subject != id {
		// 404, e não 403: a diferença entre as duas respostas diz "este piloto
		// existe, mas não é você", que é o que alguém varrendo identificadores
		// quer ouvir.
		fail(writer, http.StatusNotFound, "piloto não encontrado")
		return
	}

	rider, err := a.riders.ByID(request.Context(), id)
	if errors.Is(err, riders.ErrNotFound) {
		fail(writer, http.StatusNotFound, "piloto não encontrado")
		return
	}
	if err != nil {
		a.fatal(writer, "lendo o piloto", err)
		return
	}

	found := view(rider)
	if rider.CNHObjectKey != nil && a.objects != nil {
		url, err := a.objects.Presign(request.Context(), *rider.CNHObjectKey, presignedFor)
		if err != nil {
			// A URL é um extra. Perder o armazenamento de objetos não pode
			// derrubar a leitura do registro, que é o que o resto do sistema usa.
			a.logger.Warn("não foi possível assinar a URL da CNH")
		} else {
			found.CNHUrl = &url
		}
	}
	reply(writer, http.StatusOK, found)
}

// deleteRider apaga o piloto. Rota de administrador.
func (a *API) deleteRider(writer http.ResponseWriter, request *http.Request) {
	caller := a.caller(writer, request)
	if caller == nil {
		return
	}
	if !caller.IsAdmin() {
		fail(writer, http.StatusForbidden, "somente administradores apagam pilotos")
		return
	}

	id := request.PathValue("id")
	err := a.riders.Delete(riders.WithTraceParent(request.Context(), traceParent(request)), id)
	if errors.Is(err, riders.ErrNotFound) {
		fail(writer, http.StatusNotFound, "piloto não encontrado")
		return
	}
	if err != nil {
		a.fatal(writer, "apagando o piloto", err)
		return
	}
	writer.WriteHeader(http.StatusNoContent)
}

// updateImage recebe a foto da CNH.
//
// O caminho é `/update-image` e não `/api/riders/{id}/cnh` porque era assim que
// o RiderManager servia, e o console já chama assim. Renomear a rota e mover o
// domínio na mesma mudança seria duas migrações misturadas numa.
func (a *API) updateImage(writer http.ResponseWriter, request *http.Request) {
	caller := a.caller(writer, request)
	if caller == nil {
		return
	}
	// O sujeito do envelope decide de quem é a CNH. Aceitar um identificador do
	// corpo deixaria qualquer piloto sobrescrever o documento de outro.
	riderID := caller.Subject

	if err := request.ParseMultipartForm(media.MaxUploadBytes); err != nil {
		fail(writer, http.StatusBadRequest, "envie a imagem como multipart/form-data")
		return
	}
	file, _, err := request.FormFile("cnhFile")
	if err != nil {
		fail(writer, http.StatusBadRequest, "campo cnhFile ausente")
		return
	}
	defer func() { _ = file.Close() }()

	raw, err := io.ReadAll(io.LimitReader(file, media.MaxUploadBytes+1))
	if err != nil || len(raw) > media.MaxUploadBytes {
		fail(writer, http.StatusRequestEntityTooLarge, "a imagem passa de 8 MiB")
		return
	}

	sanitized, err := a.guard.Sanitize(request.Context(), raw)
	switch {
	case errors.Is(err, media.ErrRejected):
		// 422 e não 400: a requisição está bem formada, o conteúdo é que não
		// serve. É a mesma resposta que o RiderManager dava.
		fail(writer, http.StatusUnprocessableEntity, "conteúdo ou dimensões de imagem inválidos")
		return
	case errors.Is(err, media.ErrUnavailable):
		writer.Header().Set("Retry-After", "5")
		fail(writer, http.StatusServiceUnavailable, "processamento de imagem indisponível")
		return
	case err != nil:
		a.fatal(writer, "saneando a imagem", err)
		return
	}

	objectKey, err := a.objects.Put(request.Context(), riderID, sanitized)
	if err != nil {
		writer.Header().Set("Retry-After", "5")
		fail(writer, http.StatusServiceUnavailable, "armazenamento de objetos indisponível")
		a.logger.Warn("falha ao gravar a CNH")
		return
	}

	// O ponteiro e o fato numa transação. Gravar o objeto e não contar deixaria
	// o OCR do risk-pricing sem nada para ler, e o piloto esperando por um
	// veredito que ninguém vai emitir.
	err = a.riders.AttachDocument(
		riders.WithTraceParent(request.Context(), traceParent(request)), riderID, objectKey)
	if errors.Is(err, riders.ErrNotFound) {
		fail(writer, http.StatusNotFound, "piloto não encontrado")
		return
	}
	if err != nil {
		a.fatal(writer, "apontando a CNH", err)
		return
	}
	reply(writer, http.StatusOK, map[string]string{"objectKey": objectKey})
}

func traceParent(request *http.Request) string {
	return strings.TrimSpace(request.Header.Get("traceparent"))
}
