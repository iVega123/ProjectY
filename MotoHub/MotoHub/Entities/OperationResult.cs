namespace MotoHub.Entities
{
    public class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int? StatusCode { get; set; }

        public static OperationResult Ok(string? message = null) => new() { Success = true, Message = message ?? string.Empty };
        public static OperationResult Fail(string message, int? statusCode = null) =>
            new() { Success = false, Message = message, StatusCode = statusCode };
    }

}
