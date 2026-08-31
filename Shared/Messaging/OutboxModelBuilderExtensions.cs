using Microsoft.EntityFrameworkCore;

namespace ProjectY.Shared.Messaging;

public static class OutboxModelBuilderExtensions
{
    public static void ConfigureOutbox(this ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<OutboxMessage>();
        message.ToTable("OutboxMessages");
        message.HasKey(item => item.Id);
        message.Property(item => item.AggregateType).HasMaxLength(100);
        message.Property(item => item.AggregateId).HasMaxLength(200);
        message.Property(item => item.EventType).HasMaxLength(200);
        message.Property(item => item.Destination).HasMaxLength(200);
        message.Property(item => item.Payload).HasColumnType("text");
        message.Property(item => item.LastError).HasMaxLength(2000);
        message.HasIndex(item => new
        {
            item.AggregateType,
            item.AggregateId,
            item.AggregateSequence
        });
        message.HasIndex(item => new
        {
            item.PublishedAtUtc,
            item.ClaimedUntilUtc,
            item.NextAttemptAtUtc,
            item.OccurredAtUtc
        }).HasDatabaseName("IX_OutboxMessages_PendingClaim");
    }
}
