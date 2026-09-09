// Package riders lê e escreve o registro regulatório do piloto, e conta ao
// resto da plataforma o que mudou.
//
// Toda escrita aqui grava o fato no outbox dentro da mesma transação. Não é
// zelo: o rental-core decide se alguém pode alugar pela projeção local que
// esses fatos alimentam, então uma escrita que não contasse deixaria a projeção
// respondendo o valor antigo para sempre -- inclusive "verificado" para um
// piloto que já não existe.
package riders

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/dbx"
	"github.com/iVega123/ProjectY/services/identity/internal/facts"
)

// MaxBatchSize é o teto do lote de leitura.
//
// Mesmo motivo do lote de notas do billing: sem teto, a consulta é escrita pelo
// cliente, e o #138 pede o endpoint em lote exatamente para o console não
// disparar uma chamada por linha da tela.
const MaxBatchSize = 100

// Consumer é o nome deste serviço no inbox compartilhado. É ele que separa dois
// consumidores da MESMA mensagem.
const Consumer = "identity-v1"

// ErrNotFound é ausência de piloto.
var ErrNotFound = errors.New("piloto não encontrado")

// Outcome diz o que aconteceu com uma mensagem consumida.
type Outcome int

const (
	// Applied: a mensagem foi tratada agora.
	Applied Outcome = iota
	// Duplicate: a mensagem já tinha sido tratada. O inbox reconheceu.
	Duplicate
	// Unknown: a mensagem fala de um piloto que este serviço não conhece.
	Unknown
)

// Rider é o registro como ele sai daqui.
type Rider struct {
	UserID       string
	Name         string
	Email        string
	CNPJ         string
	DateOfBirth  time.Time
	CNHNumber    string
	CNHType      string
	CNHObjectKey *string
	Verified     bool
}

// Store é o acesso às tabelas do piloto.
type Store struct {
	db     *sql.DB
	writer facts.Writer
}

// NewStore prende o store à conexão.
func NewStore(db *sql.DB) *Store {
	return &Store{db: db}
}

const selection = `SELECT r.user_id, u.name, u.email, r.cnpj, r.date_of_birth,
                          r.cnh_number, r.cnh_type, r.cnh_object_key, r.verified
                     FROM riders r
                     JOIN users u ON u.id = r.user_id`

// ByID devolve um piloto.
func (s *Store) ByID(ctx context.Context, id string) (Rider, error) {
	if _, err := uuid.Parse(id); err != nil {
		return Rider{}, ErrNotFound
	}
	rows, err := s.db.QueryContext(ctx, selection+` WHERE r.user_id = $1`, id)
	if err != nil {
		return Rider{}, err
	}
	found, err := scan(rows)
	if err != nil {
		return Rider{}, err
	}
	if len(found) == 0 {
		return Rider{}, ErrNotFound
	}
	return found[0], nil
}

// ByIDs devolve os pilotos pedidos, na ordem em que o banco os encontrar.
//
// Ausentes são omitidos em vez de virarem erro: o lote existe para o console
// montar uma tela, e uma linha cujo piloto sumiu não deve apagar a tela
// inteira.
func (s *Store) ByIDs(ctx context.Context, ids []string) ([]Rider, error) {
	if len(ids) == 0 || len(ids) > MaxBatchSize {
		return nil, fmt.Errorf("o lote precisa ter entre 1 e %d identificadores", MaxBatchSize)
	}
	for _, id := range ids {
		if _, err := uuid.Parse(id); err != nil {
			return nil, fmt.Errorf("identificador inválido: %q", id)
		}
	}
	// A lista de marcadores é montada, e os valores continuam ligados. O
	// tamanho já foi limitado por MaxBatchSize acima, então a consulta não
	// cresce com o que o cliente mandar -- e nenhum identificador entra na
	// string, o que mantém a única fronteira que importa aqui intacta.
	placeholders := make([]string, len(ids))
	arguments := make([]any, len(ids))
	for index, id := range ids {
		placeholders[index] = "$" + strconv.Itoa(index+1)
		arguments[index] = id
	}
	query := selection + " WHERE r.user_id IN (" + strings.Join(placeholders, ", ") + ")"

	rows, err := s.db.QueryContext(ctx, query, arguments...)
	if err != nil {
		return nil, err
	}
	return scan(rows)
}

