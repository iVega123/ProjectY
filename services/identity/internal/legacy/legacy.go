// Package legacy traz os usuários do AuthGate para as tabelas do identity.
//
// # Por que isto existe
//
// O ADR 0012 diz, entre as consequências da reescrita: "a migração de senhas é
// o trabalho de verdade, não a reescrita". O AuthGate guarda PBKDF2 no formato
// do ASP.NET Identity, e sem um caminho de leitura para aquele formato todo
// usuário existente fica trancado do lado de fora.
//
// A escolha registrada é verificação dupla, e ela tem duas metades. A primeira
// mora em [passwords.Verify], que lê o formato antigo e regrava em Argon2id no
// primeiro login. A segunda é esta: as linhas precisam CHEGAR aqui, com o hash
// intacto, para que aquela primeira metade tenha o que ler.
//
// O importador copia o hash como está. Ele não pode fazer outra coisa -- a
// senha em claro não existe em lugar nenhum -- e é exatamente por isso que a
// verificação dupla é o único caminho que não força todo mundo a redefinir
// senha.
package legacy

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/accounts"
	"github.com/iVega123/ProjectY/services/identity/internal/dbx"
	"github.com/iVega123/ProjectY/services/identity/internal/facts"
)

// Report conta o que a passagem fez.
type Report struct {
	Imported int
	Skipped  int
	// Reasons descreve cada linha pulada. Uma importação que pula em silêncio é
	// uma migração que parece ter dado certo.
	Reasons []string
}

type legacyUser struct {
	id            string
	email         string
	name          string
	passwordHash  string
	discriminator string
	cnpj          sql.NullString
	dateOfBirth   sql.NullTime
	cnhNumber     sql.NullString
	cnhType       sql.NullInt64
}

// Import copia usuários, papéis e registros de piloto do AuthGate.
//
// Reexecutável: nada é sobrescrito, e uma linha que já chegou é pulada. Uma
// migração que não se pode repetir é uma migração que não se pode retomar
// depois de uma falha no meio.
func Import(ctx context.Context, source, target *sql.DB) (Report, error) {
	users, err := readUsers(ctx, source)
	if err != nil {
		return Report{}, err
	}
	roles, err := readRoles(ctx, source)
	if err != nil {
		return Report{}, err
	}

	report := Report{}
	for _, user := range users {
		if err := importOne(ctx, target, user, roles[user.id]); err != nil {
			report.Skipped++
			report.Reasons = append(report.Reasons, fmt.Sprintf("%s: %v", user.id, err))
			continue
		}
		report.Imported++
	}
	return report, nil
}

func readUsers(ctx context.Context, source *sql.DB) ([]legacyUser, error) {
	rows, err := source.QueryContext(ctx,
		`SELECT "Id", "Email", "Name", "PasswordHash", "Discriminator",
		        "CNPJ", "DateOfBirth", "CNHNumber", "CNHType"
		   FROM "AspNetUsers"`)
	if err != nil {
		return nil, fmt.Errorf("lendo AspNetUsers: %w", err)
	}
	defer func() { _ = rows.Close() }()

	var users []legacyUser
	for rows.Next() {
		var user legacyUser
		var email, name, hash sql.NullString
		if err := rows.Scan(
			&user.id, &email, &name, &hash, &user.discriminator,
			&user.cnpj, &user.dateOfBirth, &user.cnhNumber, &user.cnhType,
		); err != nil {
			return nil, err
		}
		user.email, user.name, user.passwordHash = email.String, name.String, hash.String
		users = append(users, user)
	}
	return users, rows.Err()
}

func readRoles(ctx context.Context, source *sql.DB) (map[string][]string, error) {
	rows, err := source.QueryContext(ctx,
		`SELECT link."UserId", role."Name"
		   FROM "AspNetUserRoles" link
		   JOIN "AspNetRoles" role ON role."Id" = link."RoleId"`)
	if err != nil {
		return nil, fmt.Errorf("lendo AspNetUserRoles: %w", err)
	}
	defer func() { _ = rows.Close() }()

	roles := map[string][]string{}
	for rows.Next() {
		var userID, role string
		if err := rows.Scan(&userID, &role); err != nil {
			return nil, err
		}
		roles[userID] = append(roles[userID], role)
	}
	return roles, rows.Err()
}

