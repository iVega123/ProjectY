package passwords

import (
	"errors"
	"strings"
	"testing"
)

// Os dois vetores abaixo NÃO foram escritos à mão.
//
// Eles saíram do PasswordHasher<T> do ASP.NET Identity, executado contra o
// pacote Microsoft.Extensions.Identity.Core -- a mesma implementação que gravou
// os hashes no banco do AuthGate. Um vetor que este próprio arquivo produzisse
// provaria só que o leitor concorda consigo mesmo, e concordar consigo mesmo
// não é o risco: o risco é ler o formato do OUTRO lado errado e trancar todo
// mundo do lado de fora.
//
// v3 é o formato corrente (PBKDF2-HMACSHA512, 100000 iterações, sal de 16
// bytes). v2 é o anterior (PBKDF2-HMACSHA1, 1000 iterações), que continua
// aceito porque uma instalação antiga pode ter linhas dele.
const (
	legacyPassword = "uma senha do AuthGate legado"
	legacyV3       = "AQAAAAIAAYagAAAAECObQOPfP4QYUIsusFXW1mHqpJaWJuZKOjMWdMODWx2OP/UelLwOz+7nxvQBeRNkQw=="
	legacyV2       = "AC2m2LtlRSu1t9NuoQ1aMhh0WBA23ZdhZFGVfh7ZiknRdlYeBrkMC3rWAYaBHdo/ew=="
)

func TestArgon2idRoundTrip(t *testing.T) {
	hash, err := Hash("uma frase longa o bastante")
	if err != nil {
		t.Fatalf("derivando: %v", err)
	}
	if !strings.HasPrefix(hash, "$argon2id$") {
		t.Fatalf("hash não está no formato corrente: %q", hash)
	}

	ok, needsRehash, err := Verify(hash, "uma frase longa o bastante")
	if err != nil || !ok {
		t.Fatalf("senha correta recusada: ok=%v err=%v", ok, err)
	}
	if needsRehash {
		t.Fatal("o formato corrente não deveria pedir regravação")
	}

	ok, _, err = Verify(hash, "outra frase qualquer")
	if err != nil || ok {
		t.Fatalf("senha errada aceita: ok=%v err=%v", ok, err)
	}
}

func TestSaltIsPerPassword(t *testing.T) {
	first, err := Hash("a mesma senha")
	if err != nil {
		t.Fatal(err)
	}
	second, err := Hash("a mesma senha")
	if err != nil {
		t.Fatal(err)
	}
	// Sem sal por senha, dois usuários com a mesma senha teriam o mesmo hash --
	// e o banco viraria um índice de quem escolheu a senha mais comum.
	if first == second {
		t.Fatal("duas derivações da mesma senha produziram o mesmo hash")
	}
}

// TestReadsHashesTheAspNetIdentityWrote é a prova de que a verificação dupla do
// ADR 0012 funciona: o hash veio do outro lado, e este lado o lê.
func TestReadsHashesTheAspNetIdentityWrote(t *testing.T) {
	for name, encoded := range map[string]string{"v3": legacyV3, "v2": legacyV2} {
		t.Run(name, func(t *testing.T) {
			ok, needsRehash, err := Verify(encoded, legacyPassword)
			if err != nil {
				t.Fatalf("lendo o formato legado: %v", err)
			}
			if !ok {
				t.Fatal("a senha correta do AuthGate foi recusada")
			}
			// A segunda metade da verificação dupla: acertar a senha num
			// formato antigo é o gatilho para regravá-la no novo.
			if !needsRehash {
				t.Fatal("o formato legado precisa pedir regravação")
			}

			ok, _, err = Verify(encoded, "senha errada")
			if err != nil || ok {
				t.Fatalf("senha errada aceita no formato legado: ok=%v err=%v", ok, err)
			}
		})
	}
}

func TestRejectsBlobsThatAreNotPasswordHashes(t *testing.T) {
	for name, encoded := range map[string]string{
		"vazio":                 "",
		"não é base64":          "isto não é um hash",
		"versão desconhecida":   "BwAAAAIAAYagAAAAEA==",
		"v3 truncado":           "AQAAAAIAAYag",
		"argon2 sem parâmetros": "$argon2id$v=19$$$",
	} {
		t.Run(name, func(t *testing.T) {
			ok, _, err := Verify(encoded, "qualquer coisa")
			if ok {
				t.Fatal("um blob ilegível não pode aprovar senha nenhuma")
			}
			// Erro, e não "senha errada": hash ilegível é linha corrompida, e o
			// login precisa poder distinguir uma coisa da outra para não
			// esconder corrupção atrás de uma tentativa fracassada.
			if !errors.Is(err, ErrUnknownFormat) {
				t.Fatalf("esperava ErrUnknownFormat, veio %v", err)
			}
		})
	}
}

// TestRefusesAbsurdIterationCounts cobre o blob adulterado: os números do
// cabeçalho decidem quanto trabalho este processo faz, e eles vêm do banco.
func TestRefusesAbsurdIterationCounts(t *testing.T) {
	// v3 com 0x7FFFFFFF iterações e sal de 16 bytes.
	const forged = "AQAAAAJ/////AAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="
	ok, _, err := Verify(forged, "qualquer coisa")
	if ok || !errors.Is(err, ErrUnknownFormat) {
		t.Fatalf("um cabeçalho absurdo precisa ser recusado: ok=%v err=%v", ok, err)
	}
}
