using System.Text.Json;

namespace RentalCoreTests;

/// <summary>
/// A declaração do consumidor, lida do arquivo em vez de repetida aqui.
///
/// O #138 pede que remover um endpoint de lote deixe um teste vermelho antes de
/// deixar uma tela em branco. Isso só é verdade se o teste souber o que a tela
/// pede, e a única forma de ele saber é ler a mesma declaração que o console lê
/// -- <c>contracts/reads.json</c>. Uma cópia dos nomes de campo aqui provaria
/// que este arquivo concorda consigo mesmo.
/// </summary>
public static class ReadContract
{
    public static Read For(string provider, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate()));
        foreach (var read in document.RootElement.GetProperty("reads").EnumerateArray())
        {
            if (read.GetProperty("provider").GetString() == provider
                && read.GetProperty("path").GetString() == path)
            {
                return new Read(
                    read.GetProperty("method").GetString()!,
                    read.GetProperty("path").GetString()!,
                    read.TryGetProperty("idsParameter", out var parameter) ? parameter.GetString() : null,
                    read.TryGetProperty("maxBatch", out var cap) ? cap.GetInt32() : null,
                    [.. read.GetProperty("fields").EnumerateArray().Select(field => field.GetString()!)]);
            }
        }

        throw new InvalidOperationException(
            $"contracts/reads.json declares no {provider} read at {path}. "
            + "If the console stopped asking for it, delete the endpoint; if it still asks, restore the declaration.");
    }

    /// <summary>
    /// Sobe do binário até achar o arquivo. O diretório de saída do teste muda
    /// com o framework e a configuração, e um caminho relativo fixo quebra
    /// sozinho no dia em que um deles muda.
    /// </summary>
    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contracts", "reads.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("contracts/reads.json was not found above " + AppContext.BaseDirectory);
    }

    public sealed record Read(
        string Method,
        string Path,
        string? IdsParameter,
        int? MaxBatch,
        IReadOnlyList<string> Fields);
}
