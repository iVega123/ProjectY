# Auditoria de Arquitetura e Segurança — ProjectY

Revisão manual do código-fonte dos quatro serviços (AuthGate, MotoHub, RiderManager,
RentalOperations), do `docker-compose.yml`, dos Dockerfiles e dos fluxos de CI.
Nenhum teste dinâmico ou de invasão foi executado. Nenhum código de aplicação foi
alterado por esta revisão.

**42 constatações** — 5 críticas, 11 altas, 14 de arquitetura, 12 de higiene.

---

## Resumo

A separação em quatro serviços está bem desenhada no papel — cada domínio com seu banco,
eventos assíncronos entre eles, logs centralizados. O problema não está na divisão: está em
**não haver perímetro**. Cada serviço reimplementa sua própria autorização por cópia e
colagem, as cópias já divergiram, a chave que assina os tokens é a mesma nos quatro e está
versionada no repositório, e toda a infraestrutura de apoio é publicada diretamente na
máquina hospedeira com credenciais de exemplo.

Três caminhos independentes levam ao dado sensível (CNPJ, número e foto da CNH) sem passar
por controle de acesso nenhum:

| Ponto de entrada aberto | O que atravessa | Onde termina |
|---|---|---|
| `POST /register/admin` (AuthGate :8080, sem authn) | Token com role `Admin`; `JwtKey` idêntica nos 4 serviços | Administração total da plataforma |
| AMQP :5672 (`user`/`password`) | `image_stream_queue`; `userId` vem do corpo da mensagem | Troca a foto de CNH de qualquer pessoa |
| HTTP :9200 / :5601 | Elasticsearch e Kibana sem x-pack | Lê e apaga a trilha de auditoria |

---

## Críticas

### C1 — Qualquer pessoa na internet pode se cadastrar como administrador
`POST /api/auth/register/admin` não tem `[Authorize]`, filtro, convite nem chave. Cria o papel
`Admin` se não existir e devolve um administrador ativo.
**Impacto:** controle administrativo completo dos quatro serviços a partir de uma requisição anônima.
`AuthGate/AuthGate/Controllers/AuthController.cs:47-85`

