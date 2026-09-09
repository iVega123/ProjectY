// Package kafka é o transporte dos fatos do identity: a relay que esvazia o
// outbox e o consumidor que ouve o veredito do OCR.
//
// Transporte, e só. As decisões -- o que é um fato, o que ele significa, quando
// ele muda -- moram em `internal/facts` e `internal/riders`. O que está aqui
// pode falhar e ser repetido sem mudar nada do que foi decidido, e é essa
// separação que torna a repetição segura.
package kafka

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"time"

	"github.com/twmb/franz-go/pkg/kgo"
	"google.golang.org/protobuf/proto"

	"github.com/iVega123/ProjectY/services/identity/internal/events"
	"github.com/iVega123/ProjectY/services/identity/internal/riders"
)

// TopicDocumentVerified é o que o risk-pricing publica depois de cruzar o
// número lido por OCR com o que o piloto declarou.
const TopicDocumentVerified = "document.verified"

// ------------------------------------------------------------------ registry

// Registry resolve o id de schema que acompanha cada mensagem no header
// `schema-id`.
//
// O #132 decidiu Protobuf cru com o id num header, e não o envelope binário do
// Confluent: quem consome decodifica localmente e a mensagem continua legível
// sem o registry no caminho. O registry serve para uma coisa só -- recusar um
// contrato incompatível no momento do registro.
type Registry struct {
	baseURL   string
	contracts string
	client    *http.Client
	resolved  sync.Map
}

// NewRegistry prende o cliente ao registry e à cópia dos contratos na imagem.
func NewRegistry(baseURL, contracts string) *Registry {
	return &Registry{
		baseURL:   baseURL,
		contracts: contracts,
		client:    &http.Client{Timeout: 5 * time.Second},
	}
}

// Resolve devolve o id do schema do tópico, uma vez por tópico.
//
// Uma vez, e em memória: um registry fora do ar depois da primeira resolução
// não segura nenhuma linha do outbox.
func (r *Registry) Resolve(ctx context.Context, topic string) (int, error) {
	if cached, found := r.resolved.Load(topic); found {
		return cached.(int), nil
	}

	declared, err := os.ReadFile(filepath.Join(r.contracts, "topics.json"))
	if err != nil {
		return 0, err
	}
	var topics map[string]struct {
		Schema string `json:"schema"`
	}
	if err := json.Unmarshal(declared, &topics); err != nil {
		return 0, err
	}
	entry, present := topics[topic]
	if !present || entry.Schema == "" {
		return 0, fmt.Errorf("o tópico %s não está declarado em topics.json", topic)
	}
	schema, err := os.ReadFile(filepath.Join(r.contracts, "events", entry.Schema))
	if err != nil {
		return 0, err
	}

	body, err := json.Marshal(map[string]string{
		"schemaType": "PROTOBUF",
		"schema":     string(schema),
	})
	if err != nil {
		return 0, err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost,
		r.baseURL+"/subjects/"+topic+"-value", bytes.NewReader(body))
	if err != nil {
		return 0, err
	}
	request.Header.Set("Content-Type", "application/json")

	response, err := r.client.Do(request)
	if err != nil {
		return 0, err
	}
	defer func() { _ = response.Body.Close() }()
	payload, err := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	if err != nil {
		return 0, err
	}
	if response.StatusCode < 200 || response.StatusCode > 299 {
		return 0, fmt.Errorf("o registry recusou %s: %d %s", topic, response.StatusCode, payload)
	}
	var answer struct {
		ID int `json:"id"`
	}
	if err := json.Unmarshal(payload, &answer); err != nil || answer.ID <= 0 {
		return 0, fmt.Errorf("o registry não devolveu um id usável para %s", topic)
	}
	r.resolved.Store(topic, answer.ID)
	return answer.ID, nil
}

// --------------------------------------------------------------------- relay

// Relay envia ao Kafka o que as transações deixaram no outbox.
type Relay struct {
	db       *sql.DB
	client   *kgo.Client
	registry *Registry
	log      *slog.Logger
	interval time.Duration
}

