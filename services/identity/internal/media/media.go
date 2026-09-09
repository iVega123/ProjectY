// Package media leva a foto da CNH até o armazenamento de objetos, passando
// pelo media-guard.
//
// A divisão arquivo/registro do ADR 0012 sobrevive inteira: o media-guard é
// dono do PIPELINE do arquivo -- valida os bytes de verdade, tira EXIF,
// recodifica -- e o identity é dono do REGISTRO, a linha que diz qual piloto,
// qual objeto. Este pacote é a costura entre os dois, e não reimplementa
// nenhum dos dois lados.
//
// O identity nunca escreve no bucket o que chegou pela rede. O que vai para o
// objeto é o PNG que o media-guard devolveu, e é isso que impede um HTML
// renomeado para .png de virar um objeto servido por URL assinada.
package media

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"time"

	"github.com/minio/minio-go/v7"
	"github.com/minio/minio-go/v7/pkg/credentials"
)

// MaxUploadBytes é o teto do que se aceita receber. O media-guard tem o dele; o
// ponto deste é não carregar 100 MiB na memória do emissor de tokens antes de
// descobrir que o outro lado ia recusar.
const MaxUploadBytes = 8 << 20

// ErrRejected é conteúdo que o media-guard recusou -- não é imagem, ou não tem
// dimensões aceitáveis. É culpa de quem enviou.
var ErrRejected = errors.New("imagem recusada")

// ErrUnavailable é o media-guard fora do ar. Não é culpa de quem enviou, e a
// resposta certa é pedir para tentar de novo.
var ErrUnavailable = errors.New("processamento de imagem indisponível")

// Sanitized é o que volta do media-guard.
type Sanitized struct {
	Image     []byte
	Thumbnail []byte
}

// Guard fala com o media-guard.
type Guard struct {
	client  *http.Client
	baseURL string
}

// NewGuard prende o cliente ao endereço do media-guard.
func NewGuard(baseURL string) *Guard {
	return &Guard{
		client:  &http.Client{Timeout: 15 * time.Second},
		baseURL: baseURL,
	}
}

type wireImage struct {
	Image       string `json:"image"`
	Thumbnail   string `json:"thumbnail"`
	ContentType string `json:"contentType"`
}

// Sanitize manda os bytes crus e recebe o PNG saneado e a miniatura.
func (g *Guard) Sanitize(ctx context.Context, raw []byte) (Sanitized, error) {
	if len(raw) == 0 || len(raw) > MaxUploadBytes {
		return Sanitized{}, fmt.Errorf("%w: a imagem precisa ter entre 1 byte e 8 MiB", ErrRejected)
	}

	request, err := http.NewRequestWithContext(
		ctx, http.MethodPost, g.baseURL+"/sanitize", bytes.NewReader(raw))
	if err != nil {
		return Sanitized{}, err
	}
	response, err := g.client.Do(request)
	if err != nil {
		return Sanitized{}, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	defer func() { _ = response.Body.Close() }()

	switch {
	case response.StatusCode == http.StatusUnprocessableEntity,
		response.StatusCode == http.StatusRequestEntityTooLarge:
		return Sanitized{}, fmt.Errorf("%w: conteúdo ou dimensões inválidos", ErrRejected)
	case response.StatusCode != http.StatusOK:
		return Sanitized{}, fmt.Errorf("%w: media-guard respondeu %d", ErrUnavailable, response.StatusCode)
	}

	var decoded wireImage
	if err := json.NewDecoder(io.LimitReader(response.Body, 32<<20)).Decode(&decoded); err != nil {
		return Sanitized{}, fmt.Errorf("%w: resposta ilegível", ErrUnavailable)
	}
	// O tipo é conferido porque é a garantia que se está comprando. Aceitar o
	// que voltar sem olhar transformaria o saneamento numa formalidade.
	if decoded.ContentType != "image/png" {
		return Sanitized{}, fmt.Errorf("%w: tipo saneado inesperado", ErrRejected)
	}

	image, err := base64.StdEncoding.DecodeString(decoded.Image)
	if err != nil {
		return Sanitized{}, fmt.Errorf("%w: imagem ilegível", ErrUnavailable)
	}
	thumbnail, err := base64.StdEncoding.DecodeString(decoded.Thumbnail)
	if err != nil {
		return Sanitized{}, fmt.Errorf("%w: miniatura ilegível", ErrUnavailable)
	}
	return Sanitized{Image: image, Thumbnail: thumbnail}, nil
}

// Objects guarda e serve o que foi saneado.
type Objects struct {
	client *minio.Client
	bucket string
}

// NewObjects abre o cliente do armazenamento.
func NewObjects(endpoint, accessKey, secretKey, bucket string, secure bool) (*Objects, error) {
	client, err := minio.New(endpoint, &minio.Options{
		Creds:  credentials.NewStaticV4(accessKey, secretKey, ""),
		Secure: secure,
	})
	if err != nil {
		return nil, err
	}
	return &Objects{client: client, bucket: bucket}, nil
}

// EnsureBucket cria o bucket se ele ainda não existir.
func (o *Objects) EnsureBucket(ctx context.Context) error {
	exists, err := o.client.BucketExists(ctx, o.bucket)
	if err != nil {
		return err
	}
	if exists {
		return nil
	}
	return o.client.MakeBucket(ctx, o.bucket, minio.MakeBucketOptions{})
}

// Put grava a imagem e a miniatura, e devolve a chave do objeto.
//
// O prefixo é o SHA-256 do identificador do piloto, e não o identificador. Uma
// listagem do bucket -- que é uma permissão bem mais fácil de vazar que a de
// leitura -- deixaria de ser um índice de quem tem conta.
func (o *Objects) Put(ctx context.Context, riderID string, sanitized Sanitized) (string, error) {
	prefix := sha256.Sum256([]byte(riderID))
	suffix := make([]byte, 16)
	if _, err := rand.Read(suffix); err != nil {
		return "", err
	}
	key := fmt.Sprintf("riders/%s/%s.png", hex.EncodeToString(prefix[:]), hex.EncodeToString(suffix))

	if err := o.put(ctx, key, sanitized.Image); err != nil {
		return "", err
	}
	if err := o.put(ctx, key+".thumb.png", sanitized.Thumbnail); err != nil {
		return "", err
	}
	return key, nil
}

func (o *Objects) put(ctx context.Context, key string, content []byte) error {
	_, err := o.client.PutObject(ctx, o.bucket, key,
		bytes.NewReader(content), int64(len(content)),
		minio.PutObjectOptions{ContentType: "image/png"})
	return err
}

// Presign devolve uma URL de leitura com prazo.
//
// Com prazo, e curto: o objeto é uma CNH. Uma URL sem validade é uma permissão
// permanente entregue a quem quer que veja a resposta uma vez.
func (o *Objects) Presign(ctx context.Context, key string, ttl time.Duration) (string, error) {
	url, err := o.client.PresignedGetObject(ctx, o.bucket, key, ttl, nil)
	if err != nil {
		return "", err
	}
	return url.String(), nil
}

// Remove apaga o objeto e a miniatura.
func (o *Objects) Remove(ctx context.Context, key string) error {
	if err := o.client.RemoveObject(ctx, o.bucket, key, minio.RemoveObjectOptions{}); err != nil {
		return err
	}
	return o.client.RemoveObject(ctx, o.bucket, key+".thumb.png", minio.RemoveObjectOptions{})
}
