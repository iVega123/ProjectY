// Package testdb dá aos testes um CockroachDB de verdade com o schema aplicado.
//
// De verdade, e não um duplo: metade do que este serviço promete é escrito em
// SQL -- o índice único parcial que decide qual réplica gera a primeira chave,
// o UPDATE ... RETURNING que consome um refresh token sem corrida, o CHECK que
// recusa um tipo de CNH inventado. Nada disso existe num repositório em memória,
// e um teste que passa contra um duplo prova só que o duplo concorda com o
// código.
package testdb

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"testing"
	"time"

	_ "github.com/jackc/pgx/v5/stdlib"
)

// EnvironmentVariable aponta para um CockroachDB já de pé, no formato
// `host:porta`.
//
// O CI sobe um container de serviço e preenche esta variável. Localmente,
// `docker run --rm -p 26257:26257 cockroachdb/cockroach:v26.3.1 start-single-node
// --insecure` e a mesma variável fazem o teste rodar igual.
const EnvironmentVariable = "IDENTITY_TEST_COCKROACH"

var (
	once     sync.Once
	shared   *sql.DB
	prepared error
)

// Open devolve a conexão compartilhada, aplicando o schema na primeira chamada.
//
// Um banco por binário de teste, e não um por teste: subir o CockroachDB custa
// segundos, e o isolamento que os testes precisam vem de cada um usar os
// próprios e-mails e identificadores.
func Open(t *testing.T) *sql.DB {
	t.Helper()

	endpoint := os.Getenv(EnvironmentVariable)
	if endpoint == "" {
		t.Skipf("defina %s=host:porta para rodar os testes que falam com o banco", EnvironmentVariable)
	}

	once.Do(func() { shared, prepared = prepare(endpoint) })
	if prepared != nil {
		t.Fatalf("preparando o banco de teste: %v", prepared)
	}
	return shared
}

func prepare(endpoint string) (*sql.DB, error) {
	root, err := connect(endpoint, "defaultdb")
	if err != nil {
		return nil, err
	}
	defer func() { _ = root.Close() }()

	if err := await(root); err != nil {
		return nil, err
	}
	if err := apply(root, "000_bootstrap.cockroach.sql"); err != nil {
		return nil, err
	}

	application, err := connect(endpoint, "projecty")
	if err != nil {
		return nil, err
	}
	for _, file := range []string{
		"001_schema.sql",
		"002_rental_core.sql",
		"003_billing.sql",
		"004_settlement_moves_to_billing.sql",
		"005_identity.sql",
	} {
		if err := apply(application, file); err != nil {
			_ = application.Close()
			return nil, err
		}
	}
	return application, nil
}

func connect(endpoint, database string) (*sql.DB, error) {
	// simple_protocol porque os arquivos de schema trazem várias instruções por
	// vez, e o protocolo estendido recusa isso.
	url := fmt.Sprintf(
		"postgres://root@%s/%s?sslmode=disable&default_query_exec_mode=simple_protocol",
		endpoint, database)
	return sql.Open("pgx", url)
}

func await(database *sql.DB) error {
	var err error
	for range 60 {
		if err = database.Ping(); err == nil {
			return nil
		}
		time.Sleep(time.Second)
	}
	return fmt.Errorf("o CockroachDB não respondeu: %w", err)
}

func apply(database *sql.DB, name string) error {
	path := filepath.Join(repositoryRoot(), "deploy", "db", "sql", name)
	statements, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	if _, err := database.Exec(string(statements)); err != nil {
		return fmt.Errorf("aplicando %s: %w", name, err)
	}
	return nil
}

func repositoryRoot() string {
	_, file, _, _ := runtime.Caller(0)
	return filepath.Join(filepath.Dir(file), "..", "..", "..", "..")
}
