package api

import (
	"bytes"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/iVega123/ProjectY/services/identity/internal/facts"
)

func TestARiderReadsTheirOwnRecord(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	response := fixture.send(fixture.envelope(t, http.MethodGet, "/api/riders/"+id, id, "Rider"))
	if response.Code != http.StatusOK {
		t.Fatalf("o piloto não leu o próprio registro: %d %s", response.Code, response.Body)
	}

	var found riderView
	decodeInto(t, response.Body.Bytes(), &found)
	if found.UserID != id || found.CNPJ != fixture.lastCnpj || found.CNHType != "AB" {
		t.Fatalf("registro lido: %+v", found)
	}
	// AB entitula, e o cadastro já diz isso -- o OCR só pode derrubar depois.
	if !found.Verified {
		t.Fatal("um piloto com CNH AB nasceu não verificado")
	}
}

// 404 e não 403, pelo mesmo motivo da nota do billing: a diferença entre as
// duas respostas diz "existe, mas não é seu", que é o que alguém varrendo
// identificadores quer ouvir.
func TestAnotherRidersRecordLooksAbsent(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	theirs := fixture.lastID
	fixture.register(t, http.StatusCreated)
	mine := fixture.lastID

	existing := fixture.send(fixture.envelope(t, http.MethodGet, "/api/riders/"+theirs, mine, "Rider"))
	absent := fixture.send(fixture.envelope(t,
		http.MethodGet, "/api/riders/8a1f9c2e-0000-4000-8000-000000000009", mine, "Rider"))

	if existing.Code != http.StatusNotFound || absent.Code != http.StatusNotFound {
		t.Fatalf("códigos: %d e %d", existing.Code, absent.Code)
	}
	if existing.Body.String() != absent.Body.String() {
		t.Fatalf("corpos diferentes:\n%s\n%s", existing.Body, absent.Body)
	}
}

func TestAnAdministratorReadsAnyRecord(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	response := fixture.send(fixture.envelope(t,
		http.MethodGet, "/api/riders/"+id, "um-administrador", "Admin"))
	if response.Code != http.StatusOK {
		t.Fatalf("administrador recusado: %d", response.Code)
	}
}

// TestTheBatchIsTheContractThe138Asks: sem ele o N+1 não some, só se muda para
// dentro do BFF.
func TestTheBatchReturnsWhatItFindsAndOmitsWhatItDoesNot(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	first := fixture.lastID
	fixture.register(t, http.StatusCreated)
	second := fixture.lastID
	absent := "8a1f9c2e-0000-4000-8000-000000000009"

	path := "/api/riders?ids=" + strings.Join([]string{first, second, absent}, ",")
	response := fixture.send(fixture.envelope(t, http.MethodGet, path, "um-administrador", "Admin"))
	if response.Code != http.StatusOK {
		t.Fatalf("lote recusado: %d %s", response.Code, response.Body)
	}

	var found []riderView
	decodeInto(t, response.Body.Bytes(), &found)
	returned := map[string]bool{}
	for _, rider := range found {
		returned[rider.UserID] = true
	}
	// Um piloto ausente não pode apagar a tela inteira.
	if len(found) != 2 || !returned[first] || !returned[second] {
		t.Fatalf("o lote devolveu %d linhas: %+v", len(found), found)
	}
}

func TestTheBatchIsCappedAndValidated(t *testing.T) {
	fixture := start(t)

	oversized := make([]string, 101)
	for index := range oversized {
		oversized[index] = "8a1f9c2e-0000-4000-8000-00000000000" + string(rune('0'+index%10))
	}

	for name, query := range map[string]string{
		"vazio":          "",
		"ilegível":       "não-é-um-uuid",
		"acima do teto":  strings.Join(oversized, ","),
		"só separadores": ",,,",
	} {
		t.Run(name, func(t *testing.T) {
			path := "/api/riders?ids=" + query
			response := fixture.send(fixture.envelope(t, http.MethodGet, path, "um-admin", "Admin"))
			if response.Code != http.StatusBadRequest {
				t.Fatalf("devolveu %d", response.Code)
			}
		})
	}
}

func TestTheRiderDomainRefusesWhatTheGatewayDidNotSign(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	// Sem envelope nenhum.
	bare := httptest.NewRequest(http.MethodGet, "/api/riders/"+id, nil)
	if got := fixture.send(bare).Code; got != http.StatusUnauthorized {
		t.Fatalf("sem envelope devolveu %d", got)
	}

	// Envelope assinado para outro caminho.
	forged := fixture.envelope(t, http.MethodGet, "/api/riders/outro", id, "Rider")
	replayed := httptest.NewRequest(http.MethodGet, "/api/riders/"+id, nil)
	replayed.Header = forged.Header
	if got := fixture.send(replayed).Code; got != http.StatusUnauthorized {
		t.Fatalf("envelope de outro caminho devolveu %d", got)
	}
}

// TestOnlyAnAdministratorReadsInBulkOrDeletes: a conferência aqui é a SEGUNDA.
// O portão já recusa, e esta linha é o que impede uma mudança de rota lá virar
// vazamento aqui.
func TestOnlyAnAdministratorReadsInBulkOrDeletes(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	batch := fixture.send(fixture.envelope(t,
		http.MethodGet, "/api/riders?ids="+id, id, "Rider"))
	if batch.Code != http.StatusForbidden {
		t.Fatalf("um piloto leu o lote: %d", batch.Code)
	}

	removal := fixture.send(fixture.envelope(t,
		http.MethodDelete, "/api/riders/"+id, id, "Rider"))
	if removal.Code != http.StatusForbidden {
		t.Fatalf("um piloto apagou a si mesmo por uma rota de administrador: %d", removal.Code)
	}
}

