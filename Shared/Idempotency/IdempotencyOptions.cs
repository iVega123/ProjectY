namespace ProjectY.Shared.Idempotency;

public sealed class IdempotencyOptions
{
    public const string HeaderName = "Idempotency-Key";

    public string ServiceName { get; set; } = "projecty";
    public string RedisConnectionString { get; set; } = "redis:6379";
    public TimeSpan ResponseTtl { get; set; } = TimeSpan.FromHours(24);
    public int MaximumKeyLength { get; set; } = 200;
}
