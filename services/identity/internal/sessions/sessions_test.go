package sessions

import (
	"context"
	"testing"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/testdb"
)

// TestReadyOnceTheAccessTokenColumnsExist: a prontidão do identity pergunta
// isto, e é o que segura um rollout que chegou antes do Job de schema.
func TestReadyOnceTheAccessTokenColumnsExist(t *testing.T) {
	store := NewStore(testdb.Open(t), time.Hour)

	if err := store.Ready(context.Background()); err != nil {
		t.Fatalf("o schema de teste tem a migração 007 e mesmo assim: %v", err)
	}
}
