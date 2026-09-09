# rental-core

Motos e aluguéis, num processo e num banco. Eram dois serviços -- MotoHub e
Rental Operations -- e viraram um no #135, porque criar um aluguel precisa da
moto na mesma transação: a exclusão mútua passou a ser um índice único parcial
em vez de um protocolo de reserva entre dois bancos.

O nome do arquivo diz `MotoHub` em muito lugar ainda. Os namespaces também. É
dívida declarada, e renomear tudo é uma mudança de milhares de linhas que
esconderia qualquer outra coisa no mesmo PR.

## Como um erro sai daqui

Uma forma só, `application/problem+json` do RFC 9457, e a mesma que o portão
usa:

```json
{
  "type": "urn:projecty:problem:active-rental",
  "title": "Active rental conflict",
  "status": 409,
  "detail": "Motorcycle 0f1e... already has an active rental.",
  "instance": "/api/Rental/create",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736"
}
```

Quem monta isso é o `Errors/ProblemDetailsExceptionHandler`, num lugar só. Até o
#96, cada endpoint terminava assim:

```csharp
catch (Exception ex) { return BadRequest(ex.Message); }
```

Duas coisas erradas na mesma linha. A mensagem de uma exceção do Npgsql carrega
**host, banco e usuário**, e aquela linha não sabia distinguir isso de uma frase
escrita para o cliente. E toda falha interna virava **400**, dizendo a quem
chamou para corrigir uma requisição que estava certa.

O que decide agora é o **tipo** da exceção:

| | |
|---|---|
| `ClientProblemException` | a frase foi escrita para o cliente, e o tipo é a prova |
| dependência indisponível | 503 com `Retry-After`, e uma recusa contada no medidor |
| qualquer outra coisa | **500** com frase fixa; o motivo vai para o log, endereçado pelo `traceId` |

O `traceId` é o do trace, e não um número novo: um identificador que não aparece
no Tempo não acha nada. É ele que faz "me diga o identificador" ser uma pergunta
útil no suporte.

**Nada da entrada do cliente volta no corpo.** Um id de aluguel ilegível
responde "Every rental id must be a UUID.", e não o id que veio -- devolvê-lo
faria da resposta um refletor. Pelo mesmo motivo o `instance` é o caminho e não
a query.

O CI recusa o PR que reintroduzir o vazamento: o passo *Refuse exception text in
responses* procura `ex.Message` dentro de uma resposta e confere que o tratador
continua registrado **e** no pipeline.

### A ordem no pipeline não é acidental

`app.UseExceptionHandler()` vem **depois** de `app.UseProjectYIdempotency()`. O
middleware de idempotência decide sobre a chave olhando o **status da resposta**
depois que a requisição volta: um 503 marcado como "nada aconteceu ainda" libera
a chave para o cliente repetir. Com o tratador por fora, a exceção passaria por
ele ainda como exceção -- sem status para olhar -- e a chave ficaria trancada
até expirar.

## Os códigos que este serviço usa

| | |
|---|---|
| 400 | a requisição não serve como está |
| 403 | o piloto não pode alugar -- reenviar corrigido não existe |
| 404 | o recurso não existe, ou não é de quem pediu |
| 409 | a regra recusa: aluguel ativo, moto aposentada, placa tomada, liquidação concorrente -- e a projeção do piloto que ainda não chegou |
| 500 | quebrou aqui dentro |
| 503 | dependência fora |

O 403 e o 404 eram 400 antes do #96. Um 400 para "a sua CNH não permite" manda o
cliente consertar o corpo, que não é o que precisa mudar.

**Nenhuma recusa de negócio é 5xx**, e essa regra tem dono: um 5xx diz ao portão
que este serviço está doente, e ele age -- repete a requisição (POST com
`Idempotency-Key` é repetível) e conta a resposta contra o disjuntor. A projeção
do piloto que ainda não chegou chegou a ser 503 durante o #96; o benchmark de
carga acusou, porque o portão passou a repetir o que sempre falharia igual.

## Testes

```bash
dotnet test services/rental-core/RentalCoreTests/RentalCoreTests.csproj
```

O `ProblemDetailsPipelineTests` sobe o host de verdade: ele prova que o tratador
está **ligado**, não só que ele formata certo. Um handler registrado no lugar
errado passa em todo teste de unidade e não atende requisição nenhuma.
