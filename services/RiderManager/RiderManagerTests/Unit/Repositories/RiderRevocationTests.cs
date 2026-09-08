using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Models;
using RiderManager.Repositories;

namespace RiderManagerTests.Unit.Repositories;

public sealed class RiderRevocationTests
{
    private static Rider NewRider() => new()
    {
        Id = "internal-rider-id",
        UserId = "auth-user-id",
        Email = "rider@example.com",
        Name = "Ada Lovelace",
        CNPJ = "92805586000180",
        CNHNumber = "12345678901",
        CNHType = "A"
    };

    private static ApplicationDbContext NewContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Sem este fato, um piloto apagado continua alugando: rental-core decide
    /// pela projeção local e nunca volta a perguntar a este serviço.
    /// </summary>
    [Fact]
    public async Task DeletingARider_QueuesTheRevocationOnBothTopics()
    {
        await using var context = NewContext();
        context.Riders.Add(NewRider());
        await context.SaveChangesAsync();
        context.EventOutbox.RemoveRange(context.EventOutbox);
        await context.SaveChangesAsync();

        await new RiderRepository(context).DeleteAsync("internal-rider-id");

        var queued = await context.EventOutbox.ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.Contains(queued, envelope => envelope.Topic == "rider.verified");
        Assert.Contains(queued, envelope => envelope.Topic == "rider.verified.v2");

        var legacy = ProjectY.Events.RiderEvent.Parser.ParseFrom(
            queued.Single(envelope => envelope.Topic == "rider.verified").Payload);
        var current = ProjectY.Events.RiderEventV2.Parser.ParseFrom(
            queued.Single(envelope => envelope.Topic == "rider.verified.v2").Payload);

        Assert.False(legacy.Verified);
        Assert.False(current.Verified);
        Assert.Equal("auth-user-id", current.RiderId);
        // O carimbo é o que faz a projeção aceitar a revogação: o upsert só
        // avança quando o fato novo é mais recente do que o que está lá.
        Assert.True(current.OccurredAtMs > 0);
    }

    [Fact]
    public async Task DeletingAnAbsentRider_QueuesNothing()
    {
        await using var context = NewContext();

        await new RiderRepository(context).DeleteAsync("no-such-rider");

        Assert.Empty(await context.EventOutbox.ToListAsync());
    }
}
