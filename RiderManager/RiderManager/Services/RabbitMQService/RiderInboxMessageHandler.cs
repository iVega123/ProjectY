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
                if (part.Content is null)
                {
                    throw new InvalidOperationException("The image part content is missing.");
                }

                var alreadyStored = await _context.InboxImageParts.AnyAsync(
                    stored => stored.UserId == part.UserId
                        && stored.FileName == part.FileName
                        && stored.SequenceNumber == part.SequenceNumber,
                    token);

                if (!alreadyStored)
                {
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

                if (!part.EndOfFile)
                {
                    return;
                }

                var parts = await _context.InboxImageParts
                    .Where(stored => stored.UserId == part.UserId && stored.FileName == part.FileName)
                    .OrderBy(stored => stored.SequenceNumber)
                    .ToListAsync(token);

                if (parts.Count == 0
                    || parts[^1].SequenceNumber != part.SequenceNumber
                    || parts.Where((stored, index) => stored.SequenceNumber != index).Any())
                {
                    throw new InvalidOperationException("The image stream is incomplete.");
                }

                await using var stream = new MemoryStream();
                foreach (var stored in parts)
                {
                    await stream.WriteAsync(stored.Content, token);
                }

                stream.Position = 0;
                var formFile = new FormFile(stream, 0, stream.Length, "cnhImage", part.FileName)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };
                await _riderManager.UpdateRiderImageAsync(part.UserId, formFile, part.FileName);

                _context.InboxImageParts.RemoveRange(parts);
            },
            cancellationToken);
}
