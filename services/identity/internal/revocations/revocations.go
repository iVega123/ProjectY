// Package revocations grava a denylist que o portão consulta nas operações de
// alto valor.
//
// O portão verifica o access token localmente e só pergunta ao Redis, em
// `projecty:revoked:jti:{jti}`, antes de criar um aluguel (ADR 0017). Até o #59
// ninguém escrevia essa chave: sair encerrava a renovação, e o access token que
// já estava na mão de alguém continuava criando aluguel pelos cinco minutos
// dele.
//
// O Redis não é a fonte da verdade -- o CockroachDB é. A revogação é gravada
// primeiro no banco, na linha do refresh token, e só depois copiada para cá.
// Por isso a cópia pode falhar sem que ninguém seja deslogado e sem que uma
// revogação se perca: [Resync] regrava, a partir do banco, todo token revogado
// que o portão ainda aceitaria.
package revocations

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"github.com/redis/go-redis/v9"

	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
)

// Margin é quanto a chave sobrevive ao token.
//
// O portão aceita um token até GATEWAY_JWT_CLOCK_SKEW_SECS (30 s por padrão)
// depois do `exp`. Uma chave que sumisse no `exp` exato deixaria um token
// revogado criar aluguel nesses segundos. O portão recusa subir com uma folga
// maior que esta margem (`clock_skew_within_denylist_margin`), e o valor está
// preso dos dois lados: aqui, em `TestTheMarginIsTheOneTheGatewayCapsItsSkewAt`,
// e lá, em `DENYLIST_KEY_MARGIN`.
const Margin = time.Minute

// ResyncInterval é de quanto em quanto tempo a denylist é refeita a partir do
// banco. É também o maior intervalo em que um token revogado passa numa
// operação de alto valor depois de o Redis voltar sem os dados.
const ResyncInterval = 5 * time.Second

// Key é a chave que o portão lê. O formato está preso dos dois lados: aqui, em
// `TestTheKeyIsTheOneTheGatewayReads`, e no portão, em
// `the_denylist_key_is_the_one_identity_writes`.
func Key(tokenID string) string {
	return "projecty:revoked:jti:" + tokenID
}

// Denylist nega access tokens.
type Denylist interface {
	Deny(ctx context.Context, revoked []sessions.AccessToken) error
}

// Source é de onde a denylist é refeita: os tokens revogados que expiram
// depois de `after`.
type Source interface {
	Revoked(ctx context.Context, after time.Time) ([]sessions.AccessToken, error)
}

// Redis é a denylist de verdade.
type Redis struct {
	client *redis.Client
	now    func() time.Time
}

// NewRedis prepara o cliente sem conectar. Um Redis fora do ar na subida não
// impede o identity de servir login e renovação.
func NewRedis(url string) (*Redis, error) {
	options, err := redis.ParseURL(url)
	if err != nil {
		return nil, fmt.Errorf("IDENTITY_REDIS_URL inválida: %w", err)
	}
	// Curto de propósito. A gravação acontece dentro do logout, e um Redis fora
	// do ar não pode segurar a resposta de quem está saindo; quem cobre a falha
	// é a ressincronização, e não uma repetição aqui.
	options.DialTimeout = 250 * time.Millisecond
	options.ReadTimeout = 250 * time.Millisecond
	options.WriteTimeout = 250 * time.Millisecond
	options.MaxRetries = -1
	return &Redis{client: redis.NewClient(options), now: time.Now}, nil
}

// Deny grava uma chave por token, expirando [Margin] depois dele. Um token que
// o portão já recusaria por conta própria não é gravado.
func (r *Redis) Deny(ctx context.Context, revoked []sessions.AccessToken) error {
	now := r.now()
	pipeline := r.client.Pipeline()
	queued := 0
	for _, token := range revoked {
		until := token.ExpiresAt.Add(Margin)
		if !until.After(now) {
			continue
		}
		// Expiração absoluta, e não um TTL: regravar a mesma chave a cada
		// passada não empurra o fim dela para frente.
		pipeline.SetArgs(ctx, Key(token.ID), "1", redis.SetArgs{ExpireAt: until})
		queued++
	}
	if queued == 0 {
		return nil
	}
	_, err := pipeline.Exec(ctx)
	return err
}

// Close solta as conexões.
func (r *Redis) Close() error {
	return r.client.Close()
}

// SyncOnce copia para a denylist o que o banco diz estar revogado.
func SyncOnce(ctx context.Context, source Source, denylist Denylist) error {
	revoked, err := source.Revoked(ctx, time.Now().Add(-Margin))
	if err != nil {
		return fmt.Errorf("lendo as revogações: %w", err)
	}
	return denylist.Deny(ctx, revoked)
}

// Resync refaz a denylist a cada `interval`, até o contexto acabar.
//
// Cobre as duas formas de a cópia no Redis ficar para trás: a gravação do logout
// falhou porque o Redis estava fora, ou o Redis voltou sem os dados. Enquanto
// ele está fora o portão recusa as operações de alto valor de todo mundo, então
// o único intervalo em que um token revogado passaria é entre o Redis voltar
// vazio e a próxima passada.
func Resync(ctx context.Context, source Source, denylist Denylist, interval time.Duration, logger *slog.Logger) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	failing := false
	for {
		pass, cancel := context.WithTimeout(ctx, 2*time.Second)
		err := SyncOnce(pass, source, denylist)
		cancel()
		switch {
		case ctx.Err() != nil:
			return
		case err != nil && !failing:
			// Uma vez por queda, e não a cada cinco segundos dela.
			logger.Warn("denylist não regravada; as revogações seguem no banco", slog.Any("error", err))
			failing = true
		case err == nil && failing:
			logger.Info("denylist regravada a partir do banco")
			failing = false
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}
