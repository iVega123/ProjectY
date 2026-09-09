// Package gateway confere o envelope de identidade do ADR 0008.
//
// O portão é a única fronteira que valida um token; para trás dele ele assina
// um envelope HMAC por requisição, e cada serviço confere. Esta é a TERCEIRA
// implementação da conferência -- as outras são
// `Shared/Security/GatewayIdentityAuthentication.cs`, em C#, e
// `services/billing/.../GatewayIdentity.kt`, em Kotlin.
//
// Uma terceira implementação de um limite de segurança é uma coisa que se
// assume de olhos abertos: qualquer divergência entre as três é uma porta, e a
// divergência não aparece como build quebrado. Por isso esta é literal -- a
// string canônica campo a campo na mesma ordem, o mesmo conjunto de caracteres
// aceitos, a mesma janela de tempo, comparação de tempo constante -- e por isso
// o teste carrega um envelope que o portão realmente produziu.
//
// Não reimplementa a assinatura, só a verificação. O identity não chama
// ninguém para trás dele, e uma chave que só confere é uma chave que não emite.
package gateway

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Os cabeçalhos do envelope, na ordem em que entram na string canônica.
const (
	KeyIDHeader     = "X-Identity-Key-Id"
	SubjectHeader   = "X-Identity-Subject"
	RolesHeader     = "X-Identity-Roles"
	IssuedAtHeader  = "X-Identity-Issued-At"
	SignatureHeader = "X-Identity-Signature"

	// AdminRole é comparado sem diferenciar maiúsculas, porque é o que o
	// `ClaimsPrincipal.IsInRole` do lado .NET faz. As camadas discordarem sobre
	// quem é administrador seria pior que qualquer das duas regras isolada.
	AdminRole = "Admin"
)

var headerOrder = []string{
	KeyIDHeader, SubjectHeader, RolesHeader, IssuedAtHeader, SignatureHeader,
}

// Caller é quem está chamando, segundo o portão.
type Caller struct {
	Subject string
	Roles   []string
}

// IsAdmin diz se o chamador carrega o papel de administrador.
func (c Caller) IsAdmin() bool {
	for _, role := range c.Roles {
		if strings.EqualFold(role, AdminRole) {
			return true
		}
	}
	return false
}

// Verifier confere envelopes assinados com uma chave e destinados a uma
// audiência.
type Verifier struct {
	signingKey   []byte
	signingKeyID string
	audience     string
	maximumAge   time.Duration
	clockSkew    time.Duration
	now          func() time.Time
}

// New monta o verificador. Recusa a configuração em vez de aceitar um envelope
// que qualquer um poderia forjar.
func New(signingKey []byte, signingKeyID, audience string) (*Verifier, error) {
	if len(signingKey) < 32 {
		return nil, errors.New("a chave do envelope precisa de ao menos 32 bytes")
	}
	if strings.TrimSpace(signingKeyID) == "" || strings.TrimSpace(audience) == "" {
		return nil, errors.New("identificador de chave e audiência do envelope são obrigatórios")
	}
	return &Verifier{
		signingKey:   signingKey,
		signingKeyID: signingKeyID,
		audience:     audience,
		maximumAge:   30 * time.Second,
		clockSkew:    5 * time.Second,
		now:          time.Now,
	}, nil
}

// Verify devolve o chamador, ou nil quando o envelope não vale.
//
// Nunca um erro com a razão: quem não passou não precisa saber por qual dos
// sete motivos, e a resposta que descreve a falha é a que ensina a contorná-la.
func (v *Verifier) Verify(request *http.Request) *Caller {
	values := make([]string, len(headerOrder))
	for index, name := range headerOrder {
		// Exatamente um valor por cabeçalho. Com dois, duas camadas podem ler
		// coisas diferentes do mesmo envelope.
		present := request.Header.Values(name)
		if len(present) != 1 {
			return nil
		}
		values[index] = present[0]
	}
	keyID, subject, roles, issuedAtValue, signature := values[0], values[1], values[2], values[3], values[4]

	if keyID != v.signingKeyID {
		return nil
	}
	if !SafeComponent(keyID, 128) || !SafeComponent(subject, 512) {
		return nil
	}
	// Só dígitos, como o NumberStyles.None do lado .NET. Aceitar um sinal
	// deixaria um carimbo negativo passar pela janela de tempo por baixo.
	if !onlyDigits(issuedAtValue) {
		return nil
	}
	issuedAt, err := strconv.ParseInt(issuedAtValue, 10, 64)
	if err != nil {
		return nil
	}

	now := v.now().Unix()
	if issuedAt > now+int64(v.clockSkew.Seconds()) {
		return nil
	}
	if issuedAt < now-int64(v.maximumAge.Seconds()) {
		return nil
	}

	parsedRoles, ok := parseRoles(roles)
	if !ok {
		return nil
	}

	canonical := strings.Join([]string{
		"v1",
		keyID,
		subject,
		roles,
		issuedAtValue,
		request.Method,
		// RequestURI é caminho e query na forma crua, que é a que o portão
		// assinou. Decodificar aqui produziria uma string diferente para o
		// mesmo pedido, e a assinatura deixaria de fechar.
		request.URL.RequestURI(),
		v.audience,
	}, "\n")

	if !v.validSignature(canonical, signature) {
		return nil
	}
	return &Caller{Subject: subject, Roles: parsedRoles}
}

func (v *Verifier) validSignature(canonical, signature string) bool {
	encoded, found := strings.CutPrefix(signature, "v1=")
	if !found {
		return false
	}
	// O portão remove o preenchimento.
	supplied, err := base64.RawURLEncoding.DecodeString(encoded)
	if err != nil {
		return false
	}
	mac := hmac.New(sha256.New, v.signingKey)
	mac.Write([]byte(canonical))
	// hmac.Equal é de tempo constante, e é o que impede alguém de descobrir a
	// assinatura um byte por vez.
	return hmac.Equal(mac.Sum(nil), supplied)
}

func parseRoles(value string) ([]string, bool) {
	if value == "" {
		return nil, true
	}
	if len(value) > 1024 {
		return nil, false
	}
	roles := strings.Split(value, ",")
	if len(roles) > 32 {
		return nil, false
	}
	for _, role := range roles {
		if !SafeComponent(role, 512) {
			return nil, false
		}
	}
	return roles, true
}

// SafeComponent é ASCII imprimível menos o espaço e menos a vírgula.
//
// A vírgula fica de fora porque é o separador dos papéis: aceitá-la deixaria um
// papel chamado "a,Admin" virar dois na leitura de quem separa, que é
// exatamente uma escalada de privilégio.
func SafeComponent(value string, maximumLength int) bool {
	if value == "" || len(value) > maximumLength {
		return false
	}
	for index := range len(value) {
		character := value[index]
		if (character < '!' || character > '+') && (character < '-' || character > '~') {
			return false
		}
	}
	return true
}

func onlyDigits(value string) bool {
	if value == "" {
		return false
	}
	for index := range len(value) {
		if value[index] < '0' || value[index] > '9' {
			return false
		}
	}
	return true
}
