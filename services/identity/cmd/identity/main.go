// Command identity emite os tokens da plataforma e publica o JWKS que o portão
// usa para validá-los.
//
// Quatro modos. Os três últimos existem para serem operações com procedimento,
// em vez de intervenções manuais no banco ou de uma dependência a mais na
// imagem final:
//
//	identity                   sobe o serviço
//	identity healthcheck       consulta a própria prontidão (é o probe do Compose)
//	identity rotate-keys       promove uma chave de assinatura nova
//	identity import-authgate   traz os usuários do AuthGate legado
package main

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	_ "github.com/jackc/pgx/v5/stdlib"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"

	"github.com/iVega123/ProjectY/services/identity/internal/accounts"
	"github.com/iVega123/ProjectY/services/identity/internal/api"
	"github.com/iVega123/ProjectY/services/identity/internal/config"
	"github.com/iVega123/ProjectY/services/identity/internal/keys"
	"github.com/iVega123/ProjectY/services/identity/internal/legacy"
	"github.com/iVega123/ProjectY/services/identity/internal/passwords"
	"github.com/iVega123/ProjectY/services/identity/internal/sessions"
	"github.com/iVega123/ProjectY/services/identity/internal/telemetry"
	"github.com/iVega123/ProjectY/services/identity/internal/tokens"
)

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	subcommand := ""
	if len(os.Args) > 1 {
		subcommand = os.Args[1]
	}

	// O healthcheck sai antes de tudo: ele não fala com o banco e não pode
	// exigir a configuração inteira. É o binário consultando a si mesmo, que é
	// o que permite a imagem final não carregar curl.
	if subcommand == "healthcheck" {
		if err := healthcheck(); err != nil {
			logger.Error("prontidão recusada", slog.Any("error", err))
			os.Exit(1)
		}
		return
	}

	if err := run(subcommand, logger); err != nil {
		logger.Error("identity parou", slog.Any("error", err))
		os.Exit(1)
	}
}

func run(subcommand string, logger *slog.Logger) error {
	settings, err := config.Load()
	if err != nil {
		return fmt.Errorf("configuração inválida: %w", err)
	}

	database, err := open(settings.DatabaseURL)
	if err != nil {
		return fmt.Errorf("abrindo o banco: %w", err)
	}
	defer func() { _ = database.Close() }()

	switch subcommand {
	case "":
		return serve(settings, database, logger)
	case "rotate-keys":
		return rotate(settings, database, logger)
	case "import-authgate":
		return importLegacy(database, logger)
	default:
		return fmt.Errorf("subcomando desconhecido: %q", subcommand)
	}
}

func open(url string) (*sql.DB, error) {
	database, err := sql.Open("pgx", url)
	if err != nil {
		return nil, err
	}
	database.SetMaxOpenConns(10)
	database.SetMaxIdleConns(4)
	database.SetConnMaxLifetime(30 * time.Minute)
	return database, nil
}

func healthcheck() error {
	url := os.Getenv("IDENTITY_HEALTH_URL")
	if url == "" {
		url = "http://127.0.0.1:8095/health/ready"
	}
	client := &http.Client{Timeout: 3 * time.Second}
	response, err := client.Get(url)
	if err != nil {
		return err
	}
	defer func() { _ = response.Body.Close() }()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("%s respondeu %d", url, response.StatusCode)
	}
	return nil
}

