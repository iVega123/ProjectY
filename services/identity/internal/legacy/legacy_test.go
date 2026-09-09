package legacy

import (
	"context"
	"database/sql"
	"testing"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/passwords"
	"github.com/iVega123/ProjectY/services/identity/internal/testdb"
)

// O hash veio do PasswordHasher<T> do ASP.NET Identity, como o de
// internal/passwords. É o mesmo vetor, e de propósito: o que este teste prova é
// que o hash atravessa a importação intacto e continua servindo para entrar.
const (
	legacyPassword = "uma senha do AuthGate legado"
	legacyHash     = "AQAAAAIAAYagAAAAECObQOPfP4QYUIsusFXW1mHqpJaWJuZKOjMWdMODWx2OP/UelLwOz+7nxvQBeRNkQw=="
)

func TestImportBringsRidersWithTheirPasswordsAndIdentifiers(t *testing.T) {
	database := testdb.Open(t)
	source := seedAuthGate(t, database)

	report, err := Import(context.Background(), database, database)
	if err != nil {
		t.Fatalf("importando: %v", err)
	}
	if report.Imported < 2 {
		t.Fatalf("importou %d de 2: %v", report.Imported, report.Reasons)
	}

	// O identificador é preservado. Ele é o `sub` do token e é
	// `rentals.rider_id`: sortear um novo aqui separaria cada piloto do próprio
	// histórico de aluguéis.
	var hash, userType, name string
	if err := database.QueryRow(
		`SELECT password_hash, user_type, name FROM users WHERE id = $1`, source.riderID,
	).Scan(&hash, &userType, &name); err != nil {
		t.Fatalf("o piloto não chegou com o mesmo id: %v", err)
	}
	if userType != "rider" || name != "Ada Lovelace" {
		t.Fatalf("piloto importado errado: %s / %s", userType, name)
	}

	// A metade que importa: o hash atravessou intacto, e a verificação dupla o
	// lê. Sem isso, importar seria trancar todo mundo do lado de fora.
	ok, needsRehash, err := passwords.Verify(hash, legacyPassword)
	if err != nil || !ok || !needsRehash {
		t.Fatalf("a senha antiga não entra depois da importação: ok=%v rehash=%v err=%v",
			ok, needsRehash, err)
	}

	var cnhType, cnpj string
	if err := database.QueryRow(
		`SELECT cnh_type, cnpj FROM riders WHERE user_id = $1`, source.riderID,
	).Scan(&cnhType, &cnpj); err != nil {
		t.Fatalf("registro de piloto ausente: %v", err)
	}
	// CNHType é um inteiro no EF, e o mapeamento para o enum TipoCNH mora no
	// código do AuthGate, não no dado. Errar a ordem aqui trocaria o tipo de
	// habilitação de todo mundo em silêncio.
	if cnhType != "AB" || cnpj != "11444777000161" {
		t.Fatalf("documentos importados errados: %s / %s", cnhType, cnpj)
	}

	var role string
	if err := database.QueryRow(
		`SELECT role FROM user_roles WHERE user_id = $1`, source.riderID).Scan(&role); err != nil {
		t.Fatalf("papel não importado: %v", err)
	}
	if role != "Rider" {
		t.Fatalf("papel importado: %s", role)
	}
}

func TestImportCanBeRunAgain(t *testing.T) {
	database := testdb.Open(t)
	seedAuthGate(t, database)

	first, err := Import(context.Background(), database, database)
	if err != nil {
		t.Fatal(err)
	}
	// Uma migração que não se pode repetir é uma migração que não se pode
	// retomar depois de falhar no meio.
	second, err := Import(context.Background(), database, database)
	if err != nil {
		t.Fatalf("a segunda passagem falhou: %v", err)
	}
	if second.Imported != first.Imported {
		t.Fatalf("a segunda passagem divergiu: %d vs %d", second.Imported, first.Imported)
	}
}

// TestImportAnnouncesWhatItBrought: importar sem contar produz gente que entra
// e não aluga. O rental-core autoriza pela projeção local, ela só se preenche
// por evento, e um piloto que nunca foi anunciado não existe do lado de lá.
func TestImportAnnouncesWhatItBrought(t *testing.T) {
	database := testdb.Open(t)
	source := seedAuthGate(t, database)

	if _, err := Import(context.Background(), database, database); err != nil {
		t.Fatal(err)
	}

	for _, topic := range []string{"rider.registered", "rider.verified", "rider.verified.v2"} {
		if got := outboxCount(t, database, source.riderID, topic); got != 1 {
			t.Fatalf("%s saiu %d vez(es) do importador", topic, got)
		}
	}

	// E a linha concorda com o fato: CNH AB entra habilitada, como no cadastro.
	var verified bool
	if err := database.QueryRow(
		`SELECT verified FROM riders WHERE user_id = $1`, source.riderID).Scan(&verified); err != nil {
		t.Fatal(err)
	}
	if !verified {
		t.Fatal("um piloto com CNH AB chegou não verificado")
	}

	// Reexecutar não republica: a linha já está lá, e a passagem seguinte não
	// tem nada novo a contar.
	if _, err := Import(context.Background(), database, database); err != nil {
		t.Fatal(err)
	}
	if got := outboxCount(t, database, source.riderID, "rider.registered"); got != 1 {
		t.Fatalf("a segunda passagem republicou o cadastro: %d", got)
	}
}

