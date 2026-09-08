// Package tokens emite o access token.
//
// O formato não é escolha deste pacote: o portão é quem valida, e ele exige
// EdDSA com `kid` no cabeçalho, `sub`, `iss`, `aud`, `exp`, `iat` e `jti` no
// corpo, e recusa qualquer token cuja vida passe do teto configurado nele. Este
// arquivo existe para produzir exatamente isso e nada mais.
package tokens

import (
	"fmt"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/google/uuid"

	"github.com/iVega123/ProjectY/services/identity/internal/keys"
)

// Minter emite tokens para um emissor e um conjunto de audiências.
type Minter struct {
	issuer    string
	audiences []string
	lifetime  time.Duration
}

// NewMinter prende o emissor às constantes que o portão espera ver.
func NewMinter(issuer string, audiences []string, lifetime time.Duration) Minter {
	return Minter{issuer: issuer, audiences: audiences, lifetime: lifetime}
}

// Access é o token e o que o cliente precisa saber sobre ele.
type Access struct {
	Token     string
	TokenID   string
	ExpiresIn int
}

type claims struct {
	Roles []string `json:"roles"`
	jwt.RegisteredClaims
}

// Mint assina um access token para o sujeito.
//
// # Por que `aud` é uma lista
//
// O ADR 0006 dava uma audiência por serviço, e o portão valida a do upstream
// que a requisição está chamando. Com um token por audiência, uma tela que lê
// aluguel e nota precisaria de dois tokens -- e o console guarda um só. Foi
// esse aperto que produziu, no #137, um billing configurado para aceitar a
// audiência do rental-core: uma audiência compartilhada, escrita como
// concessão.
//
// A lista desfaz aquilo sem desfazer a separação. Cada serviço continua
// validando o próprio nome; o que muda é que o EMISSOR decide quais nomes
// entram no token, por sujeito e por papel, em vez de dois serviços passarem a
// responder pelo mesmo nome. Um token continua replayável nos serviços que
// estão dentro dele -- a diferença é que agora esse conjunto é uma decisão de
// quem emite, e não um efeito colateral de configuração.
func (m Minter) Mint(key keys.Key, subject string, roles []string, now time.Time) (Access, error) {
	if subject == "" {
		return Access{}, fmt.Errorf("token sem sujeito")
	}
	if roles == nil {
		roles = []string{}
	}

	issuedAt := now.UTC().Truncate(time.Second)
	expiresAt := issuedAt.Add(m.lifetime)
	tokenID := uuid.NewString()

	token := jwt.NewWithClaims(jwt.SigningMethodEdDSA, claims{
		Roles: roles,
		RegisteredClaims: jwt.RegisteredClaims{
			Issuer:    m.issuer,
			Subject:   subject,
			Audience:  m.audiences,
			ID:        tokenID,
			IssuedAt:  jwt.NewNumericDate(issuedAt),
			NotBefore: jwt.NewNumericDate(issuedAt),
			ExpiresAt: jwt.NewNumericDate(expiresAt),
		},
	})
	// O `kid` é o que permite rotacionar: o portão escolhe a chave pública pelo
	// que está escrito aqui, e não pela última que baixou.
	token.Header["kid"] = key.ID

	signed, err := token.SignedString(key.Signer())
	if err != nil {
		return Access{}, fmt.Errorf("assinando o access token: %w", err)
	}
	return Access{
		Token:     signed,
		TokenID:   tokenID,
		ExpiresIn: int(m.lifetime.Seconds()),
	}, nil
}
