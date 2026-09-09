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
| `GET /api/riders?ids=a,b,c` | lote de pilotos, administrador, teto de 100 |
| `GET /api/riders/{userId}` | o próprio piloto, ou um administrador |
| `DELETE /api/riders/{userId}` | administrador |
| `PUT /update-image` | a foto da CNH do piloto que o envelope nomeia |
| `GET /health/{live,ready,startup}` | as três sondas |

As quatro rotas de piloto exigem o **envelope de identidade** do [ADR
0008](../../docs/adr/0008-single-trust-boundary.md); as de credencial e o JWKS
são públicas, porque são o que alguém faz antes de ter um token. O identity não
valida token nenhum: o portão é a única fronteira que faz isso, e um segundo
lugar onde a validação acontece é um segundo lugar onde ela pode divergir.

O documento de descoberta **não** faz deste serviço um provedor OIDC: não há
endpoint de autorização, nem consentimento, nem clientes registrados, e o
`issuer` é um nome opaco porque é assim que o portão o compara. O [ADR
0013](../../docs/adr/0013-asymmetric-token-signing.md) pediu o documento porque
ele é barato e torna o serviço legível para ferramenta padrão — é isso que ele
entrega, e nada além.

## O domínio do piloto

O registro regulatório -- CNPJ, CNH, data de nascimento, o ponteiro para o
objeto que o media-guard guardou -- mora aqui, em `riders`, ao lado da
credencial em `users`. O [ADR
0023](../../docs/adr/0023-the-rider-record-lives-with-the-credential.md) explica
por que o `rider-core` separado do ADR 0012 não se justificou, e o que a fusão
custa.

`riders.user_id` é chave primária **e** estrangeira: um piloto não tem
identificador próprio. É o achado B10 impossibilitado pelo schema em vez de
evitado por convenção.

Toda escrita grava o fato no outbox dentro da mesma transação:

| | |
|---|---|
| `rider.registered` | cadastro |
| `rider.verified` + `.v2` | cadastro, veredito do OCR, remoção |
| `document.stored` | a CNH foi armazenada, e onde |
| `document.verified` | **consumido**, do risk-pricing |

O veredito do OCR só pode **derrubar**. Um documento que não confere revoga; um
que confere devolve o que o tipo de CNH já dizia. Deixar o OCR conceder faria
uma habilitação categoria B virar alugável por ter mandado uma foto legível.

A foto nunca vai crua para o bucket: o que é gravado é o PNG que o media-guard
devolveu. É a divisão arquivo/registro do ADR 0012 -- ele é dono do pipeline do
arquivo, este serviço é dono da linha que diz qual piloto, qual objeto.

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

O código do AuthGate saiu do repositório no #136, e o importador ficou. Não é
esquecimento: ele lê um banco no formato do ASP.NET Identity, e esse banco não é
uma coisa que este repositório possua -- é o que uma instalação de verdade tem.
Apagar o importador porque o CÓDIGO do serviço de origem saiu confundiria "não
mantemos mais o serviço" com "ninguém tem os dados dele"; e sem ele o leitor de
PBKDF2 vira decoração, porque nada mais conseguiria colocar um hash daqueles na
tabela.

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
| `GATEWAY_IDENTITY_SIGNING_KEY` | obrigatória -- a chave com que o portão assina o envelope |
| `IDENTITY_ENVELOPE_AUDIENCE` | obrigatória, e precisa bater com `GATEWAY_JWT_AUDIENCE_IDENTITY` |
| `MEDIA_GUARD_URL` | `http://media-guard:8080` |
| `MINIO_ENDPOINT` / `_ACCESS_KEY` / `_SECRET_KEY` / `_BUCKET` | armazenamento da CNH |
| `KAFKA_BOOTSTRAP_SERVERS` | opcional; sem ele os fatos ficam retidos no outbox |
| `SCHEMA_REGISTRY_URL` | usado só para resolver o id de schema, uma vez por tópico |
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

## Tipos de evento

Os tipos Go de `contracts/events` são **versionados**, ao contrário do que fazem
o billing e o rental-core, que geram no build. O motivo é o dev loop: gerar no
build obrigaria quem roda `go test ./...` a ter protoc instalado, e a linguagem
inteira parte do princípio de que `go test` funciona sozinho.

O que impede a cópia de envelhecer é o CI, que roda `./generate-events.sh` e
recusa o PR se o resultado diferir do que está versionado. A geração roda dentro
do container para o cabeçalho do arquivo gerado -- que carrega a versão do
protoc -- ser o mesmo na máquina de quem escreve e no CI.