**Status: closed.** The public endpoint was removed and administrator bootstrap is now an explicit
CLI operation backed by process environment variables. Closed by commit
[`fef0c1f`](https://github.com/iVega123/ProjectY/commit/fef0c1feecdfc8314ffa389eac397fa3d638c110) (C1, task #18).

### C2 — O filtro de administrador do RentalOperations aceita qualquer token válido
O `AdminAuthorizationFilter` tenta claim de papel, chave de API e, por último, apenas
`ValidateToken(token)` — que confere só a assinatura. Um Rider passa pelo terceiro caminho.
Os filtros de MotoHub e RiderManager retornam `isAdmin` corretamente; esta cópia divergiu.
**Impacto:** escalonamento de privilégio; qualquer Rider lê `GET /api/rental/user/{userId}` de terceiros.
`RentalOperations/RentalOperations/Filters/AdminAuthorizationFilter.cs:38-46`

### C3 — Todos os segredos versionados, com JwtKey única nos quatro serviços
Os quatro `appsettings.json` carregam em texto puro: `JwtKey` (mesma string de 32 caracteres),
as três chaves de API entre serviços, e as senhas de Postgres, MongoDB, RabbitMQ e MinIO.
Nenhum serviço valida `iss`/`aud`, então um token vale nos quatro. Existe `.gitleaks.toml`,
mas nenhum fluxo o executa.
**Impacto:** comprometer um serviço — ou ler o repositório — entrega a plataforma inteira.
Rotacionar exige reescrever o histórico do Git.

### C4 — Encerrar um aluguel alheio e transferi-lo para si
`CalculateFinalCostAsync` busca por `rentalId` e nunca compara `rental.UserId` com o usuário
autenticado; na linha 111 faz `response.UserId = userId` antes de gravar.
**Impacto:** falha de controle de acesso somada a adulteração de dado financeiro.
`RentalOperations/RentalOperations/Services/RentalService.cs:76-116`

### C5 — A fila é uma fronteira de confiança implícita, e está aberta
O `userId` que decide de quem é o cadastro e a foto de CNH vem do corpo da mensagem, sem
assinatura nem verificação. A porta 5672 é publicada no host com `user`/`password`.
**Impacto:** escrita direta no domínio, sem passar por API, autenticação ou validação.
`RiderManager/.../MessagingConsumerService.cs:79-122, 171-204` · `docker-compose.yml:81-95`

---

## Altas

- **A1 — Toda a infraestrutura publicada no host.** Postgres 5432, Mongo 27017, RabbitMQ
  5672/15672, MinIO 9000/9001, Elasticsearch 9200/9300, Kibana 5601, pgAdmin 5050
  (`admin@example.com`/`admin`). ES e Kibana sobem sem `xpack.security`. `docker-compose.yml:4-110`
- **A2 — Um Postgres com o mesmo superusuário para três serviços.** Bancos separados, mas
  todos com o usuário `user` que os criou. Um serviço comprometido lê os hashes de senha do
  Identity dos outros.
- **A3 — `ASPNETCORE_ENVIRONMENT=Development` nos quatro contêineres.** Página de exceção
  detalhada e Swagger públicos. O AuthGate chama `UseSwagger()` uma segunda vez fora do
  `if (IsDevelopment)`. `AuthGate/AuthGate/Program.cs:94-101`
- **A4 — Não existe TLS.** O README promete 8181/8001/8101/8201; nenhum certificado é
  configurado, `ASPNETCORE_URLS` é só HTTP e `UseHttpsRedirection()` vira inofensivo sem
  porta HTTPS conhecida. As chamadas entre serviços são `http://` puro.
- **A5 — Upload de CNH validado só por extensão, com `Content-Type` do cliente.** Nunca se
  verificam os bytes iniciais, e `contentType = file.ContentType` é gravado como metadado no
  MinIO e devolvido pela URL pré-assinada → XSS armazenado.
  `RiderManager/.../MinioFileStorageService.cs:33-49`
- **A6 — URL pré-assinada de 24 h para documento de identidade, persistida no banco** e
  devolvida na listagem de entregadores, sem revogação.
  `RiderManager/.../MinioFileStorageService.cs:72-90`
- **A7 — Sem revogação de token; o logout não desliga nada.** `SignOutAsync()` limpa um cookie
  que este fluxo não usa; o JWT segue válido por 1 h. Sem `jti`, versão de credencial ou
  refresh token. `AuthGate/.../AuthController.cs:206-219`
- **A8 — Login sem limite de tentativas.** `lockoutOnFailure: false` e nenhum `AddRateLimiter`
  em nenhum `Program.cs`. `AuthGate/.../AuthController.cs:167`
- **A9 — Exceções internas devolvidas ao cliente.** `BadRequest(ex.Message)` nos cinco
  endpoints do `RentalController`; `ex.Message` concatenado no `MotorcycleService`; as
  exceções dos clientes HTTP carregam o corpo da resposta do serviço interno chamado.
- **A10 — Um canal AMQP vazado por requisição.** `MessagingPublisherService` é *scoped*, abre
  um `IModel` no construtor e não implementa `IDisposable`. O limite padrão é 2.047 canais por
  conexão. Negação de serviço só com tráfego normal, no AuthGate e no MotoHub.
- **A11 — Tratamento de dados pessoais incompatível com a LGPD.** CNPJ, CNH, data de
  nascimento e imagem circulam sem cifra pela fila e ficam sem cifra no banco; os logs no
  Elasticsearch aberto contêm e-mails e IDs; `DeleteRiderAsync` não apaga o objeto no MinIO
  nem a URL pré-assinada.

---

## Arquitetura

- **M1 — Dependência circular síncrona MotoHub ↔ RentalOperations.** Cada um chama o outro por
  HTTP no caminho da requisição. Monólito distribuído.
- **M2 — Nenhuma política de resiliência.** Os três clientes HTTP não têm timeout, repetição,
  disjuntor nem isolamento; vale o padrão de 100 s.
- **M3 — Escrita dupla sem *outbox*.** AuthGate (Identity + publish) e MotoHub (placa + publish)
  confirmam o primeiro passo e não têm compensação se a publicação falhar.
- **M4 — Filas não duráveis e mensagens não persistentes.** Todo `QueueDeclare` usa
  `durable: false` e todo `BasicPublish` passa `basicProperties: null`. Reiniciar o RabbitMQ
  descarta cadastros e imagens em trânsito.
- **M5 — Estado do consumidor na memória do processo.** `riderInfoRetryCounts` e
  `imagePartsStore` impedem uma segunda réplica; o buffer não tem limite nem expiração
  (exaustão de memória) e a `List<ImagePart>` compartilhada é alterada sem sincronização.
- **M6 — `BasicNack(requeue: true)` vira laço quente.** Com `prefetchCount: 1`, uma mensagem
  defeituosa em `ProcessImageStream` é reentregue indefinidamente em CPU cheia.
- **M7 — Fila de mensagens mortas órfã no RiderManager.** Publica em `"RiderInfoPoisonQueue"`
  (literal) e consome `rider_info_poison_queue` (configuração). São filas distintas.
- **M8 — `ConsumePoisonQueue` tende a derrubar o serviço.** A fila nunca é declarada, e um
  `EventingBasicConsumer` síncrono é registrado numa conexão `DispatchConsumersAsync = true`.
  A chamada é feita sem `await`, então a exceção escapa pelo `BackgroundService`.
- **M9 — `Database.EnsureCreated()` no construtor de um `DbContext` *scoped*.** Roda a cada
  requisição, e é mutuamente exclusivo com as migrações — as pastas `Migrations/` nunca são
  aplicadas e o esquema não evolui.
- **M10 — Sem gateway; autorização reimplementada por cópia.** Filtros, `RabbitMQOptions`,
  serviços de mensageria e entidades de contrato duplicados nos quatro serviços, sem
  biblioteca compartilhada. C2 e M7 são exatamente os desvios dessa duplicação.
- **M11 — Regra de sobreposição de aluguéis invertida, e sem proteção contra corrida.**
  `StartDate < rent.EndDate || PredictedEndDate > rent.StartDate` deveria ser conjunção; com
  `EndDate = DateTime.MinValue` no aluguel aberto, quase toda criação é recusada. A leitura
  seguida de escrita não tem transação nem índice único → *double booking* sob concorrência.
- **M12 — Exclusão de moto com janela de corrida e remoção física.** Consulta remota
  "está alugada?" e apaga em seguida; aluguéis no MongoDB ficam órfãos.
- **M13 — Nenhuma listagem tem paginação.** N+1 em `GetAllRidersAsync` (uma consulta e uma
  chamada ao MinIO por entregador, com o resultado descartado); no Mongo,
  `IsMotorcycleCurrentlyRentedAsync` carrega tudo e filtra em C#, sem índices declarados.
- **M14 — Sem verificação de saúde; `sleep 20` no lugar de ordenação.** Nenhum
  `AddHealthChecks`, nenhum `healthcheck` no compose. O `command: sh -c` põe o shell como
  PID 1, então SIGTERM não chega à aplicação — sem encerramento gracioso. RiderManager e
  RentalOperations ainda têm o `ENTRYPOINT` comentado no Dockerfile.

---

## Higiene de código e de entrega

- **B1 — `UseAuthentication()` ausente** em AuthGate e RentalOperations. Só funciona porque o
  *minimal hosting* do .NET 8 insere o middleware automaticamente.
- **B2 — `[Authorize]` e os filtros próprios se contradizem.** O `[Authorize]` roda antes, no
  middleware, e barra com 401 as chamadas entre serviços que trazem só `X-API-Key`;
  `GetByLicensePlate` e `GetRiderByUserId` ficaram sem o atributo, sem motivo aparente.
- **B3 — `role.Contains("Admin")` sobre valor possivelmente nulo** em `UpdateRiderCNH` → 500.
  `FirstOrDefault` também olha só o primeiro papel, e `Contains` compara por substring.
  `RiderManager/.../RiderController.cs:57-64`
- **B4 — A esteira de CI não roda.** Os fluxos estão em `<Serviço>/.github/workflows/`; o
  GitHub só lê `.github/workflows/` na raiz, e não existe `.github` na raiz do ProjectY. Não
  há SAST, análise de dependências vulneráveis nem varredura de imagem; o gitleaks nunca é
  executado; e o fluxo do MotoHub exclui `CrossCutting`, `RabbitMQ` e `Configurations` da
  análise do Sonar — exatamente onde vivem as chaves de API e a mensageria.
- **B5 — Os `.dockerignore` não têm efeito.** O `context` das quatro construções é a raiz do
  repositório e o Docker só lê o `.dockerignore` da raiz do contexto, que não existe — então
  `COPY . .` leva o monorepo inteiro, incluindo `.git`. Os próprios arquivos ainda reincluem
  `.git/config` e `.git/refs/heads/**` explicitamente.
- **B6 — Imagens sem versão fixa:** `mongo:latest`, `minio/minio:latest`.
- **B7 — Validação de entrada inconsistente.** `MotorcycleDTO` não tem nenhum atributo; as
  expressões de CNPJ não têm âncora `^…$` nem verificação de dígito; e em `RiderUser` o
  `StringLength(11)` convive com uma expressão que exige o prefixo `cnh`/`habilitação` antes
  dos onze dígitos — regra impossível de satisfazer.
- **B8 — Nomes de objeto no MinIO previsíveis e sujeitos a colisão:** `yyyyMMddHHmmssfff` mais
  extensão, sem prefixo por usuário nem parte aleatória, com índice único sobre `ObjectName`.
- **B9 — Valores interpolados em URLs internas sem `Uri.EscapeDataString`.**
- **B10 — `Rider.Id` e `Rider.UserId` usados como se fossem a mesma chave.**
  `GetOrCreatePresignedUrlAsync` busca por `Id`, `StorePresignedUrlAsync` por `UserId`; o
  caminho com imagem de `AddRiderAsync` lança `ArgumentException("Rider not found")`. Hoje só
  não quebra porque o consumidor da fila nunca preenche `CNHImagePath`.
- **B11 — Banco divergente:** o compose cria `BikeBookingDB`, o AuthGate conecta em
  `BikeGuardianDB`.
- **B12 — `Microsoft.AspNetCore.ApiAuthorization.IdentityServer` 7.0.18** referenciado em um
  projeto `net8.0` e não utilizado.

---

## Ordem de correção sugerida

A sequência importa: rotacionar segredos antes de tirá-los do código não adianta, e endurecer
os filtros não ajuda enquanto qualquer um puder criar um administrador.

1. **Fechar as três portas abertas.** Exigir autenticação de administrador em
   `/register/admin` (ou substituí-lo por semeadura controlada); corrigir o retorno do
   `AdminAuthorizationFilter` do RentalOperations; validar o dono do aluguel em
   `CalculateFinalCostAsync` e parar de sobrescrever o `UserId`. — C1, C2, C4
2. **Tirar os segredos do repositório e trocá-los.** Variáveis de ambiente ou cofre; chave de
   assinatura distinta por serviço, com `iss`/`aud` validados; credenciais próprias por banco,
   broker e bucket. Tudo que já está no histórico deve ser considerado comprometido. Ligar o
   gitleaks na esteira. — C3
3. **Estabelecer um perímetro.** Remover `ports:` de tudo que não precisa ser alcançado de
   fora; ligar autenticação em Elasticsearch, Kibana e MinIO; trocar `Development` por
   `Production`; terminar TLS em um *ingress* — ou remover a promessa de HTTPS do README
   enquanto ela não for verdade. — A1, A3, A4
4. **Tornar a mensageria confiável.** Filas duráveis, mensagens persistentes, *publisher
   confirms*, DLQ de verdade no lugar do reenfileiramento infinito, e *outbox* nos dois pontos
   de escrita dupla. Tratar o conteúdo da mensagem como não confiável. Tirar o estado de
   retentativa e o buffer de imagens da memória do processo. — C5, M3–M8
5. **Extrair o que está duplicado.** Biblioteca compartilhada para autenticação, contratos de
   mensagem e configuração do broker — ou um gateway que centralize a autenticação. Enquanto
   os filtros forem quatro cópias editáveis em paralelo, o próximo C2 é questão de tempo. — M10