func serve(settings config.Config, database *sql.DB, logger *slog.Logger) error {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	shutdownTelemetry, err := telemetry.Start(ctx)
	if err != nil {
		return fmt.Errorf("ligando a telemetria: %w", err)
	}
	defer func() {
		flush, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = shutdownTelemetry(flush)
	}()

	keyStore := keys.NewStore(database, settings.KeyEncryptionKey)
	ring, err := keyStore.Load(ctx)
	if err != nil {
		return fmt.Errorf("carregando as chaves de assinatura: %w", err)
	}
	logger.Info("chave de assinatura ativa", slog.String("kid", ring.Active().ID))

	accountStore := accounts.NewStore(database)
	if settings.AdminEmail != "" {
		hash, err := passwords.Hash(settings.AdminPassword)
		if err != nil {
			return err
		}
		if err := accountStore.EnsureAdmin(ctx, settings.AdminEmail, "Administrador", hash); err != nil {
			return fmt.Errorf("garantindo o administrador: %w", err)
		}
	}

	sessionStore := sessions.NewStore(database, settings.RefreshTokenTTL)
	service := api.New(
		accountStore,
		sessionStore,
		ring,
		tokens.NewMinter(settings.Issuer, settings.Audiences, settings.AccessTokenTTL),
		settings.Issuer,
		settings.PublicURL,
		logger,
	)

	mux := service.Routes()
	mux.HandleFunc("GET /health/live", func(writer http.ResponseWriter, _ *http.Request) {
		_, _ = writer.Write([]byte("live"))
	})
	ready := func(writer http.ResponseWriter, request *http.Request) {
		probe, cancel := context.WithTimeout(request.Context(), 2*time.Second)
		defer cancel()
		if err := database.PingContext(probe); err != nil {
			http.Error(writer, "database", http.StatusServiceUnavailable)
			return
		}
		if ring.Active().ID == "" {
			http.Error(writer, "signing key", http.StatusServiceUnavailable)
			return
		}
		_, _ = writer.Write([]byte("ready"))
	}
	mux.HandleFunc("GET /health/ready", ready)
	mux.HandleFunc("GET /health/startup", ready)

	// O chaveiro é relido de tempos em tempos porque a rotação pode ter sido
	// feita por outra réplica ou pelo subcomando. Sem isso, o processo que não
	// rodou a rotação continua assinando com uma chave que já saiu do JWKS.
	go refresh(ctx, keyStore, ring, logger)
	go sweep(ctx, sessionStore, logger)

	server := &http.Server{
		Addr:              settings.Bind,
		Handler:           otelhttp.NewHandler(mux, "identity"),
		ReadHeaderTimeout: 5 * time.Second,
	}
	go func() {
		<-ctx.Done()
		shutdown, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		_ = server.Shutdown(shutdown)
	}()

	logger.Info("identity ouvindo", slog.String("bind", settings.Bind))
	if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}

func refresh(ctx context.Context, store *keys.Store, ring *keys.Ring, logger *slog.Logger) {
	ticker := time.NewTicker(time.Minute)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			refreshed, err := store.Load(ctx)
			if err != nil {
				logger.Warn("não foi possível reler as chaves", slog.Any("error", err))
				continue
			}
			ring.Adopt(refreshed)
		}
	}
}

func sweep(ctx context.Context, store *sessions.Store, logger *slog.Logger) {
	ticker := time.NewTicker(time.Hour)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			removed, err := store.Sweep(ctx)
			if err != nil {
				logger.Warn("varredura de refresh tokens falhou", slog.Any("error", err))
				continue
			}
			if removed > 0 {
				logger.Info("refresh tokens expirados removidos", slog.Int64("count", removed))
			}
		}
	}
}

func rotate(settings config.Config, database *sql.DB, logger *slog.Logger) error {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	ring, err := keys.NewStore(database, settings.KeyEncryptionKey).
		Rotate(ctx, settings.KeyRotationOverlap)
	if err != nil {
		return err
	}
	logger.Info("chave promovida",
		slog.String("kid", ring.Active().ID),
		slog.String("overlap", settings.KeyRotationOverlap.String()))
	return nil
}

func importLegacy(database *sql.DB, logger *slog.Logger) error {
	source := os.Getenv("AUTHGATE_DATABASE_URL")
	if source == "" {
		return errors.New("AUTHGATE_DATABASE_URL é obrigatório para importar")
	}
	legacyDatabase, err := open(source)
	if err != nil {
		return err
	}
	defer func() { _ = legacyDatabase.Close() }()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()

	report, err := legacy.Import(ctx, legacyDatabase, database)
	if err != nil {
		return err
	}
	for _, reason := range report.Reasons {
		logger.Warn("linha não importada", slog.String("reason", reason))
	}
	logger.Info("importação concluída",
		slog.Int("imported", report.Imported),
		slog.Int("skipped", report.Skipped))
	return nil
}
