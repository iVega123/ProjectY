namespace RentalCore.Errors;

/// <summary>
/// O cursor de paginação vem do cliente, e um cursor quebrado é erro dele.
///
/// Ele atravessa três lugares que podem recusá-lo -- <c>CursorPagination.Decode</c>
/// em <c>Shared</c>, o <c>Position.Parse</c> do repositório de aluguéis, e o de
/// motos -- e os três levantam <see cref="FormatException"/>. Sem tradução, o
/// tratador global os lê como falha interna e responde 500, quando a resposta
/// certa é 400: quem manda um cursor inválido pode consertar o que mandou.
///
/// A tradução mora aqui, e não em cada controlador, porque a frase que explica
/// o porquê só precisa existir uma vez. E não mora em <c>Shared</c> porque
/// <c>Shared</c> não conhece o contrato de erro deste serviço -- só quem serve
/// HTTP sabe que aquela string veio de uma query.
/// </summary>
public static class Cursors
{
    public static async Task<T> Paged<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (FormatException)
        {
            throw new InvalidRequestException("The pagination cursor is invalid.");
        }
    }
}
