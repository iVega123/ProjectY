// Package sessions cuida da metade longa da sessão: o refresh token.
//
// Ele mora no CockroachDB, e não no Redis, pela pergunta que decidiu o ADR
// 0017: perder o Redis desloga todo mundo? Com os tokens no Redis, sim. O custo
// da escolha é uma escrita no banco por renovação, e o que ela compra é uma
// tabela de degradação que não precisa colocar "limitação de taxa degradada" e
// "toda sessão da plataforma destruída" na mesma linha.
//
// O token é opaco -- bytes aleatórios, sem nada assinado dentro. Ele não é
// verificado, é PROCURADO, e é essa diferença que permite revogá-lo de verdade.
//
// Cada linha guarda também o access token emitido com ela. Revogar a sessão
// devolve os que ainda podem estar em uso, para o portão passar a recusá-los
// nas operações de alto valor sem esperar os cinco minutos deles (#59).
package sessions

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/base64"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/dbx"
)

var (
	// ErrUnknown é token que não existe, expirou ou foi revogado.
	ErrUnknown = errors.New("refresh token desconhecido")
	// ErrReplayed é um token já consumido sendo reapresentado. Ver [Store.Rotate].
	ErrReplayed = errors.New("refresh token reapresentado")
)

// AccessToken é um access token emitido junto com um refresh token: o `jti` e
// quando ele deixa de valer.
type AccessToken struct {
	ID        string
	ExpiresAt time.Time
}

// Rotation é o que uma renovação produziu.
type Rotation struct {
	UserID string
	Next   string
	// Revoked são os access tokens da família revogada por reapresentação.
	// Vem preenchido junto com [ErrReplayed], e só nesse caso.
	Revoked []AccessToken
}

// Store guarda e consome refresh tokens.
type Store struct {
	db  *sql.DB
	ttl time.Duration
}

// NewStore prende o store à conexão e à validade dos tokens.
func NewStore(db *sql.DB, ttl time.Duration) *Store {
	return &Store{db: db, ttl: ttl}
}

// Issue abre uma sessão nova: família nova, token novo, e o access token que
// sai junto com ele.
func (s *Store) Issue(ctx context.Context, userID string, access AccessToken) (string, error) {
	token, hash, err := generate()
	if err != nil {
		return "", err
	}
	_, err = s.db.ExecContext(ctx,
		`INSERT INTO refresh_tokens
		        (user_id, token_hash, family_id, expires_at, access_token_id, access_expires_at)
		 VALUES ($1, $2, $3, $4, $5, $6)`,
		userID, hash, uuid.NewString(), time.Now().UTC().Add(s.ttl), access.ID, access.ExpiresAt.UTC())
	if err != nil {
		return "", err
	}
	return token, nil
}

// Rotate consome o token apresentado e emite o próximo da mesma família, com o
// access token que sai junto.
//
// # Por que um token de renovação só serve uma vez
//
// Se ele servisse sempre, roubá-lo daria ao ladrão sete dias de acesso sem
// deixar rastro -- as duas partes renovariam em paralelo, e nada distinguiria
// uma da outra. Consumindo na primeira renovação, a segunda apresentação do
// mesmo token é um fato: OU a vítima OU o ladrão está usando um token que já
// foi trocado.
//
// Não dá para saber qual dos dois, e é justamente por isso que a reação é
// revogar a família inteira. Quem for legítimo faz login de novo; quem roubou
// perde o acesso. Recusar só aquela chamada deixaria o ladrão renovando com o
// token seguinte.
//
// O `jti` do access token entra na linha nova antes de o dono ser conhecido: é
// a mesma escrita que abre a próxima renovação, e o token só é assinado depois.
func (s *Store) Rotate(ctx context.Context, presented string, access AccessToken) (Rotation, error) {
	hash := digest(presented)

	// A recusa NÃO viaja pelo retorno de erro da transação.
	//
	// Devolver ErrReplayed dali faria [dbx.InTransaction] desfazer a transação,
	// e junto com ela a revogação da família -- o ladrão receberia 401 nesta
	// chamada e continuaria renovando na seguinte. A transação precisa fechar;
	// o veredito vem por fora dela.
	var verdict error
	var rotation Rotation

	err := dbx.InTransaction(ctx, s.db, func(transaction *sql.Tx) error {
		verdict = nil
		rotation = Rotation{}

		var familyID string
		err := transaction.QueryRowContext(ctx,
			`UPDATE refresh_tokens
			    SET consumed_at = now()
			  WHERE token_hash = $1
			    AND consumed_at IS NULL
			    AND revoked_at IS NULL
			    AND expires_at > now()
			 RETURNING user_id, family_id`, hash).Scan(&rotation.UserID, &familyID)

		if errors.Is(err, sql.ErrNoRows) {
			rotation.Revoked, verdict, err = s.reject(ctx, transaction, hash)
			return err
		}
		if err != nil {
			return err
		}

		token, nextHash, err := generate()
		if err != nil {
			return err
		}
		if _, err := transaction.ExecContext(ctx,
			`INSERT INTO refresh_tokens
			        (user_id, token_hash, family_id, expires_at, access_token_id, access_expires_at)
			 VALUES ($1, $2, $3, $4, $5, $6)`,
			rotation.UserID, nextHash, familyID, time.Now().UTC().Add(s.ttl),
			access.ID, access.ExpiresAt.UTC(),
		); err != nil {
			return err
		}
		rotation.Next = token
		return nil
	})
	if err != nil {
		return Rotation{}, err
	}
	if verdict != nil {
		return Rotation{Revoked: rotation.Revoked}, verdict
	}
	return rotation, nil
}

