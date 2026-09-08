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
