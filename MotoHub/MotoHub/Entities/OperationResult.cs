namespace MotoHub.Entities
{
    public class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        public static OperationResult Ok(string message = null) => new OperationResult { Success = true, Message = message };
        public static OperationResult Fail(string message) => new OperationResult { Success = false, Message = message };
    }

}
