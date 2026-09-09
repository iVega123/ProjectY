// Package accounts guarda quem existe: a credencial, os papéis e -- para
// pilotos -- o registro regulatório.
//
// Credencial e registro moram em tabelas separadas de propósito, pelo motivo
// que o ADR 0023 registra: são ciclos de vida diferentes, e o SELECT do login
// não tem por que tocar em número de CNH.
package accounts

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/dbx"
)

// Tipos de usuário, como o CHECK de `users.user_type` os conhece.
const (
	TypeAdmin = "admin"
	TypeRider = "rider"
)

// Papéis. São os mesmos nomes que o portão compara ao decidir Access::Admin, e
// o acordo entre os dois é por string -- vale escrevê-los uma vez só.
const (
	RoleAdmin = "Admin"
	RoleRider = "Rider"
)

var (
	// ErrNotFound é ausência, e o chamador precisa tratá-la como ausência: um
	// login que responde diferente para "não existe" e "senha errada" conta
	// quais e-mails estão cadastrados.
	ErrNotFound = errors.New("usuário não encontrado")
	// ErrEmailTaken e ErrCnpjTaken vêm do banco, não de uma consulta prévia.
	// Verificar antes de inserir deixa uma janela entre a verificação e a
	// escrita; o índice único não deixa.
	ErrEmailTaken = errors.New("e-mail já cadastrado")
	ErrCnpjTaken  = errors.New("CNPJ já cadastrado")
)

// User é a credencial e os papéis.
type User struct {
	ID           string
	Email        string
	Name         string
	PasswordHash string
	Type         string
	Roles        []string
}

// Rider é o registro regulatório, sempre preso a um usuário.
type Rider struct {
	UserID       string
	CNPJ         string
	DateOfBirth  time.Time
	CNHNumber    string
	CNHType      string
	CNHObjectKey *string
	Verified     bool
}

// Registration é um cadastro de piloto pronto para gravar: validado, com a
// senha já derivada e o CNPJ já normalizado.
type Registration struct {
	Email        string
	Name         string
	PasswordHash string
	CNPJ         string
	DateOfBirth  time.Time
	CNHNumber    string
	CNHType      string
}

// Store é o acesso às tabelas de identidade.
type Store struct {
	db *sql.DB
}

// NewStore prende o store à conexão.
func NewStore(db *sql.DB) *Store {
	return &Store{db: db}
}

// Normalize devolve a forma canônica do e-mail -- a que decide unicidade.
func Normalize(email string) string {
	return strings.ToLower(strings.TrimSpace(email))
}

// RegisterRider grava usuário, papel e registro do piloto numa transação.
//
// Uma transação e não três escritas: um usuário sem papel não consegue fazer
// nada, e um usuário sem registro de piloto é um piloto que o risk-pricing não
// consegue cruzar com a CNH que o OCR leu. Meio cadastro é pior que nenhum,
// porque parece completo.
func (s *Store) RegisterRider(ctx context.Context, registration Registration) (User, error) {
	user := User{
		Email:        strings.TrimSpace(registration.Email),
		Name:         registration.Name,
		PasswordHash: registration.PasswordHash,
		Type:         TypeRider,
		Roles:        []string{RoleRider},
	}

	err := dbx.InTransaction(ctx, s.db, func(transaction *sql.Tx) error {
		row := transaction.QueryRowContext(ctx,
			`INSERT INTO users (email, email_normalized, password_hash, name, user_type)
			 VALUES ($1, $2, $3, $4, $5)
			 RETURNING id`,
			user.Email, Normalize(user.Email), user.PasswordHash, user.Name, user.Type)
		if err := row.Scan(&user.ID); err != nil {
			return err
		}
		if _, err := transaction.ExecContext(ctx,
			`INSERT INTO user_roles (user_id, role) VALUES ($1, $2)`,
			user.ID, RoleRider,
		); err != nil {
			return err
		}
		_, err := transaction.ExecContext(ctx,
			`INSERT INTO riders (user_id, cnpj, date_of_birth, cnh_number, cnh_type)
			 VALUES ($1, $2, $3, $4, $5)`,
			user.ID, registration.CNPJ, registration.DateOfBirth,
			registration.CNHNumber, registration.CNHType)
		return err
	})
	if err != nil {
		return User{}, translate(err)
	}
	return user, nil
}

// EnsureAdmin cria o administrador da instalação se ele ainda não existir.
//
// Idempotente de propósito: o processo roda isto em toda subida, e uma stack
// que reinicia não pode nem falhar nem regravar a senha de quem já entrou.
func (s *Store) EnsureAdmin(ctx context.Context, email, name, passwordHash string) error {
	return dbx.InTransaction(ctx, s.db, func(transaction *sql.Tx) error {
		var id string
		err := transaction.QueryRowContext(ctx,
			`INSERT INTO users (email, email_normalized, password_hash, name, user_type)
			 VALUES ($1, $2, $3, $4, $5)
			 ON CONFLICT (email_normalized) DO NOTHING
			 RETURNING id`,
			strings.TrimSpace(email), Normalize(email), passwordHash, name, TypeAdmin,
		).Scan(&id)
		if errors.Is(err, sql.ErrNoRows) {
			return nil
		}
		if err != nil {
			return err
		}
		_, err = transaction.ExecContext(ctx,
			`INSERT INTO user_roles (user_id, role) VALUES ($1, $2)`, id, RoleAdmin)
		return err
	})
}

// FindByEmail busca pela forma normalizada.
func (s *Store) FindByEmail(ctx context.Context, email string) (User, error) {
	return s.find(ctx, `email_normalized = $1`, Normalize(email))
}

// FindByID busca pelo identificador, que é o `sub` do token.
func (s *Store) FindByID(ctx context.Context, id string) (User, error) {
	return s.find(ctx, `id = $1`, id)
}

func (s *Store) find(ctx context.Context, where string, argument any) (User, error) {
	var user User
	query := fmt.Sprintf(
		`SELECT id, email, name, password_hash, user_type FROM users WHERE %s`, where)
	err := s.db.QueryRowContext(ctx, query, argument).
		Scan(&user.ID, &user.Email, &user.Name, &user.PasswordHash, &user.Type)
	if errors.Is(err, sql.ErrNoRows) {
		return User{}, ErrNotFound
	}
	if err != nil {
		return User{}, err
	}

	rows, err := s.db.QueryContext(ctx,
		`SELECT role FROM user_roles WHERE user_id = $1 ORDER BY role`, user.ID)
	if err != nil {
		return User{}, err
	}
	defer func() { _ = rows.Close() }()
	for rows.Next() {
		var role string
		if err := rows.Scan(&role); err != nil {
			return User{}, err
		}
		user.Roles = append(user.Roles, role)
	}
	return user, rows.Err()
}

// ReplacePasswordHash regrava o hash. É o segundo passo da verificação dupla:
// a senha estava certa, o formato era o antigo, e este usuário não passa mais
// pelo leitor de PBKDF2.
func (s *Store) ReplacePasswordHash(ctx context.Context, id, hash string) error {
	_, err := s.db.ExecContext(ctx,
		`UPDATE users SET password_hash = $1, updated_at = now() WHERE id = $2`, hash, id)
	return err
}

func translate(err error) error {
	if !dbx.IsUniqueViolation(err) {
		return err
	}
	switch dbx.ConstraintName(err) {
	case "one_user_per_email":
		return ErrEmailTaken
	case "one_rider_per_cnpj":
		return ErrCnpjTaken
	default:
		return err
	}
}
