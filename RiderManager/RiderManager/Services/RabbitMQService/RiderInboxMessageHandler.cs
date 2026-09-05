using Microsoft.EntityFrameworkCore;
using ProjectY.Shared.Messaging;
using RiderManager.Data;
using RiderManager.DTOs;
using RiderManager.Entities;
using RiderManager.Managers;
using RiderManager.Models;

namespace RiderManager.Services.RabbitMQService;

public sealed class RiderInboxMessageHandler
{
    public const int MaximumUploadBytes = 8 * 1024 * 1024;
    public const int MaximumPartBytes = 64 * 1024;
    public const int MaximumParts = 2048;
    public const string RegistrationConsumer = "rider-manager/registration/v1";
    public const string ImageStreamConsumer = "rider-manager/cnh-image-part/v1";

    private readonly ApplicationDbContext _context;
    private readonly IRiderInboxProcessor _inbox;
    private readonly IRiderManager _riderManager;

    public RiderInboxMessageHandler(
        ApplicationDbContext context,
        IRiderInboxProcessor inbox,
        IRiderManager riderManager)
    {
        _context = context;
        _inbox = inbox;
        _riderManager = riderManager;
    }

    public Task<bool> HandleRegistrationAsync(
        AuthenticatedQueueMessage<RiderMQEntity> message,
        CancellationToken cancellationToken = default)
        => _inbox.ProcessAsync(
            message.MessageId,
            RegistrationConsumer,
            async token =>
            {
                if (await _context.Riders.AnyAsync(
                        rider => rider.UserId == message.Payload.UserId,
                        token))
                {
                    return;
                }

                var rider = message.Payload;
                await _riderManager.AddRiderAsync(new RiderDTO
                {
                    UserId = rider.UserId,
                    Email = rider.Email,
                    Name = rider.Name,
                    CNPJ = rider.CNPJ,
                    DateOfBirth = rider.DateOfBirth,
                    CNHNumber = rider.CNHNumber,
                    CNHType = rider.CNHType
                });
            },
            cancellationToken);

    public Task<bool> HandleImagePartAsync(
        AuthenticatedQueueMessage<ImagePart> message,
        CancellationToken cancellationToken = default)
        => _inbox.ProcessAsync(
            message.MessageId,
            ImageStreamConsumer,
            async token =>
            {
                var part = message.Payload;
                if (part.Content is null || part.Content.Length == 0 || part.Content.Length > MaximumPartBytes
                    || part.SequenceNumber < 0 || part.SequenceNumber >= MaximumParts
                    || string.IsNullOrWhiteSpace(part.UserId) || string.IsNullOrWhiteSpace(part.FileName))
                {
                    throw new InvalidDataException("The image part exceeds the upload bounds or has invalid identity.");
                }

                // A transaction-scoped database lock serializes replicas for this rider.
                // The inbox transaction also rolls back the size check and buffered part together.
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({part.UserId}, 0))", token);
                var uploadId = "upload:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{part.UserId.Length}:{part.UserId}{part.FileName}")));
                const string completedConsumer = ImageStreamConsumer + "/completed";
                if (await _context.InboxMessages.AnyAsync(
                    row => row.MessageId == uploadId && row.ConsumerName == completedConsumer, token))
                {
                    return;
                }

                var existing = await _context.InboxImageParts.SingleOrDefaultAsync(
                    row => row.UserId == part.UserId && row.FileName == part.FileName
                        && row.SequenceNumber == part.SequenceNumber, token);
                if (existing is not null)
                {
                    if (existing.EndOfFile != part.EndOfFile || !existing.Content.SequenceEqual(part.Content))
                    {
                        throw new InvalidDataException("An image sequence number cannot be replaced with different content.");
                    }
                }
                else
                {
                    var bufferedBytes = await _context.InboxImageParts
                        .Where(row => row.UserId == part.UserId && row.FileName == part.FileName)
                        .SumAsync(row => (long)row.Content.Length, token);
                    if (bufferedBytes + part.Content.Length > MaximumUploadBytes)
                    {
                        throw new InvalidDataException("The image upload exceeds the 8 MiB limit.");
                    }
                    _context.InboxImageParts.Add(new InboxImagePart
                    {
                        UserId = part.UserId,
                        FileName = part.FileName,
                        SequenceNumber = part.SequenceNumber,
                        Content = part.Content,
                        EndOfFile = part.EndOfFile
                    });
                    await _context.SaveChangesAsync(token);
                }

                var metadata = await _context.InboxImageParts
                    .Where(row => row.UserId == part.UserId && row.FileName == part.FileName)
                    .Select(row => new { row.SequenceNumber, row.EndOfFile })
                    .OrderBy(row => row.SequenceNumber)
                    .ToListAsync(token);
                var endings = metadata.Where(row => row.EndOfFile).ToArray();
                if (endings.Length > 1 || (endings.Length == 1 && metadata[^1].SequenceNumber > endings[0].SequenceNumber))
                {
                    throw new InvalidDataException("The image stream has conflicting end markers.");
                }
                // An EOF may arrive on another replica before preceding parts. Commit it
                // and let whichever part completes the sequence perform the upload.
                if (endings.Length == 0 || metadata.Where((row, index) => row.SequenceNumber != index).Any())
                {
                    return;
                }

                var parts = await _context.InboxImageParts
                    .Where(row => row.UserId == part.UserId && row.FileName == part.FileName)
                    .OrderBy(row => row.SequenceNumber).ToListAsync(token);
                await using var stream = new MemoryStream();
                foreach (var stored in parts) { await stream.WriteAsync(stored.Content, token); }
                stream.Position = 0;
                var formFile = new FormFile(stream, 0, stream.Length, "cnhImage", part.FileName)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };
                await _riderManager.UpdateRiderImageAsync(part.UserId, formFile, part.FileName);
                _context.InboxImageParts.RemoveRange(parts);
                _context.InboxMessages.Add(new InboxMessage
                {
                    MessageId = uploadId,
                    ConsumerName = completedConsumer,
                    ProcessedAtUtc = DateTime.UtcNow
                });
            },
            cancellationToken);
}