// TestDeletingARiderAnnouncesIt: apagar a linha não basta. O rental-core
// autoriza pela projeção local, e sem um fato novo ela continua respondendo
// "verificado" para um piloto que já não existe.
func TestDeletingARiderAnnouncesIt(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	response := fixture.send(fixture.envelope(t,
		http.MethodDelete, "/api/riders/"+id, "um-administrador", "Admin"))
	if response.Code != http.StatusNoContent {
		t.Fatalf("remoção devolveu %d: %s", response.Code, response.Body)
	}

	if fixture.pending(t, id, facts.TopicVerifiedV2) == 0 {
		t.Fatal("a remoção não deixou o fato no outbox")
	}
	// A credencial vai junto: uma conta que entra e não pode fazer nada é o
	// meio-piloto que o achado B10 já produziu uma vez.
	var remaining int
	if err := fixture.database.QueryRow(
		`SELECT count(*) FROM users WHERE id = $1`, id).Scan(&remaining); err != nil {
		t.Fatal(err)
	}
	if remaining != 0 {
		t.Fatal("o usuário sobreviveu à remoção do piloto")
	}
}

// TestRegistrationAnnouncesTheRiderInTheSameTransaction: um cadastro que grava
// e não conta produz alguém que se cadastrou e não consegue alugar, sem erro
// nenhum em lugar nenhum.
func TestRegistrationAnnouncesTheRiderInTheSameTransaction(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)

	for _, topic := range []string{
		facts.TopicRegistered, facts.TopicVerified, facts.TopicVerifiedV2,
	} {
		if fixture.pending(t, fixture.lastID, topic) == 0 {
			t.Fatalf("o cadastro não deixou %s no outbox", topic)
		}
	}
}

func TestTheDocumentPathStoresOnlyWhatTheGuardReturned(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	response := fixture.send(fixture.upload(t, id, "uma foto qualquer"))
	if response.Code != http.StatusOK {
		t.Fatalf("upload recusado: %d %s", response.Code, response.Body)
	}

	// O que foi para o objeto é o PNG do media-guard, e não os bytes que
	// chegaram pela rede. É isso que impede um HTML renomeado para .png de
	// virar um objeto servido por URL assinada.
	found := false
	for _, stored := range fixture.objects.stored {
		if bytes.Equal(stored, []byte("PNG saneado")) {
			found = true
		}
		if bytes.Equal(stored, []byte("uma foto qualquer")) {
			t.Fatal("os bytes crus foram gravados")
		}
	}
	if !found {
		t.Fatal("o PNG saneado não foi gravado")
	}

	if fixture.pending(t, id, facts.TopicDocument) == 0 {
		t.Fatal("o upload não deixou document.stored no outbox")
	}

	// E a leitura passa a trazer a URL assinada.
	read := fixture.send(fixture.envelope(t, http.MethodGet, "/api/riders/"+id, id, "Rider"))
	var view riderView
	decodeInto(t, read.Body.Bytes(), &view)
	if view.CNHUrl == nil || !strings.Contains(*view.CNHUrl, "assinado=1") {
		t.Fatalf("a URL da CNH não veio: %+v", view.CNHUrl)
	}
}

func TestARejectedImageIsUnprocessableAndStoresNothing(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	response := fixture.send(fixture.upload(t, id, "isto não é uma imagem"))
	// 422 e não 400: a requisição está bem formada, o conteúdo é que não serve.
	if response.Code != http.StatusUnprocessableEntity {
		t.Fatalf("devolveu %d", response.Code)
	}
	if len(fixture.objects.stored) != 0 {
		t.Fatal("um conteúdo recusado chegou ao armazenamento")
	}
	if fixture.pending(t, id, facts.TopicDocument) != 0 {
		t.Fatal("um conteúdo recusado virou document.stored")
	}
}

// TestTheUploadBelongsToWhoeverTheEnvelopeNames: aceitar um identificador do
// corpo deixaria qualquer piloto sobrescrever o documento de outro.
func TestTheUploadBelongsToWhoeverTheEnvelopeNames(t *testing.T) {
	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	theirs := fixture.lastID
	fixture.register(t, http.StatusCreated)
	mine := fixture.lastID

	response := fixture.send(fixture.upload(t, mine, "uma foto qualquer"))
	if response.Code != http.StatusOK {
		t.Fatalf("upload recusado: %d", response.Code)
	}

	var key *string
	if err := fixture.database.QueryRow(
		`SELECT cnh_object_key FROM riders WHERE user_id = $1`, theirs).Scan(&key); err != nil {
		t.Fatal(err)
	}
	if key != nil {
		t.Fatal("o upload de um piloto tocou o documento de outro")
	}
}

// ---------------------------------------------------------------- ferramentas

// pending conta as linhas que a transação deixou no outbox para o piloto.
func (f *fixture) pending(t *testing.T, riderID, topic string) int {
	t.Helper()
	var count int
	if err := f.database.QueryRow(
		`SELECT count(*) FROM outbox
		  WHERE aggregate_type = 'rider' AND aggregate_id = $1 AND topic = $2`,
		riderID, topic).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count
}

func (f *fixture) upload(t *testing.T, subject, content string) *http.Request {
	t.Helper()
	body := &bytes.Buffer{}
	form := multipart.NewWriter(body)
	part, err := form.CreateFormFile("cnhFile", "cnh.png")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := part.Write([]byte(content)); err != nil {
		t.Fatal(err)
	}
	if err := form.Close(); err != nil {
		t.Fatal(err)
	}

	request := f.envelope(t, http.MethodPut, "/update-image", subject, "Rider")
	replaced := httptest.NewRequest(http.MethodPut, "/update-image", body)
	replaced.Header = request.Header
	replaced.Header.Set("Content-Type", form.FormDataContentType())
	return replaced
}
