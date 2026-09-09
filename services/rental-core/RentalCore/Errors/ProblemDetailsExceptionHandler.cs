using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using RentalOperations.Services;

namespace RentalCore.Errors;

/// <summary>
/// Uma saída para todo erro, em <c>application/problem+json</c>.
///
/// O achado A9: cinco endpoints terminavam em
/// <c>catch (Exception ex) { return BadRequest(ex.Message); }</c>, e a mensagem
/// de uma exceção do Npgsql carrega host, banco e usuário. Com um único lugar
/// decidindo a resposta, o vazamento deixa de depender de cada controlador
/// lembrar -- e o formato deixa de ser dois: o portão já responde RFC 9457, e
/// agora o serviço responde a mesma forma.
///
/// Três casos, nesta ordem:
///
/// 1. <see cref="ClientProblemException"/> -- a frase foi escrita para o
///    cliente, e o tipo é a prova disso.
/// 2. Indisponibilidade de dependência -- 503 com Retry-After, e uma recusa
///    contada no medidor de degradação.
/// 3. Qualquer outra coisa -- 500 com uma frase fixa. O motivo vai para o log,
///    junto do mesmo <c>traceId</c> que a resposta carrega.
/// </summary>
public sealed class ProblemDetailsExceptionHandler(
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = Map(context, exception);

        // O detalhe fica aqui, e só aqui. `traceId` é o que liga esta linha à
        // resposta que o cliente tem na mão -- é o que torna "me diga o
        // identificador" uma pergunta útil no suporte.
        logger.Log(
            problem.Status >= 500 ? LogLevel.Error : LogLevel.Warning,
            exception,
            "Request failed with {Status} {ProblemType} (traceId {TraceId})",
            problem.Status,
            problem.Type,
            problem.Extensions["traceId"]);

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        // O tipo de conteúdo vai no WriteAsJsonAsync, e não numa atribuição
        // antes: ele sobrescreve o cabeçalho com application/json ao escrever,
        // e a resposta sairia como JSON comum -- que é a diferença entre
        // "isto é um erro descrito" e "isto é um objeto qualquer".
        await context.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }

    private ProblemDetails Map(HttpContext context, Exception exception)
    {
        if (exception is ClientProblemException client)
        {
            if (client.Status == StatusCodes.Status503ServiceUnavailable)
            {
                context.Response.Headers.RetryAfter = "1";
            }
            return ProblemFactory.Create(
                context, client.Status, client.ProblemType, client.Title, client.Detail);
        }

        if (DependencyFailure.IsUnavailable(exception))
        {
            DependencyFailure.Record(exception);
            if (exception is PreWriteDependencyException)
            {
                // Nada aconteceu ainda, então repetir é seguro: a chave de
                // idempotência é liberada em vez de trancar a operação até
                // expirar.
                ProjectY.Shared.Idempotency.RedisIdempotencyMiddleware
                    .AllowRetryBeforeSideEffects(context);
            }
            context.Response.Headers.RetryAfter = "1";
            return ProblemFactory.Create(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ProblemTypes.DependencyUnavailable,
                "Dependency unavailable",
                "A required dependency is temporarily unavailable. Retry later.");
        }

        return ProblemFactory.Create(
            context,
            StatusCodes.Status500InternalServerError,
            ProblemTypes.Unexpected,
            "Unexpected error",
            // Fixa, e sem interpolação de coisa alguma. Toda variação útil para
            // quem depura está no log, endereçada pelo traceId.
            "The request could not be completed. Quote the traceId when reporting it.");
    }
}