func outboxCount(t *testing.T, database *sql.DB, riderID, topic string) int {
	t.Helper()
	var count int
	if err := database.QueryRow(
		`SELECT count(*) FROM outbox
		  WHERE aggregate_type = 'rider' AND aggregate_id = $1 AND topic = $2`,
		riderID, topic).Scan(&count); err != nil {
		t.Fatal(err)
	}
	return count
}

func TestImportReportsWhatItSkipped(t *testing.T) {
	database := testdb.Open(t)
	seedAuthGate(t, database)

	// Um usuário cujo identificador não é UUID não pode entrar: `users.id` é
	// UUID porque é o mesmo valor que `rentals.rider_id` carrega.
	if _, err := database.Exec(
		`INSERT INTO "AspNetUsers" ("Id", "Email", "Name", "PasswordHash", "Discriminator")
		 VALUES ('nao-e-um-uuid', 'quebrado@example.test', 'Quebrado', $1, 'AdminUser')`,
		legacyHash,
	); err != nil {
		t.Fatal(err)
	}

	report, err := Import(context.Background(), database, database)
	if err != nil {
		t.Fatal(err)
	}
	if report.Skipped != 1 || len(report.Reasons) != 1 {
		t.Fatalf("a linha inválida passou em silêncio: %+v", report)
	}
}

type seeded struct {
	riderID string
	adminID string
}

// seedAuthGate monta as tabelas do ASP.NET Identity no mesmo banco.
//
// Mesmo banco e não outro: o que este teste precisa provar é o MAPEAMENTO entre
// dois formatos, e ele é o mesmo esteja a origem noutro engine ou não. O
// subcomando de produção abre duas conexões; aqui as duas apontam para a mesma,
// e nenhuma linha é confundida porque os nomes das tabelas não colidem.
func seedAuthGate(t *testing.T, database *sql.DB) seeded {
	t.Helper()

	for _, statement := range []string{
		`CREATE TABLE IF NOT EXISTS "AspNetUsers" (
			"Id" TEXT PRIMARY KEY,
			"Discriminator" TEXT NOT NULL,
			"Email" TEXT,
			"Name" TEXT,
			"PasswordHash" TEXT,
			"CNPJ" TEXT,
			"DateOfBirth" TIMESTAMPTZ,
			"CNHNumber" TEXT,
			"CNHType" INT
		)`,
		`CREATE TABLE IF NOT EXISTS "AspNetRoles" ("Id" TEXT PRIMARY KEY, "Name" TEXT NOT NULL)`,
		`CREATE TABLE IF NOT EXISTS "AspNetUserRoles" (
			"UserId" TEXT NOT NULL, "RoleId" TEXT NOT NULL, PRIMARY KEY ("UserId", "RoleId"))`,
		`DELETE FROM "AspNetUserRoles"`,
		`DELETE FROM "AspNetUsers"`,
		`DELETE FROM "AspNetRoles"`,
	} {
		if _, err := database.Exec(statement); err != nil {
			t.Fatalf("preparando as tabelas legadas: %v", err)
		}
	}

	source := seeded{riderID: uuid.NewString(), adminID: uuid.NewString()}
	riderRole, adminRole := uuid.NewString(), uuid.NewString()

	execute(t, database,
		`INSERT INTO "AspNetRoles" ("Id", "Name") VALUES ($1, 'Rider'), ($2, 'Admin')`,
		riderRole, adminRole)
	execute(t, database,
		`INSERT INTO "AspNetUsers"
		   ("Id", "Discriminator", "Email", "Name", "PasswordHash",
		    "CNPJ", "DateOfBirth", "CNHNumber", "CNHType")
		 VALUES ($1, 'RiderUser', 'ada@example.test', 'Ada Lovelace', $2,
		         '11444777000161', '1990-01-31T00:00:00Z', '12345678901', 2)`,
		source.riderID, legacyHash)
	execute(t, database,
		`INSERT INTO "AspNetUsers" ("Id", "Discriminator", "Email", "Name", "PasswordHash")
		 VALUES ($1, 'AdminUser', 'admin@example.test', 'Administrador', $2)`,
		source.adminID, legacyHash)
	execute(t, database,
		`INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES ($1, $2), ($3, $4)`,
		source.riderID, riderRole, source.adminID, adminRole)

	// As linhas de destino de uma execução anterior deste teste sairiam do
	// caminho por ON CONFLICT, mas os índices únicos de e-mail e CNPJ pegariam
	// os identificadores novos. Limpar é mais honesto que reaproveitar.
	execute(t, database, `DELETE FROM users WHERE email IN ('ada@example.test', 'admin@example.test')`)

	return source
}

func execute(t *testing.T, database *sql.DB, statement string, arguments ...any) {
	t.Helper()
	if _, err := database.Exec(statement, arguments...); err != nil {
		t.Fatalf("%s: %v", statement, err)
	}
}
