namespace RiderManager.Models;

public sealed class InboxImagePart
{
    public required string UserId { get; set; }
    public required string FileName { get; set; }
    public int SequenceNumber { get; set; }
    public required byte[] Content { get; set; }
    public bool EndOfFile { get; set; }
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}
