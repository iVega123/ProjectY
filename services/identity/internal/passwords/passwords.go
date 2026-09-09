// Package passwords guarda senha em Argon2id e ainda sabe ler o formato que o
// ASP.NET Identity deixou para trás.
//
// # A migração, que é o trabalho de verdade
//
// O AuthGate guardava PBKDF2 no formato próprio do ASP.NET Identity: um blob
// com marcador de versão, PRF, contagem de iterações e sal empacotados juntos.
// Havia três saídas, e o ADR 0012 escolheu a do meio:
//
//  1. Reimplementar o formato em Go e continuar lendo os hashes antigos para
//     sempre. Mantém o formato legado vivo indefinidamente.
//  2. Verificação dupla: confere no formato antigo, regrava no novo no primeiro
//     login bem-sucedido. O formato legado sai sozinho, à medida que as pessoas
//     entram.
//  3. Re-semear tudo. Legítimo aqui, porque o dado é fictício -- e é a única
//     opção que não pode ser usada num sistema com usuários reais.
//
// A escolha é a (2), e o motivo é que ela é a única que se pratica de verdade.
// A (3) resolveria este repositório e não ensinaria nada; a (1) nunca termina.
// O custo da (2) está escrito abaixo: o leitor do formato antigo existe, tem
// teste, e some quando o último hash legado for regravado -- o que é
// observável, e não uma esperança.
package passwords

import (
	"crypto/hmac"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/sha512"
	"crypto/subtle"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"hash"
	"strings"

	"golang.org/x/crypto/argon2"
)

// Parâmetros do Argon2id. São os do RFC 9106 para o perfil de segunda opção
// (64 MiB), que é o que cabe num contêiner de serviço sem transformar login em
// vetor de negação de serviço contra o próprio serviço.
const (
	argonTime    = 3
	argonMemory  = 64 * 1024
	argonThreads = 1
	argonKeyLen  = 32
	argonSaltLen = 16
)

// ErrUnknownFormat diz que o hash guardado não é nem Argon2id nem um blob do
// ASP.NET Identity. É corrupção de dado, não senha errada, e o chamador precisa
// distinguir as duas coisas.
var ErrUnknownFormat = errors.New("formato de hash de senha desconhecido")

var argonEncoding = base64.RawStdEncoding

// Hash grava a senha no formato corrente.
func Hash(password string) (string, error) {
	salt := make([]byte, argonSaltLen)
	if _, err := rand.Read(salt); err != nil {
		return "", fmt.Errorf("sorteando sal: %w", err)
	}
	key := argon2.IDKey([]byte(password), salt, argonTime, argonMemory, argonThreads, argonKeyLen)
	return fmt.Sprintf(
		"$argon2id$v=%d$m=%d,t=%d,p=%d$%s$%s",
		argon2.Version, argonMemory, argonTime, argonThreads,
		argonEncoding.EncodeToString(salt), argonEncoding.EncodeToString(key),
	), nil
}

// Verify confere a senha contra o hash guardado.
//
// O segundo retorno é o que faz a verificação dupla funcionar: quando ele vem
// verdadeiro, a senha estava certa E o hash está num formato de saída. Quem
// chama regrava com [Hash] na mesma transação do login, e aquele usuário nunca
// mais passa por aqui.
func Verify(encoded, password string) (ok bool, needsRehash bool, err error) {
	if strings.HasPrefix(encoded, "$argon2id$") {
		ok, err := verifyArgon2id(encoded, password)
		return ok, false, err
	}
	ok, err = verifyAspNetIdentity(encoded, password)
	return ok, ok, err
}

func verifyArgon2id(encoded, password string) (bool, error) {
	parts := strings.Split(encoded, "$")
	if len(parts) != 6 {
		return false, ErrUnknownFormat
	}

	var version int
	if _, err := fmt.Sscanf(parts[2], "v=%d", &version); err != nil || version != argon2.Version {
		return false, ErrUnknownFormat
	}

	var memory uint32
	var time uint32
	var threads uint8
	if _, err := fmt.Sscanf(parts[3], "m=%d,t=%d,p=%d", &memory, &time, &threads); err != nil {
		return false, ErrUnknownFormat
	}

	salt, err := argonEncoding.DecodeString(parts[4])
	if err != nil {
		return false, ErrUnknownFormat
	}
	expected, err := argonEncoding.DecodeString(parts[5])
	if err != nil || len(expected) == 0 {
		return false, ErrUnknownFormat
	}

	// Os parâmetros vêm do hash, e não das constantes deste arquivo. É o que
	// permite subir o custo do Argon2 sem invalidar o que já está gravado.
	actual := argon2.IDKey([]byte(password), salt, time, memory, threads, uint32(len(expected)))
	return subtle.ConstantTimeCompare(actual, expected) == 1, nil
}

// verifyAspNetIdentity lê os dois formatos que o ASP.NET Identity grava.
//
// O layout não está documentado em lugar nenhum além do código do framework, e
// é este:
//
//	v2: 0x00 | sal (16 bytes) | subchave (32 bytes)
//	    PBKDF2-HMACSHA1, 1000 iterações
//
//	v3: 0x01 | prf (uint32 BE) | iterações (uint32 BE) | tamanho do sal (uint32 BE)
//	         | sal | subchave
//	    prf: 0 = HMACSHA1, 1 = HMACSHA256, 2 = HMACSHA512
//
// Tudo big-endian, o que é digno de nota num formato escrito por uma runtime
// little-endian: é uma escolha explícita do framework, e copiá-la errado
// produz um verificador que recusa toda senha correta.
func verifyAspNetIdentity(encoded, password string) (bool, error) {
	blob, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil || len(blob) == 0 {
		return false, ErrUnknownFormat
	}

	switch blob[0] {
	case 0x00:
		if len(blob) != 1+16+32 {
			return false, ErrUnknownFormat
		}
		return comparePbkdf2(sha1.New, password, blob[1:17], 1000, blob[17:]), nil
	case 0x01:
		if len(blob) < 13 {
			return false, ErrUnknownFormat
		}
		prf := binary.BigEndian.Uint32(blob[1:5])
		iterations := binary.BigEndian.Uint32(blob[5:9])
		saltLength := binary.BigEndian.Uint32(blob[9:13])
		// O corte é aritmética sobre números que vieram do banco. Sem estes
		// limites, um blob adulterado escolhe quanto trabalho este processo faz
		// e onde ele lê.
		if saltLength < 8 || iterations == 0 || iterations > 1_000_000 {
			return false, ErrUnknownFormat
		}
		if uint64(len(blob)) < 13+uint64(saltLength)+1 {
			return false, ErrUnknownFormat
		}
		var newHash func() hash.Hash
		switch prf {
		case 0:
			newHash = sha1.New
		case 1:
			newHash = sha256.New
		case 2:
			newHash = sha512.New
		default:
			return false, ErrUnknownFormat
		}
		salt := blob[13 : 13+saltLength]
		return comparePbkdf2(newHash, password, salt, int(iterations), blob[13+saltLength:]), nil
	default:
		return false, ErrUnknownFormat
	}
}

func comparePbkdf2(
	newHash func() hash.Hash,
	password string,
	salt []byte,
	iterations int,
	expected []byte,
) bool {
	actual, err := pbkdf2.Key(newHash, password, salt, iterations, len(expected))
	if err != nil {
		return false
	}
	return hmac.Equal(actual, expected)
}
