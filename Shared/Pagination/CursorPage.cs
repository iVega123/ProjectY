using System.Text;

namespace ProjectY.Shared.Pagination;

public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class CursorPagination
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    public static int NormalizePageSize(int? pageSize) =>
        Math.Clamp(pageSize ?? DefaultPageSize, 1, MaximumPageSize);

    public static string Encode(string value)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return string.IsNullOrWhiteSpace(value)
                ? throw new FormatException("The pagination cursor is empty.")
                : value;
        }
        catch (FormatException exception)
        {
            throw new FormatException("The pagination cursor is invalid.", exception);
        }
    }

    public static CursorPage<T> CreatePage<T>(
        IReadOnlyList<T> fetched,
        int pageSize,
        Func<T, string> cursorValue)
    {
        var items = fetched.Take(pageSize).ToList();
        var nextCursor = fetched.Count > pageSize && items.Count > 0
            ? Encode(cursorValue(items[^1]))
            : null;
        return new CursorPage<T>(items, nextCursor);
    }
}
