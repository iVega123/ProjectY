package telemetry

import (
	"testing"

	"go.opentelemetry.io/otel/attribute"
	semconv "go.opentelemetry.io/otel/semconv/v1.27.0"
)

// O recurso é montado uma vez, na subida, e só quando há coletor configurado --
// de modo que um erro aqui não aparece em teste nenhum e aparece no container,
// como saída 1 do processo. Este teste monta o recurso sem coletor.
func TestTheResourceMerges(t *testing.T) {
	t.Setenv("OTEL_SERVICE_NAME", "identity")

	described, err := Resource()
	if err != nil {
		t.Fatalf("o recurso não montou: %v", err)
	}

	var name attribute.Value
	for _, held := range described.Attributes() {
		if held.Key == semconv.ServiceNameKey {
			name = held.Value
		}
	}
	if name.AsString() != "identity" {
		t.Fatalf("service.name saiu %q", name.AsString())
	}
}
