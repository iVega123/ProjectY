# identity

Emite os tokens da plataforma e publica o JWKS que o portão usa para
validá-los. Em Go — por razões de ecossistema, e o [ADR
0012](../../docs/adr/0012-identity-and-rider-domains.md) diz isso em vez de
inventar um argumento de carga depois do fato.

## O que ele serve

| | |
|---|---|
| `POST /api/auth/register/rider` | cadastro de piloto: credencial, papel e registro regulatório numa transação |
| `POST /api/auth/login` | e-mail e senha, devolve access token e refresh token |
| `POST /api/auth/refresh` | troca o refresh token pelo próximo da mesma família |
| `POST /api/auth/logout` | revoga a família inteira |
| `GET /.well-known/jwks.json` | as chaves públicas, selecionáveis por `kid` |
| `GET /.well-known/openid-configuration` | documento com a forma da descoberta OIDC |
| `GET /health/{live,ready,startup}` | as três sondas |

O documento de descoberta **não** faz deste serviço um provedor OIDC: não há
endpoint de autorização, nem consentimento, nem clientes registrados, e o
`issuer` é um nome opaco porque é assim que o portão o compara. O [ADR
0013](../../docs/adr/0013-asymmetric-token-signing.md) pediu o documento porque
ele é barato e torna o serviço legível para ferramenta padrão — é isso que ele
entrega, e nada além.

## A migração de senhas, e por que ela é o trabalho de verdade

O AuthGate guarda PBKDF2 no formato próprio do ASP.NET Identity — marcador de
versão, PRF, contagem de iterações e sal empacotados num blob em base64. Havia
três saídas:

| | |
|---|---|
| **1. Ler o formato antigo para sempre** | Funciona, e nunca termina: o formato legado vira permanente. |
| **2. Verificação dupla** | Confere no formato antigo, regrava em Argon2id no primeiro login bem-sucedido. O formato legado sai sozinho, à medida que as pessoas entram. |
| **3. Re-semear** | Legítimo aqui, porque o dado é fictício — e é a única opção que não se pode usar num sistema com usuários reais. |

**A escolha é a (2)**, e ela tem duas metades que precisam existir juntas:

- `internal/passwords` lê os formatos v2 e v3 do ASP.NET Identity e sinaliza
  `needsRehash`; o login regrava em Argon2id no único momento em que a senha em
  claro existe neste processo.
- `identity import-authgate` copia as linhas do banco do AuthGate com o hash
  **intacto** — ele não pode fazer outra coisa, porque a senha em claro não
  existe em lugar nenhum. É o que dá à primeira metade o que ler.

O leitor do formato antigo tem teste contra um vetor gerado pelo
`PasswordHasher<T>` do próprio ASP.NET Identity, e não por este repositório.
Um vetor auto-produzido provaria que o leitor concorda consigo mesmo, que não é
o risco: o risco é ler o formato do outro lado errado e trancar todo mundo do
lado de fora.

Custo aceito: o leitor de PBKDF2 fica no código até o último hash legado ser
regravado. Isso é observável — `SELECT count(*) FROM users WHERE password_hash
NOT LIKE '$argon2id$%'` — e não uma esperança.

## As chaves

Ed25519, guardadas em `signing_keys`, com a semente **selada em AES-256-GCM**
sob `IDENTITY_KEY_ENCRYPTION_KEY`. O ADR 0013 afirma que nenhum validador
carrega segredo capaz de assinar; sem o selo, essa afirmação valeria só para o
caminho da rede, e não para quem tem o backup.

Rotação é um comando:

```bash
docker compose exec identity /app/identity rotate-keys
```

Ele promove uma chave nova e rebaixa a anterior, que **continua publicada no
JWKS** até `IDENTITY_KEY_ROTATION_OVERLAP_SECONDS`. É isso que faz um token já
emitido sobreviver à rotação: ele carrega o `kid` antigo, o portão ainda acha a
pública correspondente, e a validação passa. A configuração recusa uma
sobreposição menor que a vida do access token.

## Configuração

| Variável | |
|---|---|
| `IDENTITY_DATABASE_URL` | obrigatória |
| `IDENTITY_AUDIENCES` | obrigatória, separada por vírgula — ver [ADR 0024](../../docs/adr/0024-one-token-many-audiences.md) |
| `IDENTITY_KEY_ENCRYPTION_KEY` | obrigatória, mínimo 32 bytes |
| `IDENTITY_ISSUER` | `projecty.identity` |
| `IDENTITY_ACCESS_TOKEN_TTL_SECONDS` | `300` — e o portão recusa acima disso |
| `IDENTITY_REFRESH_TOKEN_TTL_SECONDS` | `604800` |
| `IDENTITY_KEY_ROTATION_OVERLAP_SECONDS` | `900` |
| `IDENTITY_ADMIN_EMAIL` / `IDENTITY_ADMIN_PASSWORD` | opcionais, e vêm em par |

## Testes

Os testes falam com um CockroachDB de verdade. Metade do que este serviço
promete está escrita em SQL — o índice único parcial que decide qual réplica
gera a primeira chave, o `UPDATE ... RETURNING` que consome um refresh token sem
corrida — e nada disso existe num duplo.

```bash
docker run --rm -d -p 26257:26257 cockroachdb/cockroach:v26.3.1 start-single-node --insecure
IDENTITY_TEST_COCKROACH=127.0.0.1:26257 go test -p 1 ./...
```

`-p 1` porque os pacotes dividem um banco só, e aplicar o schema em paralelo faz
o CockroachDB recusar com `table is being added`.