func importOne(ctx context.Context, target *sql.DB, user legacyUser, roles []string) error {
	// O identificador é preservado, e não sorteado de novo. Ele é o `sub` do
	// token, é `rentals.rider_id` e é a chave de partição do Cassandra: gerar
	// um novo aqui separaria cada piloto do próprio histórico.
	id, err := uuid.Parse(user.id)
	if err != nil {
		return fmt.Errorf("identificador não é um UUID")
	}
	if strings.TrimSpace(user.email) == "" || strings.TrimSpace(user.passwordHash) == "" {
		return fmt.Errorf("sem e-mail ou sem hash de senha")
	}

	userType := accounts.TypeAdmin
	if user.discriminator == "RiderUser" {
		userType = accounts.TypeRider
	}
	name := user.name
	if strings.TrimSpace(name) == "" {
		name = user.email
	}

	return dbx.InTransaction(ctx, target, func(transaction *sql.Tx) error {
		if _, err := transaction.ExecContext(ctx,
			`INSERT INTO users (id, email, email_normalized, password_hash, name, user_type)
			 VALUES ($1, $2, $3, $4, $5, $6)
			 ON CONFLICT DO NOTHING`,
			id, user.email, accounts.Normalize(user.email), user.passwordHash, name, userType,
		); err != nil {
			return err
		}
		for _, role := range roles {
			if _, err := transaction.ExecContext(ctx,
				`INSERT INTO user_roles (user_id, role) VALUES ($1, $2) ON CONFLICT DO NOTHING`,
				id, role,
			); err != nil {
				return err
			}
		}
		if userType != accounts.TypeRider {
			return nil
		}
		if !user.cnpj.Valid || !user.cnhNumber.Valid || !user.cnhType.Valid || !user.dateOfBirth.Valid {
			return fmt.Errorf("piloto sem documentos completos")
		}
		cnhType, err := decodeCnhType(user.cnhType.Int64)
		if err != nil {
			return err
		}
		rider := facts.Rider{
			ID: id.String(), Name: name,
			CNHNumber: user.cnhNumber.String, CNHType: cnhType,
		}
		// `verified` nasce do tipo de CNH, como no cadastro. Deixá-lo no padrão
		// faria a linha e o fato discordarem sobre o mesmo piloto -- e o fato é
		// o que o rental-core lê.
		verified := facts.Entitled(cnhType)

		var landed string
		err = transaction.QueryRowContext(ctx,
			`INSERT INTO riders (user_id, cnpj, date_of_birth, cnh_number, cnh_type, verified)
			 VALUES ($1, $2, $3, $4, $5, $6)
			 ON CONFLICT DO NOTHING
			 RETURNING user_id`,
			id, user.cnpj.String, user.dateOfBirth.Time.UTC().Truncate(24*time.Hour),
			user.cnhNumber.String, cnhType, verified,
		).Scan(&landed)
		if errors.Is(err, sql.ErrNoRows) {
			// A linha já estava aqui: a passagem anterior já contou. Repetir os
			// fatos seria republicar cadastro a cada reexecução.
			return nil
		}
		if err != nil {
			return err
		}

		// Os mesmos fatos que o cadastro grava, na mesma transação que a linha.
		//
		// Sem isto, a importação produz gente que entra e não aluga: o
		// rental-core autoriza pela projeção local, ela só se preenche por
		// evento, e um piloto que nunca foi anunciado é um piloto que não
		// existe do lado de lá. O relay publica assim que houver broker.
		writer := facts.Writer{}
		occurredAt := time.Now().UTC()
		if err := writer.Registered(ctx, transaction, rider, occurredAt, ""); err != nil {
			return err
		}
		return writer.Verified(ctx, transaction, rider, verified, occurredAt, "")
	})
}

// decodeCnhType traduz o inteiro que o EF gravou para o enum TipoCNH.
//
// A ordem é a da declaração em AuthGate.Model.TipoCNH, e ela é a única fonte
// desse mapeamento -- um enum gravado como inteiro é um formato cuja chave mora
// noutro repositório de código, e não no dado.
func decodeCnhType(value int64) (string, error) {
	switch value {
	case 0:
		return "A", nil
	case 1:
		return "B", nil
	case 2:
		return "AB", nil
	default:
		return "", fmt.Errorf("tipo de CNH desconhecido: %d", value)
	}
}
