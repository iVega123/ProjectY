namespace RentalCore.Errors;

/// <summary>
/// Uma exceção cuja mensagem foi escrita PARA o cliente.
///
/// É a distinção que o achado A9 pede. Hoje todo controlador termina em
/// <c>catch (Exception ex) { return BadRequest(ex.Message); }</c>, e essa linha
/// não sabe a diferença entre "A motorcycle id is required." e a string de
/// conexão que o Npgsql põe na mensagem dele. Quem decide o que o cliente vê
/// passa a ser o TIPO da exceção, e não a sorte de qual delas subiu.
///
/// Herdar disto é uma afirmação: esta frase é para ser lida por quem chamou.
/// Tudo o mais vira 500 com uma frase fixa e um identificador de correlação --
/// o detalhe vai para o log, onde tem dono.
/// </summary>
public abstract class ClientProblemException : Exception
{
    protected ClientProblemException(
        int status,
        string problemType,
        string title,
        string detail,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Status = status;
        ProblemType = problemType;
        Title = title;
    }

    public int Status { get; }

    /// <summary>
    /// O <c>type</c> do RFC 9457, no mesmo espaço de nomes que o portão usa.
    /// Uma URN e não uma URL: não há documento para buscar, e prometer um que
    /// não existe é a mesma classe de defeito do achado A4.
    /// </summary>
    public string ProblemType { get; }

    public string Title { get; }

    public string Detail => Message;
}

/// <summary>A requisição não serve como está: 400.</summary>
public sealed class InvalidRequestException(string detail)
    : ClientProblemException(400, ProblemTypes.InvalidRequest, "Invalid request", detail);

/// <summary>O recurso não existe, ou não é de quem pediu: 404.</summary>
public sealed class ResourceNotFoundException(string detail)
    : ClientProblemException(404, ProblemTypes.NotFound, "Not found", detail);

/// <summary>
/// A regra de negócio recusa: 409.
///
/// 409 e não 400: a requisição está bem formada, o estado é que não permite.
/// A diferença diz ao cliente se corrigir o corpo adianta.
/// </summary>
public class BusinessRuleException(
    string problemType,
    string title,
    string detail,
    Exception? innerException = null)
    : ClientProblemException(409, problemType, title, detail, innerException);

/// <summary>
/// A resposta ainda não é possível, e vale insistir: 409.
///
/// **409 e não 503**, e a diferença importa para quem está na frente. Um 5xx diz
/// ao portão "este upstream está doente": ele repete a requisição -- POST com
/// Idempotency-Key é repetível -- e conta a resposta contra o disjuntor. Nada
/// está doente aqui; é o registro do próprio chamador que ainda não chegou, e
/// repetir agora falha igual. Dizer 503 é mentir sobre a saúde do serviço, e
/// fazer o portão gastar tentativas por causa da mentira.
///
/// O tipo próprio é o que separa "ainda não" de "não pode": o comentário do
/// RiderProjectionPendingException pede essa distinção, porque uma recusa
/// genérica seria indistinguível de um fato permanente.
/// </summary>
public class NotYetAvailableException(string problemType, string detail)
    : ClientProblemException(409, problemType, "Not available yet", detail);

/// <summary>
/// O piloto não pode alugar: 403.
///
/// 403 e não 400: o corpo está correto, e reenviá-lo corrigido não existe --
/// quem muda isto é a habilitação do piloto, não a requisição. Era 400 até o
/// #96, e um 400 aqui dizia ao cliente para tentar consertar o que ele mandou.
/// </summary>
public sealed class RiderNotEntitledException()
    : ClientProblemException(
        403,
        ProblemTypes.NotEntitled,
        "Rider not entitled",
        "The rider is not entitled to rent this motorcycle.");

public static class ProblemTypes
{
    private const string Prefix = "urn:projecty:problem:";

    public const string InvalidRequest = Prefix + "invalid-request";
    public const string NotFound = Prefix + "not-found";
    public const string NotEntitled = Prefix + "rider-not-entitled";
    public const string ActiveRental = Prefix + "active-rental";
    public const string MotorcycleRetired = Prefix + "motorcycle-retired";
    public const string SettlementConflict = Prefix + "settlement-conflict";
    public const string PlateTaken = Prefix + "licence-plate-taken";
    public const string RiderProjectionPending = Prefix + "rider-projection-pending";
    public const string DependencyUnavailable = Prefix + "dependency-unavailable";

    /// <summary>
    /// O 500. Uma URN só, porque do lado de fora todas as falhas internas são a
    /// mesma coisa: alguma coisa quebrou aqui dentro, e o identificador de
    /// correlação é o que liga esta resposta ao trace que tem o motivo.
    /// </summary>
    public const string Unexpected = Prefix + "unexpected";
}
