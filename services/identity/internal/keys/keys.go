// Package keys guarda o par Ed25519 que assina os tokens e publica a metade
// pública como JWKS.
//
// O ADR 0013 troca HMAC por audiência por assinatura assimétrica, e a frase que
// justifica a troca é esta: sob HMAC, a chave que VALIDA um token também o
// EMITE. Todo serviço que verificava carregava um segredo capaz de forjar os
// próprios chamadores. Aqui só o identity tem privada, e o portão baixa uma
// chave pública que não assina nada.
//
// A privada não fica em claro nem no banco. Se ficasse, quem lê o banco --
// backup, réplica, quem depurar -- passaria a ser um emissor, e a afirmação do
// ADR 0013 valeria só para o caminho da rede.
package keys

import (
	"context"
	"crypto"
	"crypto/aes"
	"crypto/cipher"
	"crypto/ed25519"
	"crypto/rand"
	"database/sql"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/iVega123/ProjectY/services/identity/internal/dbx"
)

const (
	statusActive   = "active"
	statusRetiring = "retiring"
)

// Key é uma chave de assinatura. Só a ativa carrega a privada: uma chave em
// retirada existe para o portão continuar validando o que já foi emitido, e
// para isso a pública basta.
type Key struct {
	ID      string
	Public  ed25519.PublicKey
	private ed25519.PrivateKey
}

// NewKey monta uma chave a partir de uma semente Ed25519 de 32 bytes.
//
// É o caminho para quem já tem o material da chave em outro lugar -- um teste
// com vetor fixo hoje, um KMS que devolve a semente amanhã -- sem passar pelo
// banco.
func NewKey(id string, seed []byte) (Key, error) {
	if len(seed) != ed25519.SeedSize {
		return Key{}, fmt.Errorf("semente Ed25519 precisa de %d bytes", ed25519.SeedSize)
	}
	private := ed25519.NewKeyFromSeed(seed)
	public, ok := private.Public().(ed25519.PublicKey)
	if !ok {
		return Key{}, errors.New("chave gerada não é Ed25519")
	}
	return Key{ID: id, Public: public, private: private}, nil
}

// Signer entrega a chave como assinador, que é a única forma dela sair daqui.
// O tipo concreto fica no pacote; o que atravessa é a capacidade de assinar.
func (k Key) Signer() crypto.Signer {
	return k.private
}

// Ring é o conjunto publicado: a que assina agora, e as que ainda validam.
type Ring struct {
	mutex  sync.RWMutex
	active Key
	jwks   []byte
}

// NewRing monta um chaveiro de uma chave só, sem passar pelo banco.
func NewRing(active Key) (*Ring, error) {
	document, err := json.Marshal(jwkSet{Keys: []jwk{publicJwk(active.ID, active.Public)}})
	if err != nil {
		return nil, err
	}
	return &Ring{active: active, jwks: document}, nil
}

// Active devolve a chave que assina.
func (r *Ring) Active() Key {
	r.mutex.RLock()
	defer r.mutex.RUnlock()
	return r.active
}

// Adopt troca o conteúdo deste chaveiro pelo de outro, recém-lido do banco.
//
// Substituir o conteúdo em vez do ponteiro é o que permite entregar o *Ring uma
// vez, na montagem, e ainda assim ver uma rotação feita por outra réplica. Sem
// isso, quem já segurava o chaveiro continuaria assinando com a chave anterior.
func (r *Ring) Adopt(other *Ring) {
	other.mutex.RLock()
	active, jwks := other.active, other.jwks
	other.mutex.RUnlock()

	r.mutex.Lock()
	defer r.mutex.Unlock()
	r.active, r.jwks = active, jwks
}

// JWKS devolve o documento pronto para servir.
func (r *Ring) JWKS() []byte {
	r.mutex.RLock()
	defer r.mutex.RUnlock()
	return r.jwks
}

// Store lê e escreve as chaves, selando a privada na ida e abrindo na volta.
type Store struct {
	db      *sql.DB
	sealing [32]byte
}

// NewStore prende o store à chave que sela as sementes.
func NewStore(db *sql.DB, sealing [32]byte) *Store {
	return &Store{db: db, sealing: sealing}
}

// Load monta o chaveiro corrente, criando a primeira chave se o banco estiver
// vazio.
//
// Duas réplicas subindo juntas contra um banco vazio geram chaves diferentes e
// tentam inserir as duas como ativas. O índice único parcial de
// 005_identity.sql decide quem ganha; a perdedora recebe violação de unicidade
// e relê. Sem lock de aplicação, e sem a janela em que as duas publicam JWKS
// diferentes.
func (s *Store) Load(ctx context.Context) (*Ring, error) {
	if err := s.prune(ctx); err != nil {
		return nil, err
	}

	ring, err := s.read(ctx)
	if err == nil {
		return ring, nil
	}
	if !errors.Is(err, errNoActiveKey) {
		return nil, err
	}

	if err := s.insert(ctx, statusActive, nil); err != nil && !dbx.IsUniqueViolation(err) {
		return nil, err
	}
	return s.read(ctx)
}