// NewRelay abre o produtor.
func NewRelay(
	db *sql.DB,
	brokers []string,
	registry *Registry,
	logger *slog.Logger,
) (*Relay, error) {
	client, err := kgo.NewClient(
		kgo.SeedBrokers(brokers...),
		// Idempotência ligada: uma reentrega da própria biblioteca não pode
		// virar dois eventos com o mesmo `event_id` na fita.
		kgo.RequiredAcks(kgo.AllISRAcks()),
		kgo.ProducerLinger(10*time.Millisecond),
	)
	if err != nil {
		return nil, err
	}
	return &Relay{
		db:       db,
		client:   client,
		registry: registry,
		log:      logger,
		interval: 2 * time.Second,
	}, nil
}

// Close fecha o produtor.
func (r *Relay) Close() { r.client.Close() }

type pending struct {
	id          string
	key         string
	topic       string
	payload     []byte
	traceParent *string
}

// Run esvazia o outbox até o contexto acabar.
//
// Marcar como publicado é um UPDATE separado, DEPOIS do envio. Cair entre os
// dois republica o mesmo evento com o mesmo `event_id`, que é o que o inbox de
// quem consome reconhece. Marcar antes perderia o evento em silêncio, e essa é
// a única das duas falhas que ninguém percebe.
func (r *Relay) Run(ctx context.Context) {
	for {
		if err := r.drain(ctx); err != nil {
			if ctx.Err() != nil {
				return
			}
			r.log.Warn("relay adiada; os fatos do piloto ficam retidos", slog.Any("error", err))
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(r.interval):
		}
	}
}

func (r *Relay) drain(ctx context.Context) error {
	rows, err := r.db.QueryContext(ctx,
		// O filtro por aggregate_type é o que permite dividir a tabela com o
		// rental-core e o billing sem que uma relay publique os fatos da outra.
		`SELECT id, aggregate_id, topic, payload, trace_parent
		   FROM outbox
		  WHERE published_at IS NULL AND aggregate_type = 'rider'
		  ORDER BY occurred_at
		  LIMIT 100`)
	if err != nil {
		return err
	}
	var batch []pending
	for rows.Next() {
		var row pending
		if err := rows.Scan(&row.id, &row.key, &row.topic, &row.payload, &row.traceParent); err != nil {
			_ = rows.Close()
			return err
		}
		batch = append(batch, row)
	}
	if err := rows.Err(); err != nil {
		_ = rows.Close()
		return err
	}
	_ = rows.Close()

	for _, row := range batch {
		schemaID, err := r.registry.Resolve(ctx, row.topic)
		if err != nil {
			return err
		}
		record := &kgo.Record{
			Topic: row.topic,
			Key:   []byte(row.key),
			Value: row.payload,
			Headers: []kgo.RecordHeader{
				{Key: "schema-id", Value: []byte(strconv.Itoa(schemaID))},
			},
		}
		if row.traceParent != nil && *row.traceParent != "" {
			record.Headers = append(record.Headers,
				kgo.RecordHeader{Key: "traceparent", Value: []byte(*row.traceParent)})
		}
		if err := r.client.ProduceSync(ctx, record).FirstErr(); err != nil {
			return err
		}
		if _, err := r.db.ExecContext(ctx,
			`UPDATE outbox SET published_at = now() WHERE id = $1 AND published_at IS NULL`,
			row.id,
		); err != nil {
			return err
		}
	}
	return nil
}

// ----------------------------------------------------------------- consumer

// Verifier é o que o consumidor faz com o veredito. É uma interface para o
// teste do laço não precisar de banco.
type Verifier interface {
	MarkVerified(
		ctx context.Context,
		messageID, riderID string,
		matched bool,
		occurredAt time.Time,
	) (riders.Outcome, error)
}

// Consumer ouve `document.verified` e aplica o veredito.
type Consumer struct {
	client  *kgo.Client
	riders  Verifier
	log     *slog.Logger
	backoff time.Duration
}

