// Package telemetry liga o identity ao coletor do OpenTelemetry.
//
// O mesmo alvo dos outros serviços, pelo mesmo protocolo: OTLP sobre HTTP para
// o otel-collector, que fecha o service graph e alimenta os painéis. Um serviço
// que não exporta trace é um buraco no mapa -- e este está no caminho do login,
// que é o primeiro passo de quase todo fluxo do sistema.
package telemetry

import (
	"context"
	"os"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracehttp"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/sdk/resource"
	"go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.27.0"
)

// Start liga o exportador e devolve como desligá-lo.
//
// Sem OTEL_EXPORTER_OTLP_ENDPOINT o serviço sobe sem telemetria em vez de não
// subir: observabilidade indisponível não é motivo para recusar login. É a
// mesma postura que o ADR 0003 dá ao resto -- degradar, e dizer que degradou.
func Start(ctx context.Context) (func(context.Context) error, error) {
	if os.Getenv("OTEL_EXPORTER_OTLP_ENDPOINT") == "" {
		return func(context.Context) error { return nil }, nil
	}

	exporter, err := otlptracehttp.New(ctx)
	if err != nil {
		return nil, err
	}

	attributes, err := Resource()
	if err != nil {
		return nil, err
	}

	provider := trace.NewTracerProvider(
		trace.WithBatcher(exporter, trace.WithBatchTimeout(5*time.Second)),
		trace.WithResource(attributes),
	)
	otel.SetTracerProvider(provider)
	// W3C traceparent, que é o que o portão injeta e o que os outros serviços
	// leem. Sem isto o trace do login começa e termina aqui.
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(
		propagation.TraceContext{}, propagation.Baggage{},
	))
	return provider.Shutdown, nil
}

// Resource descreve este processo para o coletor.
//
// O conjunto próprio vai SEM schema URL de propósito. resource.Merge recusa
// dois recursos que declarem schemas diferentes, e resource.Default() declara o
// do semconv que a versão do SDK carrega -- fixar uma versão aqui faz a subida
// quebrar no dia em que o SDK avança, e quebrar dentro do container, longe do
// teste. Sem schema, o resultado herda o do outro lado, e um go get -u deixa de
// conseguir derrubar o serviço.
func Resource() (*resource.Resource, error) {
	return resource.Merge(resource.Default(), resource.NewSchemaless(
		semconv.ServiceName(name()),
	))
}

func name() string {
	if configured := os.Getenv("OTEL_SERVICE_NAME"); configured != "" {
		return configured
	}
	return "identity"
}
