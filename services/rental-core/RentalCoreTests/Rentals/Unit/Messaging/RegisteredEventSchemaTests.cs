using System.Net;
using Google.Protobuf;
using ProjectY.Events;
using ProjectY.Shared.Messaging;
using RentalOperations.Model;
using Xunit;

namespace RentalOperationsTests.Unit.Messaging;

public sealed class RegisteredEventSchemaTests
{
    [Fact]
    public async Task CachedSchemaSurvivesRegistryFailure()
    {
        var handler = new RegistryHandler();
        using var client = new HttpClient(handler);
        var schemas = new RegisteredEventSchema(client, "http://registry/apis/ccompat/v7",
            Path.Combine(AppContext.BaseDirectory, "event-contracts"));
        Assert.Equal(42, await schemas.ResolveAsync("rental.closed", default));
        handler.Available = false;
        Assert.Equal(42, await schemas.ResolveAsync("rental.closed", default));
        Assert.Equal(1, handler.Requests);
        await Assert.ThrowsAsync<HttpRequestException>(() => schemas.ResolveAsync("rental.started", default));
    }

    [Fact]
    public void PlateCannotReplaceImmutablePartitionKey()
    {
        RegisteredEventSchema.ValidateKey("motorcycle-id", "motorcycle-id");
        Assert.Throws<InvalidDataException>(() => RegisteredEventSchema.ValidateKey("ABC1D23", "motorcycle-id"));
    }

    [Fact]
    public void HistoricPayloadDistinguishesMissingMoneyFromZero()
    {
        // Retained v0 message: event_id=e, rental_id=r, motorcycle_id=m.
        var historic = RentalEvent.Parser.ParseFrom(Convert.FromHexString("0A016512017222016D"));
        Assert.Equal("m", historic.MotorcycleId);
        Assert.False(historic.HasAgreedTotalMinor);
        var current = new RentalEvent { EventId = "e", AgreedTotalMinor = 0, Currency = "BRL" };
        Assert.True(RentalEvent.Parser.ParseFrom(current.ToByteArray()).HasAgreedTotalMinor);
        var golden = new RentalEvent
        {
            EventId = "e",
            RentalId = "r",
            MotorcycleId = "m",
            AgreedTotalMinor = 0,
            Currency = "BRL",
            RiderName = "Ada"
        };
        // The unchanged Elixir v0 decoder consumes this same golden in tracking_test.exs.
        Assert.Equal("0A016512017222016D50005A0342524C6203416461", Convert.ToHexString(golden.ToByteArray()));
    }

    [Fact]
    public void ClosedEventCarriesAgreedMoneyAndDatedRiderName()
    {
        var rental = new Rental
        {
            MotorcycleLicencePlate = "ABC1D23",
            MotorcycleId = new Guid("22222222-2222-2222-2222-222222222222"),
            UserId = "rider",
            RiderName = "Original name",
            InitCost = 210.25m,
            StartDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            PredictedEndDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc)
        };
        var envelope = RentalEventEnvelope.Create(rental, "rental.closed");
        rental.RiderName = "Renamed later";
        var message = RentalEvent.Parser.ParseFrom(envelope.Payload);
        Assert.Equal(21025, message.AgreedTotalMinor);
        Assert.Equal("BRL", message.Currency);
        Assert.Equal("Original name", message.RiderName);
    }

    /// <summary>
    /// O contrato com o billing, agora que a liquidação mora lá.
    ///
    /// O billing recusa um rental.closed sem ended_at_ms ou sem plan_days -- não
    /// há como liquidar sem eles -- e calcula errado sem as outras duas datas.
    /// Nada disso é opcional do lado de cá, e este teste é o que faz uma coluna
    /// removida do evento aparecer aqui em vez de virar uma fatura errada.
    /// </summary>
    [Fact]
    public void ClosedEventCarriesEverythingTheSettlementNeeds()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var rental = new Rental
        {
            MotorcycleId = new Guid("22222222-2222-2222-2222-222222222222"),
            UserId = "rider",
            InitCost = 210m,
            StartDate = start,
            PredictedEndDate = start.AddDays(7),
            EndDate = start.AddDays(4),
            Status = RentalStatus.Completed
        };

        var message = RentalEvent.Parser.ParseFrom(
            RentalEventEnvelope.Create(rental, "rental.closed").Payload);

        Assert.True(message.HasEndedAtMs, "sem a data de fim o billing recusa o evento");
        Assert.Equal(7, message.PlanDays);
        Assert.Equal(21000, message.AgreedTotalMinor);
        Assert.Equal(
            new DateTimeOffset(start).ToUnixTimeMilliseconds(),
            message.StartedAtMs);
        Assert.Equal(
            new DateTimeOffset(start.AddDays(7)).ToUnixTimeMilliseconds(),
            message.PredictedEndAtMs);
        Assert.Equal(
            new DateTimeOffset(start.AddDays(4)).ToUnixTimeMilliseconds(),
            message.EndedAtMs);
    }

    private sealed class RegistryHandler : HttpMessageHandler
    {
        public bool Available { get; set; } = true;
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (!Available) throw new HttpRequestException("Registry unavailable");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"id\":42}") });
        }
    }
}
