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

	attributes, err := resource.Merge(resource.Default(), resource.NewWithAttributes(
		semconv.SchemaURL,
		semconv.ServiceName(name()),
	))
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

func name() string {
	if configured := os.Getenv("OTEL_SERVICE_NAME"); configured != "" {
		return configured
	}
	return "identity"
}