// Delete apaga o piloto e conta que ele deixou de valer.
//
// Apagar a linha não basta. O rental-core autoriza pela projeção local, então
// sem um fato novo ela continuaria respondendo "verificado" para sempre e um
// piloto apagado seguiria alugando. O evento não inventa tópico nem campo: é o
// mesmo fato com `verified = false` e carimbo novo, e o upsert de
// mais-novo-vence da projeção derruba a linha sozinho.
func (s *Store) Delete(ctx context.Context, id string) error {
	if _, err := uuid.Parse(id); err != nil {
		return ErrNotFound
	}
	return dbx.InTransaction(ctx, s.db, func(transaction *sql.Tx) error {
		var rider facts.Rider
		err := transaction.QueryRowContext(ctx,
			`SELECT r.user_id, u.name, r.cnh_number, r.cnh_type
			   FROM riders r JOIN users u ON u.id = r.user_id
			  WHERE r.user_id = $1`, id,
		).Scan(&rider.ID, &rider.Name, &rider.CNHNumber, &rider.CNHType)
		if errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		}
		if err != nil {
			return err
		}

		if err := s.writer.Verified(
			ctx, transaction, rider, false, time.Now().UTC(), traceParent(ctx),
		); err != nil {
			return err
		}
		// A credencial vai junto. Deixar o usuário sem o registro de piloto
		// produziria uma conta que entra e não pode fazer nada, e o achado B10
		// já mostrou o que meio-piloto custa.
		_, err = transaction.ExecContext(ctx, `DELETE FROM users WHERE id = $1`, id)
		return err
	})
}

// AttachDocument aponta o piloto para o objeto que o media-guard guardou e
// conta que ele existe.
func (s *Store) AttachDocument(ctx context.Context, id, objectKey string) error {
	return dbx.InTransaction(ctx, s.db, func(transaction *sql.Tx) error {
		var rider facts.Rider
		err := transaction.QueryRowContext(ctx,
			`UPDATE riders SET cnh_object_key = $2, updated_at = now()
			  WHERE user_id = $1
			 RETURNING user_id, cnh_number, cnh_type`, id, objectKey,
		).Scan(&rider.ID, &rider.CNHNumber, &rider.CNHType)
		if errors.Is(err, sql.ErrNoRows) {
			return ErrNotFound
		}
		if err != nil {
			return err
		}
		if err := transaction.QueryRowContext(ctx,
			`SELECT name FROM users WHERE id = $1`, id).Scan(&rider.Name); err != nil {
			return err
		}
		// `document.stored` é o que acorda o OCR do risk-pricing, e ele carrega
		// o número que o piloto declarou -- é contra ele que o OCR cruza.
		return s.writer.DocumentStored(
			ctx, transaction, rider, objectKey, time.Now().UTC(), traceParent(ctx))
	})
}

