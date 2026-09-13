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
//
// Duas versões da string canônica convivem até o #191 terminar. A `v1` liga o
// envelope a quem, quando, método, caminho e audiência; a `v2` acrescenta o
// SHA-256 do corpo, e é o que impede um envelope capturado de servir, na mesma
// rota e dentro da janela, para outro corpo -- outra foto de CNH, por exemplo.
// Quando `X-Identity-Signature-V2` vem, só ela decide. Sem ela, ainda vale a
// `v1`, porque recusá-la antes de o portão assinar `v2` trancaria o serviço
// para fora, como no #136.
package gateway

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"io"
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
	// SignatureV2Header é a assinatura que cobre o corpo.
	SignatureV2Header = "X-Identity-Signature-V2"

	// AdminRole é comparado sem diferenciar maiúsculas, porque é o que o
	// `ClaimsPrincipal.IsInRole` do lado .NET faz. As camadas discordarem sobre
	// quem é administrador seria pior que qualquer das duas regras isolada.
	AdminRole = "Admin"

	// MaxSignedBodyBytes é o maior corpo que o portão assina. Um corpo maior
	// não saiu dele, e ler além disso seria dar a quem alcança esta porta um
	// jeito de encher a memória antes de qualquer assinatura conferir.
	MaxSignedBodyBytes = 32 << 20
)

var headerOrder = []string{
	KeyIDHeader, SubjectHeader, RolesHeader, IssuedAtHeader,
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
//
// Com a assinatura `v2`, Verify lê o corpo inteiro para conferi-lo e o devolve
// a `request.Body` intacto, para o handler ler depois como leria sem isto.
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
	keyID, subject, roles, issuedAtValue := values[0], values[1], values[2], values[3]

	// Cada assinatura no máximo uma vez, e ao menos uma das duas.
	v1 := request.Header.Values(SignatureHeader)
	v2 := request.Header.Values(SignatureV2Header)
	if len(v1) > 1 || len(v2) > 1 || len(v1)+len(v2) == 0 {
		return nil
	}

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

	bound := strings.Join([]string{
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

	if len(v2) == 1 {
		// Presente, a `v2` decide sozinha. Cair para a `v1` quando ela falha
		// seria aceitar exatamente o corpo trocado que ela acabou de recusar.
		digest, ok := digestBody(request)
		if !ok || !v.validSignature("v2\n"+bound+"\n"+digest, v2[0], "v2=") {
			return nil
		}
	} else if !v.validSignature("v1\n"+bound, v1[0], "v1=") {
		return nil
	}
	return &Caller{Subject: subject, Roles: parsedRoles}
}

// digestBody é a última linha da `v2`: o SHA-256 dos bytes do corpo, em hex
// minúsculo, lido até o fim, com ou sem `Content-Length`. Sem corpo e corpo
// vazio são o mesmo digest, o de zero bytes.
func digestBody(request *http.Request) (string, bool) {
	var raw []byte
	if request.Body != nil && request.Body != http.NoBody {
		read, err := io.ReadAll(io.LimitReader(request.Body, MaxSignedBodyBytes+1))
		if err != nil || len(read) > MaxSignedBodyBytes {
			return "", false
		}
		raw = read
		request.Body = io.NopCloser(bytes.NewReader(raw))
	}
	sum := sha256.Sum256(raw)
	return hex.EncodeToString(sum[:]), true
}

func (v *Verifier) validSignature(canonical, signature, version string) bool {
	encoded, found := strings.CutPrefix(signature, version)
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
