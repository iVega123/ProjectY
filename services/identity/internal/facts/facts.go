// Package facts monta o que o identity conta ao resto da plataforma, e grava
// no outbox.
//
// Grava, e não publica. A linha do outbox entra na MESMA transação do cadastro,
// e uma relay separada a envia ao Kafka -- é o padrão do ADR 0009, e é o que
// impede o par "gravou mas não contou" e o par "contou mas não gravou".
//
// Os fatos aqui são os mesmos que o RiderManager contava, byte a byte no mesmo
// contrato. Um consumidor não deve conseguir notar que o produtor mudou de
// serviço e de linguagem.
package facts

import (
	"context"
	"database/sql"
	"fmt"
	"time"

	"github.com/google/uuid"
	"google.golang.org/protobuf/proto"

	"github.com/iVega123/ProjectY/services/identity/internal/events"
)

// Os tópicos que este serviço produz.
const (
	TopicRegistered = "rider.registered"
	TopicVerified   = "rider.verified"
	// TopicVerifiedV2 existe porque o Apicurio, sob FULL, recusa acrescentar um
	// campo a um schema governado -- a passagem para trás lê o campo novo como
	// remoção não reservada. O fato evoluído ganha um subject próprio, que não
	// tem versão anterior com que ser incompatível.
	TopicVerifiedV2  = "rider.verified.v2"
	TopicDocument    = "document.stored"
	aggregateType    = "rider"
	traceParentField = "traceparent"
)

// Rider é o pouco que um fato precisa saber sobre o piloto.
type Rider struct {
	ID        string
	Name      string
	CNHNumber string
	CNHType   string
}

// Entitled é o que "verificado" significa para quem consome.
//
// A regra mora com o dono do fato, e não com quem decide o aluguel: o
// rental-core lê a resposta da projeção local em vez de recalculá-la, que é o
// que tira o identity do caminho da requisição. Portar a regra errada para cá
// mudaria em silêncio quem pode alugar.
func Entitled(cnhType string) bool {
	return cnhType == "A" || cnhType == "AB"
}

// Writer grava fatos no outbox compartilhado.
//
// A tabela é a mesma do rental-core e do billing. `aggregate_type` é o que
// separa quem publica o quê: cada relay lê só as próprias linhas.
type Writer struct{}

// Registered é o cadastro do piloto, contado uma vez.
func (w Writer) Registered(
	ctx context.Context,
	transaction *sql.Tx,
	rider Rider,
	occurredAt time.Time,
	traceParent string,
) error {
	id := uuid.NewString()
	payload := &events.RiderEvent{
		EventId:      proto.String(id),
		RiderId:      proto.String(rider.ID),
		CnhNumber:    proto.String(rider.CNHNumber),
		OccurredAtMs: proto.Int64(occurredAt.UnixMilli()),
	}
	return write(ctx, transaction, TopicRegistered, rider.ID, payload, traceParent)
}

// Verified conta o veredito nos dois tópicos.
//
// Os dois, e não só o v2: o v1 continua na fita para quem ainda não migrou, e
// aposentá-lo antes disso deixaria consumidores em silêncio -- que é a pior
// forma de quebrar um contrato, porque nada falha.
func (w Writer) Verified(
	ctx context.Context,
	transaction *sql.Tx,
	rider Rider,
	verified bool,
	occurredAt time.Time,
	traceParent string,
) error {
	legacyID := uuid.NewString()
	legacy := &events.RiderEvent{
		EventId:      proto.String(legacyID),
		RiderId:      proto.String(rider.ID),
		CnhNumber:    proto.String(rider.CNHNumber),
		Verified:     proto.Bool(verified),
		OccurredAtMs: proto.Int64(occurredAt.UnixMilli()),
	}
	if err := write(ctx, transaction, TopicVerified, rider.ID, legacy, traceParent); err != nil {
		return err
	}

	currentID := uuid.NewString()
	current := &events.RiderEventV2{
		EventId:      proto.String(currentID),
		RiderId:      proto.String(rider.ID),
		Name:         proto.String(rider.Name),
		CnhNumber:    proto.String(rider.CNHNumber),
		Verified:     proto.Bool(verified),
		OccurredAtMs: proto.Int64(occurredAt.UnixMilli()),
	}
	return write(ctx, transaction, TopicVerifiedV2, rider.ID, current, traceParent)
}

// DocumentStored diz que a CNH foi armazenada e onde. É o que acorda o OCR do
// risk-pricing.
func (w Writer) DocumentStored(
	ctx context.Context,
	transaction *sql.Tx,
	rider Rider,
	objectKey string,
	occurredAt time.Time,
	traceParent string,
) error {
	id := uuid.NewString()
	payload := &events.RiderEvent{
		EventId:      proto.String(id),
		RiderId:      proto.String(rider.ID),
		ObjectKey:    proto.String(objectKey),
		CnhNumber:    proto.String(rider.CNHNumber),
		OccurredAtMs: proto.Int64(occurredAt.UnixMilli()),
	}
	return write(ctx, transaction, TopicDocument, rider.ID, payload, traceParent)
}

func write(
	ctx context.Context,
	transaction *sql.Tx,
	topic, riderID string,
	payload proto.Message,
	traceParent string,
) error {
	encoded, err := proto.Marshal(payload)
	if err != nil {
		return fmt.Errorf("serializando %s: %w", topic, err)
	}
	var trace any
	if traceParent != "" {
		trace = traceParent
	}
	_, err = transaction.ExecContext(ctx,
		`INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, trace_parent)
		 VALUES ($1, $2, $3, $4, $5, $6)`,
		aggregateType, riderID, topic, topic, encoded, trace)
	return err
}