// MarkVerified aplica o veredito do OCR e reconta o fato.
//
// O inbox, o registro e o outbox numa transação só. É a promessa do ADR 0009 na
// sua forma mais afiada: ou a mensagem foi marcada como tratada E o piloto foi
// atualizado E o fato foi enfileirado, ou nada disso aconteceu.
//
// O veredito só pode DERRUBAR. Um documento que não confere revoga; um que
// confere devolve o que o tipo de CNH já dizia. Deixar o OCR conceder faria um
// piloto com habilitação categoria B passar a alugar por ter mandado uma foto
// legível.
func (s *Store) MarkVerified(
	ctx context.Context,
	messageID, riderID string,
	matched bool,
	occurredAt time.Time,
) (Outcome, error) {
	outcome := Applied
	err := dbx.InTransaction(ctx, s.db, func(transaction *sql.Tx) error {
		outcome = Applied

		claimed, err := claim(ctx, transaction, messageID)
		if err != nil {
			return err
		}
		if !claimed {
			outcome = Duplicate
			return nil
		}

		var rider facts.Rider
		err = transaction.QueryRowContext(ctx,
			`SELECT r.user_id, u.name, r.cnh_number, r.cnh_type
			   FROM riders r JOIN users u ON u.id = r.user_id
			  WHERE r.user_id = $1`, riderID,
		).Scan(&rider.ID, &rider.Name, &rider.CNHNumber, &rider.CNHType)
		if errors.Is(err, sql.ErrNoRows) {
			// A mensagem fica marcada como tratada: reprocessá-la não traria o
			// piloto de volta, e o offset precisa avançar.
			outcome = Unknown
			return nil
		}
		if err != nil {
			return err
		}

		verified := matched && facts.Entitled(rider.CNHType)
		if _, err := transaction.ExecContext(ctx,
			`UPDATE riders SET verified = $2, updated_at = now() WHERE user_id = $1`,
			riderID, verified,
		); err != nil {
			return err
		}
		return s.writer.Verified(ctx, transaction, rider, verified, occurredAt, traceParent(ctx))
	})
	return outcome, err
}

// RegisterFacts é o que a transação do cadastro grava: o piloto existe, e o que
// o tipo de CNH já permite dizer sobre ele.
func (s *Store) RegisterFacts(
	ctx context.Context,
	transaction *sql.Tx,
	rider facts.Rider,
) error {
	now := time.Now().UTC()
	trace := traceParent(ctx)
	if err := s.writer.Registered(ctx, transaction, rider, now, trace); err != nil {
		return err
	}
	return s.writer.Verified(ctx, transaction, rider, facts.Entitled(rider.CNHType), now, trace)
}

func claim(ctx context.Context, transaction *sql.Tx, messageID string) (bool, error) {
	result, err := transaction.ExecContext(ctx,
		`INSERT INTO inbox (message_id, consumer) VALUES ($1, $2) ON CONFLICT DO NOTHING`,
		messageID, Consumer)
	if err != nil {
		return false, err
	}
	affected, err := result.RowsAffected()
	return affected == 1, err
}

func scan(rows *sql.Rows) ([]Rider, error) {
	defer func() { _ = rows.Close() }()
	found := []Rider{}
	for rows.Next() {
		var rider Rider
		if err := rows.Scan(
			&rider.UserID, &rider.Name, &rider.Email, &rider.CNPJ, &rider.DateOfBirth,
			&rider.CNHNumber, &rider.CNHType, &rider.CNHObjectKey, &rider.Verified,
		); err != nil {
			return nil, err
		}
		found = append(found, rider)
	}
	return found, rows.Err()
}

// ParseIDs quebra o parâmetro `ids=a,b,c` e recusa o que não cabe.
func ParseIDs(raw string) ([]string, error) {
	if strings.TrimSpace(raw) == "" {
		return nil, errors.New("informe ao menos um identificador")
	}
	parts := strings.Split(raw, ",")
	ids := make([]string, 0, len(parts))
	for _, part := range parts {
		trimmed := strings.TrimSpace(part)
		if trimmed == "" {
			continue
		}
		if _, err := uuid.Parse(trimmed); err != nil {
			return nil, fmt.Errorf("identificador inválido: %q", trimmed)
		}
		ids = append(ids, trimmed)
	}
	if len(ids) == 0 {
		return nil, errors.New("informe ao menos um identificador")
	}
	if len(ids) > MaxBatchSize {
		return nil, fmt.Errorf("o lote aceita no máximo %d identificadores", MaxBatchSize)
	}
	return ids, nil
}

type traceKey struct{}

// WithTraceParent guarda o traceparent da requisição para o fato carregá-lo.
// Sem isso o trace termina na borda e o evento aparece no Tempo como se
// tivesse nascido sozinho.
func WithTraceParent(ctx context.Context, value string) context.Context {
	return context.WithValue(ctx, traceKey{}, value)
}

func traceParent(ctx context.Context) string {
	value, _ := ctx.Value(traceKey{}).(string)
	return value
}