// Rotate promove uma chave nova e rebaixa a anterior.
//
// A anterior continua no JWKS até `retire_after`, e é isso que faz um token já
// emitido sobreviver à rotação: ele carrega o `kid` antigo, o portão ainda
// encontra a pública correspondente, e a validação passa. Rotação é uma
// atualização de JWKS, não uma distribuição de segredo -- que é exatamente a
// propriedade que o HMAC não tinha.
func (s *Store) Rotate(ctx context.Context, overlap time.Duration) (*Ring, error) {
	transaction, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		return nil, err
	}
	defer func() { _ = transaction.Rollback() }()

	retireAfter := time.Now().UTC().Add(overlap)
	if _, err := transaction.ExecContext(ctx,
		`UPDATE signing_keys SET status = $1, retire_after = $2 WHERE status = $3`,
		statusRetiring, retireAfter, statusActive,
	); err != nil {
		return nil, err
	}
	if err := insertInto(ctx, transaction, s.sealing, statusActive, nil); err != nil {
		return nil, err
	}
	if err := transaction.Commit(); err != nil {
		return nil, err
	}

	if err := s.prune(ctx); err != nil {
		return nil, err
	}
	return s.read(ctx)
}

var errNoActiveKey = errors.New("nenhuma chave ativa")

func (s *Store) read(ctx context.Context) (*Ring, error) {
	rows, err := s.db.QueryContext(ctx,
		`SELECT kid, public_key, sealed_seed, seal_nonce, status
		   FROM signing_keys
		  ORDER BY status, created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer func() { _ = rows.Close() }()

	ring := &Ring{}
	document := jwkSet{Keys: []jwk{}}
	found := false
	for rows.Next() {
		var kid, status string
		var public, sealed, nonce []byte
		if err := rows.Scan(&kid, &public, &sealed, &nonce, &status); err != nil {
			return nil, err
		}
		document.Keys = append(document.Keys, publicJwk(kid, public))
		if status != statusActive {
			continue
		}
		// Só a ativa é aberta. Uma chave em retirada não assina mais nada, e
		// abrir a semente dela seria expor um segredo sem uso.
		seed, err := unseal(s.sealing, nonce, sealed)
		if err != nil {
			return nil, fmt.Errorf("abrindo a semente de %s: %w", kid, err)
		}
		ring.active = Key{
			ID:      kid,
			Public:  ed25519.PublicKey(public),
			private: ed25519.NewKeyFromSeed(seed),
		}
		found = true
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	if !found {
		return nil, errNoActiveKey
	}

	encoded, err := json.Marshal(document)
	if err != nil {
		return nil, err
	}
	ring.jwks = encoded
	return ring, nil
}

// prune apaga as chaves cuja sobreposição já passou. Um JWKS que só cresce
// acaba maior que o limite que o portão aceita baixar, e aí a rotação seguinte
// derruba a autenticação inteira.
func (s *Store) prune(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx,
		`DELETE FROM signing_keys
		  WHERE status = $1 AND retire_after IS NOT NULL AND retire_after < $2`,
		statusRetiring, time.Now().UTC())
	return err
}

func (s *Store) insert(ctx context.Context, status string, retireAfter *time.Time) error {
	return insertInto(ctx, s.db, s.sealing, status, retireAfter)
}

type execer interface {
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
}

func insertInto(
	ctx context.Context,
	target execer,
	sealing [32]byte,
	status string,
	retireAfter *time.Time,
) error {
	public, private, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return err
	}
	nonce, sealed, err := seal(sealing, private.Seed())
	if err != nil {
		return err
	}
	_, err = target.ExecContext(ctx,
		`INSERT INTO signing_keys (kid, public_key, sealed_seed, seal_nonce, status, retire_after)
		 VALUES ($1, $2, $3, $4, $5, $6)`,
		newKeyID(), []byte(public), sealed, nonce, status, retireAfter)
	return err
}

// newKeyID produz um `kid` legível e único. A data na frente é para quem lê um
// JWKS meses depois conseguir dizer qual chave é a nova sem consultar o banco.
func newKeyID() string {
	suffix := make([]byte, 4)
	if _, err := rand.Read(suffix); err != nil {
		// crypto/rand falhando é o processo não ter mais entropia; seguir com
		// um kid previsível seria pior que parar.
		panic(fmt.Sprintf("sorteando identificador de chave: %v", err))
	}
	return time.Now().UTC().Format("20060102") + "-" + hex.EncodeToString(suffix)
}

type jwkSet struct {
	Keys []jwk `json:"keys"`
}

type jwk struct {
	Kty string `json:"kty"`
	Crv string `json:"crv"`
	X   string `json:"x"`
	Kid string `json:"kid"`
	Alg string `json:"alg"`
	Use string `json:"use"`
}

func publicJwk(kid string, public []byte) jwk {
	return jwk{
		Kty: "OKP",
		Crv: "Ed25519",
		X:   base64.RawURLEncoding.EncodeToString(public),
		Kid: kid,
		Alg: "EdDSA",
		Use: "sig",
	}
}

func seal(key [32]byte, seed []byte) (nonce, ciphertext []byte, err error) {
	aead, err := newAEAD(key)
	if err != nil {
		return nil, nil, err
	}
	nonce = make([]byte, aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, nil, err
	}
	return nonce, aead.Seal(nil, nonce, seed, nil), nil
}

func unseal(key [32]byte, nonce, ciphertext []byte) ([]byte, error) {
	aead, err := newAEAD(key)
	if err != nil {
		return nil, err
	}
	if len(nonce) != aead.NonceSize() {
		return nil, errors.New("nonce com tamanho inesperado")
	}
	return aead.Open(nil, nonce, ciphertext, nil)
}

func newAEAD(key [32]byte) (cipher.AEAD, error) {
	block, err := aes.NewCipher(key[:])
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(block)
}
