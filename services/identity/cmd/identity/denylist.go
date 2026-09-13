package main

import (
	"context"
	"log/slog"

	"github.com/iVega123/ProjectY/services/identity/internal/config"
	"github.com/iVega123/ProjectY/services/identity/internal/revocations"
)

// startDenylist abre a denylist e põe a ressincronização para rodar.
//
// Não conecta na subida: um Redis fora do ar não impede ninguém de entrar nem
// de renovar, e a primeira passada que o encontrar de pé regrava o que o banco
// revogou enquanto ele estava fora.
func startDenylist(
	ctx context.Context,
	settings config.Config,
	source revocations.Source,
	logger *slog.Logger,
) (*revocations.Redis, error) {
	denylist, err := revocations.NewRedis(settings.RedisURL)
	if err != nil {
		return nil, err
	}
	go revocations.Resync(ctx, source, denylist, revocations.ResyncInterval, logger)
	return denylist, nil
}