// reject decide entre "não existe" e "já foi usado", e revoga a família no
// segundo caso. O veredito volta separado do erro justamente para a revogação
// poder ser confirmada.
func (s *Store) reject(
	ctx context.Context,
	transaction *sql.Tx,
	hash []byte,
) (revoked []AccessToken, verdict error, failure error) {
	var familyID string
	err := transaction.QueryRowContext(ctx,
		`SELECT family_id FROM refresh_tokens WHERE token_hash = $1 AND consumed_at IS NOT NULL`,
		hash).Scan(&familyID)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, ErrUnknown, nil
	}
	if err != nil {
		return nil, nil, err
	}
	rows, err := transaction.QueryContext(ctx,
		`UPDATE refresh_tokens SET revoked_at = now()
		  WHERE family_id = $1 AND revoked_at IS NULL
		 RETURNING access_token_id, access_expires_at`, familyID)
	if err != nil {
		return nil, nil, err
	}
	revoked, _, err = collect(rows)
	if err != nil {
		return nil, nil, err
	}
	return revoked, ErrReplayed, nil
}

// Revoke encerra a sessão a que o token pertence, e devolve os access tokens
// que ela emitiu.
//
// A família inteira, e não só o token apresentado: sair é encerrar a sessão, e
// a sessão é a família. Revogar uma folha deixaria a cadeia renovável a partir
// de qualquer token anterior que ainda estivesse por aí.
func (s *Store) Revoke(ctx context.Context, presented string) ([]AccessToken, error) {
	rows, err := s.db.QueryContext(ctx,
		`UPDATE refresh_tokens SET revoked_at = now()
		  WHERE revoked_at IS NULL
		    AND family_id = (SELECT family_id FROM refresh_tokens WHERE token_hash = $1)
		 RETURNING access_token_id, access_expires_at`,
		digest(presented))
	if err != nil {
		return nil, err
	}
	revoked, touched, err := collect(rows)
	if err != nil {
		return nil, err
	}
	if touched == 0 {
		return nil, ErrUnknown
	}
	return revoked, nil
}

// RevokeUser encerra todas as sessões de um usuário -- o caso de conta
// comprometida -- e devolve os access tokens delas.
func (s *Store) RevokeUser(ctx context.Context, userID string) ([]AccessToken, error) {
	rows, err := s.db.QueryContext(ctx,
		`UPDATE refresh_tokens SET revoked_at = now()
		  WHERE user_id = $1 AND revoked_at IS NULL
		 RETURNING access_token_id, access_expires_at`,
		userID)
	if err != nil {
		return nil, err
	}
	revoked, _, err := collect(rows)
	return revoked, err
}

// Revoked devolve os access tokens de sessões revogadas que expiram depois de
// `after`: o que ainda pode ser apresentado ao portão e precisa estar negado.
//
// É a fonte da denylist. O Redis guarda uma cópia disto, e esta consulta é o
// que a refaz quando a cópia se perde.
func (s *Store) Revoked(ctx context.Context, after time.Time) ([]AccessToken, error) {
	rows, err := s.db.QueryContext(ctx,
		`SELECT access_token_id, access_expires_at
		   FROM refresh_tokens
		  WHERE revoked_at IS NOT NULL
		    AND access_expires_at > $1`,
		after.UTC())
	if err != nil {
		return nil, err
	}
	revoked, _, err := collect(rows)
	return revoked, err
}

// Sweep apaga o que já não pode mais ser usado. Sem isso a tabela só cresce, e
// a detecção de reapresentação passa a varrer sete dias de tokens mortos.
func (s *Store) Sweep(ctx context.Context) (int64, error) {
	result, err := s.db.ExecContext(ctx,
		`DELETE FROM refresh_tokens WHERE expires_at < now()`)
	if err != nil {
		return 0, err
	}
	return result.RowsAffected()
}

// collect lê pares (jti, expiração) e conta as linhas. Uma linha sem `jti` é de
// antes da migração 007: ela conta como sessão tocada, e não tem o que negar.
func collect(rows *sql.Rows) (tokens []AccessToken, touched int, err error) {
	defer func() { _ = rows.Close() }()
	for rows.Next() {
		touched++
		var id sql.NullString
		var expiresAt sql.NullTime
		if err := rows.Scan(&id, &expiresAt); err != nil {
			return nil, 0, err
		}
		if id.Valid && expiresAt.Valid {
			tokens = append(tokens, AccessToken{ID: id.String, ExpiresAt: expiresAt.Time})
		}
	}
	return tokens, touched, rows.Err()
}

// generate sorteia o token e devolve junto o que vai para o banco.
//
// O banco recebe o SHA-256, nunca o token. Quem lê a tabela consegue, no
// máximo, invalidar sessões -- não assumi-las. Não há alongamento de chave aqui
// de propósito: o segredo tem 256 bits sorteados, e Argon2 sobre isso só
// tornaria a renovação cara sem tornar o token mais difícil de adivinhar.
func generate() (token string, hash []byte, err error) {
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		return "", nil, fmt.Errorf("sorteando refresh token: %w", err)
	}
	token = base64.RawURLEncoding.EncodeToString(raw)
	return token, digest(token), nil
}

func digest(token string) []byte {
	sum := sha256.Sum256([]byte(token))
	return sum[:]
}
