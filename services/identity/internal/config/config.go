// Package config lê o ambiente uma vez, na subida, e recusa o processo quando
// falta alguma coisa.
//
// Recusar cedo é o ponto. Um padrão silencioso para chave de assinatura ou
// audiência é o tipo de configuração que só aparece em produção, e aparece como
// token que todo mundo aceita.
package config

import (
	"crypto/sha256"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

// Config é tudo que o serviço precisa saber antes de aceitar a primeira
// requisição.
type Config struct {
	Bind        string
	DatabaseURL string

	Issuer    string
	Audiences []string

	// PublicURL é como o mundo chega neste serviço, e serve só para o documento
	// de descoberta apontar para um JWKS que alguém de fora consiga baixar.
	PublicURL string

	// AccessTokenTTL vale 5 minutos por padrão, e o número é uma decisão do ADR
	// 0017: o portão verifica localmente, então uma revogação só morde quando o
	// token expira. Cinco minutos é a latência declarada dessa revogação.
	//
	// O portão recusa qualquer token cujo `exp - iat` passe de
	// GATEWAY_JWT_MAX_LIFETIME_SECS, que também vale 300. Subir este valor sem
	// subir aquele produz tokens que o emissor assina e o portão nunca aceita.
	AccessTokenTTL  time.Duration
	RefreshTokenTTL time.Duration

	// KeyRotationOverlap é por quanto tempo a chave anterior continua no JWKS
	// depois de rebaixada. Precisa cobrir a vida de um access token, ou rotação
	// vira deslogar todo mundo.
	KeyRotationOverlap time.Duration

	KeyEncryptionKey [32]byte

	AdminEmail    string
	AdminPassword string
}

// Load monta a configuração a partir do ambiente.
func Load() (Config, error) {
	config := Config{
		Bind:      value("IDENTITY_BIND", ":8095"),
		Issuer:    value("IDENTITY_ISSUER", "projecty.identity"),
		PublicURL: strings.TrimRight(value("IDENTITY_PUBLIC_URL", "http://localhost:8095"), "/"),
	}

	var err error
	if config.DatabaseURL, err = required("IDENTITY_DATABASE_URL"); err != nil {
		return Config{}, err
	}

	audiences, err := required("IDENTITY_AUDIENCES")
	if err != nil {
		return Config{}, err
	}
	for _, audience := range strings.Split(audiences, ",") {
		if trimmed := strings.TrimSpace(audience); trimmed != "" {
			config.Audiences = append(config.Audiences, trimmed)
		}
	}
	if len(config.Audiences) == 0 {
		return Config{}, fmt.Errorf("IDENTITY_AUDIENCES não lista nenhuma audiência")
	}

	if config.AccessTokenTTL, err = seconds("IDENTITY_ACCESS_TOKEN_TTL_SECONDS", 300); err != nil {
		return Config{}, err
	}
	if config.RefreshTokenTTL, err = seconds("IDENTITY_REFRESH_TOKEN_TTL_SECONDS", 7*24*3600); err != nil {
		return Config{}, err
	}
	if config.KeyRotationOverlap, err = seconds("IDENTITY_KEY_ROTATION_OVERLAP_SECONDS", 900); err != nil {
		return Config{}, err
	}
	if config.KeyRotationOverlap < config.AccessTokenTTL {
		return Config{}, fmt.Errorf(
			"IDENTITY_KEY_ROTATION_OVERLAP_SECONDS precisa cobrir a vida do access token, " +
				"ou uma rotação invalida tokens que ainda estão em voo")
	}

	sealingSecret, err := required("IDENTITY_KEY_ENCRYPTION_KEY")
	if err != nil {
		return Config{}, err
	}
	if len(sealingSecret) < 32 {
		return Config{}, fmt.Errorf("IDENTITY_KEY_ENCRYPTION_KEY precisa de ao menos 32 bytes")
	}
	// SHA-256 aqui ajusta comprimento, não deriva chave a partir de senha. O
	// valor é um segredo gerado (scripts/New-LocalSecrets.ps1 emite 32 bytes
	// aleatórios em base64url), então já tem a entropia toda; o que falta é ele
	// ter exatamente os 32 bytes que o AES-256 exige.
	config.KeyEncryptionKey = sha256.Sum256([]byte(sealingSecret))

	config.AdminEmail = value("IDENTITY_ADMIN_EMAIL", "")
	config.AdminPassword = value("IDENTITY_ADMIN_PASSWORD", "")
	if (config.AdminEmail == "") != (config.AdminPassword == "") {
		return Config{}, fmt.Errorf(
			"IDENTITY_ADMIN_EMAIL e IDENTITY_ADMIN_PASSWORD vêm juntos ou não vêm")
	}

	return config, nil
}

func value(name, fallback string) string {
	if found, ok := os.LookupEnv(name); ok && strings.TrimSpace(found) != "" {
		return strings.TrimSpace(found)
	}
	return fallback
}

func required(name string) (string, error) {
	found := value(name, "")
	if found == "" {
		return "", fmt.Errorf("%s é obrigatório", name)
	}
	return found, nil
}

func seconds(name string, fallback int) (time.Duration, error) {
	raw := value(name, strconv.Itoa(fallback))
	parsed, err := strconv.Atoi(raw)
	if err != nil || parsed <= 0 {
		return 0, fmt.Errorf("%s precisa ser um número de segundos positivo", name)
	}
	return time.Duration(parsed) * time.Second, nil
}
