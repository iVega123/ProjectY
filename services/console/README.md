# console

O BFF. Ele compõe a tela e não decide nada sobre segurança.

Em Next.js, e a divisão importa mais do que a moldura: tudo que fala com o
resto do sistema roda no **servidor** deste serviço, com o token do usuário num
cookie `httpOnly`. O navegador nunca vê o token e nunca chama um serviço
diretamente.

## Por que a composição mora aqui

O [ADR 0014](../../docs/adr/0014-read-aggregation-at-the-bff.md) decide isto, e
a razão é taxa de mudança, não linguagem:

| | Portão | Agregação de leitura |
|---|---|---|
| Muda | raramente -- é fronteira de segurança | toda vez que uma tela muda |
| Falha | **fechado** -- sem token válido, sem entrada | **suave** -- renderiza o que chegou |

Costurar Aluguel a Piloto dentro do portão acoplaria um artefato que muda toda
semana ao que valida token. O BFF pode saber a forma da tela porque ele é
implantado **com** a tela.

O console continua chamando pelo portão, com o token do usuário. Falar direto
com os serviços faria dele um segundo lugar que decide quem é quem -- que é
exatamente o que o ADR 0008 existe para impedir.

## A tela de aluguéis

Uma página custa **quatro chamadas**, e o número não muda com o número de
linhas:

| | |
|---|---|
| `GET /api/Rental/user` | a página de aluguéis |
| `GET /api/motorcycles/batch?ids=` | modelo e ano das motos da página |
| `GET /api/invoices?rentalIds=` | o que cada aluguel fechado custou de verdade |
| `GET /api/riders/{id}` | o nome de quem está logado |

As três últimas saem **em paralelo**, então a tela paga a mais lenta e não a
soma. `test/compose.test.ts` conta as chamadas com uma linha e com cem e exige o
mesmo resultado: é o que impede um `await` dentro de um `map` de voltar sem que
ninguém perceba.

O padrão DataLoader sobreviveu; a tecnologia não. O que ele faz de fato é juntar
as chaves de uma requisição numa chamada só -- e isso exige um endpoint de lote
do outro lado. Por isso o #138 trata os lotes como **contrato**: sem eles o N+1
não desaparece, só se muda do navegador para cá.

A leitura do piloto é única e não em lote de propósito: a tela mostra os
aluguéis de quem pediu, então o conjunto de pilotos de uma página tem sempre
tamanho um. Pedir o lote -- que é rota de administrador -- para um id só seria
usar a permissão maior para a pergunta menor.

### Falhar suave

Um provedor fora do ar deixa a linha **sem aquele campo**, e a tela diz o que
faltou:

```
3 in the current page · without invoices
```

Ela não apaga a página. Essa é a metade do ADR 0014 que costuma sumir na
implementação, e tem teste: `a provider that is down leaves the row without that
field, not without the page`.

**404 é resposta, não falha.** Um administrador não tem registro de piloto, e
dizer "sem piloto" para ele está certo; dizer "o identity caiu" seria mentira, e
mentira que aparece toda vez que ele entra. O fixture de carga achou isso -- o
sujeito do token dele não é um piloto cadastrado, e a tela anunciava degradação
a cada requisição.

## O contrato de leitura

`contracts/reads.json` é a declaração do consumidor: caminho, nome do parâmetro
de ids, teto do lote e os campos que a tela usa. Cada provedor tem um teste que
lê **esse mesmo arquivo** e verifica o próprio handler contra ele -- em Go, em
Kotlin e em C#.

Isso é o que faz "remover um lote deixa um teste vermelho antes de deixar uma
tela em branco" ser verdade em vez de intenção. Uma cópia dos nomes de campo
dentro de cada teste provaria apenas que o teste concorda consigo mesmo.

O tamanho de página do console e o teto dos lotes são o **mesmo número**, e há
teste fixando a igualdade: subir um sem o outro deixaria a última linha da
página sem moto, em silêncio.

## O preço do salto extra

O ADR 0014 aceita uma consequência e manda medi-la: a chamada passa pelo portão
em vez de ir direto. O número está em
[`docs/measurements/bff-hop.json`](../../docs/measurements/bff-hop.json), e o
que o produz é `test/hop-cost.mjs`, que pede a mesma leitura das duas formas do
mesmo lugar -- assinando o envelope do ADR 0008 à mão no caminho direto, para
não medir "com autenticação" contra "sem".

## Rodar

```bash
npm ci
npm test          # composição, contratos e limites de entrada
npm run typecheck
npm run build
```

Os testes de pilha exigem o fixture `projecty-load` de pé; o runbook em
[`docs/runbooks/polyglot-services.md`](../../docs/runbooks/polyglot-services.md)
tem a sequência.

## Configuração

| Variável | |
|---|---|
| `GATEWAY_URL` | `http://api-gateway:8090` |
| `CONSOLE_ORIGIN` | de onde o navegador chama, e o que o `sameOrigin` exige |
| `TELEMETRY_TICKET_KEY` | assina o ticket de rastreamento e a permissão de trace, mínimo 32 bytes |
| `TELEMETRY_PUBLIC_URL` | o WebSocket que o navegador abre |
