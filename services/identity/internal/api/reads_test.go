package api

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"testing"
)

// A declaração do consumidor, lida do arquivo em vez de repetida aqui.
//
// O #138 pede que remover um endpoint de leitura deixe um teste vermelho antes
// de deixar uma tela em branco. Isso só vale se o teste souber o que a tela
// pede, e a única forma de saber é ler a mesma declaração que o console lê.
// Repetir os nomes de campo aqui provaria que este arquivo concorda consigo
// mesmo.
type declaredRead struct {
	Name         string   `json:"name"`
	Provider     string   `json:"provider"`
	Method       string   `json:"method"`
	Path         string   `json:"path"`
	IDsParameter string   `json:"idsParameter"`
	Batched      bool     `json:"batched"`
	MaxBatch     int      `json:"maxBatch"`
	Fields       []string `json:"fields"`
}

func readContract(t *testing.T, provider, path string) declaredRead {
	t.Helper()
	raw, err := os.ReadFile(locateContract(t))
	if err != nil {
		t.Fatal(err)
	}
	var document struct {
		Reads []declaredRead `json:"reads"`
	}
	if err := json.Unmarshal(raw, &document); err != nil {
		t.Fatal(err)
	}
	for _, read := range document.Reads {
		if read.Provider == provider && read.Path == path {
			return read
		}
	}
	t.Fatalf("contracts/reads.json não declara leitura de %s em %s. "+
		"Se o console deixou de pedir, apague a rota; se ainda pede, restaure a declaração.",
		provider, path)
	return declaredRead{}
}

// Sobe até achar o arquivo. O diretório de trabalho de um teste Go é o do
// pacote, e um caminho relativo fixo quebra ao mover o pacote.
func locateContract(t *testing.T) string {
	t.Helper()
	directory, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		candidate := filepath.Join(directory, "contracts", "reads.json")
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
		parent := filepath.Dir(directory)
		if parent == directory {
			t.Fatal("contracts/reads.json não foi encontrado acima do pacote")
		}
		directory = parent
	}
}

// TestTheRiderReadAnswersWhatTheConsoleDeclared: a tela de aluguéis mostra o
// nome de quem está logado, e é esta rota que o diz.
func TestTheRiderReadAnswersWhatTheConsoleDeclared(t *testing.T) {
	declared := readContract(t, "identity", "/api/riders/{id}")
	if declared.Method != http.MethodGet {
		t.Fatalf("a declaração pede %s", declared.Method)
	}

	fixture := start(t)
	fixture.register(t, http.StatusCreated)
	id := fixture.lastID

	response := fixture.send(fixture.envelope(t, http.MethodGet, "/api/riders/"+id, id, "Rider"))
	if response.Code != http.StatusOK {
		t.Fatalf("leitura recusada: %d %s", response.Code, response.Body)
	}

	var found map[string]any
	decodeInto(t, response.Body.Bytes(), &found)
	for _, field := range declared.Fields {
		if _, present := found[field]; !present {
			t.Fatalf("contracts/reads.json declara %q, e a resposta não trouxe: %v", field, found)
		}
	}
}
