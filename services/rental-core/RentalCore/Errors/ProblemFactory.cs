using System.Diagnostics;

using Microsoft.AspNetCore.Mvc;

namespace RentalCore.Errors;

/// <summary>
/// Uma forma só para todo erro que sai daqui.
///
/// Duas coisas produzem erro: o controlador, quando recusa uma entrada que ele
/// mesmo validou, e o <see cref="ProblemDetailsExceptionHandler"/>, quando algo
/// sobe. Se cada uma montasse a resposta do seu jeito, o cliente veria dois
/// formatos vindos do mesmo serviço -- que é a versão pequena do problema que o
/// #96 descreve entre o portão e o domínio.
/// </summary>
public static class ProblemFactory
{
    public static ProblemDetails Create(
        HttpContext context,
        int status,
        string problemType,
        string title,
        string detail) => new()
        {
            Status = status,
            Type = problemType,
            Title = title,
            Detail = detail,
            // O caminho, e NÃO a query.
            //
            // Tirar o identificador recusado do `detail` e devolvê-lo aqui não
            // adiantaria nada: o corpo continuaria refletindo a entrada de quem
            // chamou. O caminho basta para dizer onde aconteceu, e a ocorrência
            // exata é o traceId -- que é melhor que a URL, porque ele acha o
            // resto da história.
            Instance = context.Request.Path,
            // O identificador de correlação É o do trace, e não um número novo:
            // um identificador que não aparece no Tempo não acha nada.
            Extensions = { ["traceId"] = TraceId(context) }
        };

    public static string TraceId(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
}
