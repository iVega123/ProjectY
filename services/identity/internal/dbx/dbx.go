// Package dbx guarda o pouco que todo acesso ao CockroachDB deste serviço
// precisa saber: quais SQLSTATE importam, e como repetir uma transação que o
// banco desistiu de serializar.
package dbx

import (
	"context"
	"database/sql"
	"errors"
	"time"

	"github.com/jackc/pgx/v5/pgconn"
)

const (
	uniqueViolation      = "23505"
	serializationFailure = "40001"
)

// IsUniqueViolation diz se o erro é uma violação de índice único.
//
// Neste serviço isso quase nunca é falha: é o banco decidindo uma corrida que a
// aplicação não precisou coordenar -- dois cadastros no mesmo e-mail, duas
// réplicas gerando a primeira chave de assinatura.
func IsUniqueViolation(err error) bool {
	return sqlState(err) == uniqueViolation
}

// IsSerializationFailure diz se o CockroachDB abortou a transação para manter
// SERIALIZABLE.
func IsSerializationFailure(err error) bool {
	return sqlState(err) == serializationFailure
}

// ConstraintName diz qual restrição o banco recusou, ou string vazia.
//
// Sem isso, "e-mail já cadastrado" e "CNPJ já cadastrado" chegam ao usuário
// como a mesma mensagem genérica -- e quem está cadastrando não tem como saber
// qual dos dois campos corrigir.
func ConstraintName(err error) string {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.ConstraintName
	}
	return ""
}

func sqlState(err error) string {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.Code
	}
	return ""
}

// InTransaction roda o corpo numa transação, repetindo quando -- e somente
// quando -- o banco pediu.
//
// SERIALIZABLE recusa transações que não consegue ordenar, e a recusa é
// esperada, não excepcional. Repetir qualquer outro erro seria repetir uma
// senha errada ou um e-mail duplicado, que não melhoram na segunda tentativa.
func InTransaction(ctx context.Context, db *sql.DB, body func(*sql.Tx) error) error {
	const attempts = 5

	var err error
	for attempt := range attempts {
		if attempt > 0 {
			// Espera curta e crescente: duas transações que colidiram e
			// repetem no mesmo instante colidem de novo.
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(time.Duration(attempt) * 20 * time.Millisecond):
			}
		}

		var transaction *sql.Tx
		transaction, err = db.BeginTx(ctx, nil)
		if err != nil {
			return err
		}
		err = body(transaction)
		if err != nil {
			_ = transaction.Rollback()
			if IsSerializationFailure(err) {
				continue
			}
			return err
		}
		if err = transaction.Commit(); err != nil {
			if IsSerializationFailure(err) {
				continue
			}
			return err
		}
		return nil
	}
	return err
}