// NewConsumer abre o consumidor no grupo do identity.
func NewConsumer(
	brokers []string,
	verifier Verifier,
	logger *slog.Logger,
) (*Consumer, error) {
	client, err := kgo.NewClient(
		kgo.SeedBrokers(brokers...),
		kgo.ConsumerGroup("identity-document-verified-v1"),
		kgo.ConsumeTopics(TopicDocumentVerified),
		kgo.ConsumeResetOffset(kgo.NewOffset().AtStart()),
		// Sem commit automático: o offset avança depois do efeito, nunca antes.
		kgo.DisableAutoCommit(),
	)
	if err != nil {
		return nil, err
	}
	return &Consumer{client: client, riders: verifier, log: logger, backoff: 5 * time.Second}, nil
}

// Close fecha o consumidor.
func (c *Consumer) Close() { c.client.Close() }

// Run consome até o contexto acabar.
//
// Duas falhas diferentes, tratadas de forma diferente:
//
//   - Mensagem quebrada -- bytes que não decodificam, evento sem identidade.
//     Repetir não conserta, e não avançar prende a partição para sempre. Ela é
//     registrada e pulada, e o offset avança.
//   - Dependência quebrada -- o banco fora do ar. Repetir CONSERTA, e pular
//     perderia o efeito. O lote é repetido, e o offset não avança.
//
// Confundir as duas é como se perde um evento ou como se para uma partição, e
// as duas coisas parecem a mesma no log de quem não separou.
func (c *Consumer) Run(ctx context.Context) {
	for ctx.Err() == nil {
		fetches := c.client.PollFetches(ctx)
		if fetches.IsClientClosed() || ctx.Err() != nil {
			return
		}
		if errs := fetches.Errors(); len(errs) > 0 {
			for _, fetchError := range errs {
				if !errors.Is(fetchError.Err, context.Canceled) {
					c.log.Warn("leitura do Kafka falhou",
						slog.String("topic", fetchError.Topic), slog.Any("error", fetchError.Err))
				}
			}
			c.sleep(ctx)
			continue
		}

		records := fetches.Records()
		if len(records) == 0 {
			continue
		}
		if !c.handle(ctx, records) {
			// Não comitar é o que faz o lote voltar. O efeito é idempotente
			// pelo inbox, então repetir não duplica nada.
			c.sleep(ctx)
			continue
		}
		if err := c.client.CommitRecords(ctx, records...); err != nil && ctx.Err() == nil {
			c.log.Warn("commit adiado; o lote volta", slog.Any("error", err))
		}
	}
}

// handle devolve false quando a dependência falhou e o lote precisa voltar.
func (c *Consumer) handle(ctx context.Context, records []*kgo.Record) bool {
	for _, record := range records {
		event := &events.RiderEvent{}
		if err := proto.Unmarshal(record.Value, event); err != nil {
			c.skip(record, "protobuf indecifrável", err)
			continue
		}
		if event.GetEventId() == "" || event.GetRiderId() == "" || event.OccurredAtMs == nil {
			c.skip(record, "evento sem identidade ou sem tempo", nil)
			continue
		}

		outcome, err := c.riders.MarkVerified(ctx, event.GetEventId(), event.GetRiderId(),
			event.GetVerified(), time.UnixMilli(event.GetOccurredAtMs()).UTC())
		if err != nil {
			if ctx.Err() != nil {
				return false
			}
			c.log.Warn("veredito não aplicado; o lote volta",
				slog.String("rider_id", event.GetRiderId()), slog.Any("error", err))
			return false
		}
		if outcome == riders.Unknown {
			c.log.Info("veredito sobre um piloto desconhecido",
				slog.String("rider_id", event.GetRiderId()))
		}
	}
	return true
}

func (c *Consumer) skip(record *kgo.Record, reason string, err error) {
	c.log.Warn("mensagem pulada",
		slog.String("reason", reason),
		slog.String("topic", record.Topic),
		slog.Int64("offset", record.Offset),
		slog.Any("error", err))
}

func (c *Consumer) sleep(ctx context.Context) {
	select {
	case <-ctx.Done():
	case <-time.After(c.backoff):
	}
}
