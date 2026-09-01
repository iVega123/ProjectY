using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using RentalOperations.CrossCutting.Model;
using RentalOperations.CrossCutting.Services;
using RentalOperations.Data;
using RentalOperations.DTOs;
using RentalOperations.Model;
using RentalOperations.Repository;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Testcontainers.MongoDb;

namespace RentalOperationsTests.Integration.MongoDb;

public sealed class ActiveRentalApiTests : IAsyncLifetime
{
    private const string DatabaseName = "rental_concurrency_tests";
    private readonly MongoDbContainer _database = new MongoDbBuilder("mongo:8.0").Build();
    private MongoRentalApiFactory? _factory;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        var rentals = new MongoClient(_database.GetConnectionString())
            .GetDatabase(DatabaseName)
            .GetCollection<BsonDocument>("Rentals");
        await rentals.InsertManyAsync(
        [
            CreateLegacyOpenRental("legacy-rider-1", DateTime.UtcNow.Date.AddDays(-14)),
            CreateLegacyOpenRental("legacy-rider-2", DateTime.UtcNow.Date.AddDays(-7))
        ]);
        _factory = new MongoRentalApiFactory(_database.GetConnectionString(), DatabaseName);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#database-serialized-rental-claim")]
    public async Task ConcurrentCreateRequestsForSameMotorcycle_OneSucceedsAndOneReturnsConflict()
    {
        using var client = CreateAuthenticatedClient();
        var request = new RentalCreateDto
        {
            MotocycleLicencePlate = "RACE-0001",
            StartDate = DateTime.UtcNow.Date.AddDays(1),
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(8)
        };

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/Rental/create", request),
            client.PostAsJsonAsync("/api/Rental/create", request));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        var rentals = new MongoClient(_database.GetConnectionString())
            .GetDatabase(DatabaseName)
            .GetCollection<Rental>("Rentals");
        var activeRentals = await rentals.CountDocumentsAsync(
            rental => rental.MotorcycleLicencePlate == request.MotocycleLicencePlate &&
                      rental.Status == RentalStatus.Active);
        var persisted = await rentals.Find(
                rental => rental.MotorcycleLicencePlate == request.MotocycleLicencePlate)
            .SingleAsync();

        Assert.Equal(1, activeRentals);
        Assert.Null(persisted.EndDate);

        var legacyRentals = await rentals.Find(rental =>
                rental.MotorcycleLicencePlate == "LEGACY-0001")
            .SortBy(rental => rental.StartDate)
            .ToListAsync();
        Assert.Equal(2, legacyRentals.Count);
        Assert.Equal(RentalStatus.Active, legacyRentals[0].Status);
        Assert.Equal(RentalStatus.Quarantined, legacyRentals[1].Status);
        Assert.Equal(
            MongoRentalIndexInitializer.LegacyDuplicateQuarantineMessage,
            legacyRentals[1].StatusMessage);
        Assert.All(legacyRentals, rental => Assert.Null(rental.EndDate));

        var indexes = await (await rentals.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, index =>
            index["name"] == MongoRentalIndexInitializer.ActiveRentalIndexName &&
            index["unique"].AsBoolean);

        var actualEndDate = Uri.EscapeDataString(request.PredictedEndDate.ToString("O"));
        var completion = await client.PostAsync(
            $"/api/Rental/calculate-final-cost?rentalId={persisted._id!.Value}&actualEndDate={actualEndDate}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);

        var nextRental = new RentalCreateDto
        {
            MotocycleLicencePlate = request.MotocycleLicencePlate,
            StartDate = request.PredictedEndDate,
            PredictedEndDate = request.PredictedEndDate.AddDays(7)
        };
        var nextResponse = await client.PostAsJsonAsync("/api/Rental/create", nextRental);

        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        Assert.Equal(2, await rentals.CountDocumentsAsync(
            rental => rental.MotorcycleLicencePlate == request.MotocycleLicencePlate));
        Assert.Equal(1, await rentals.CountDocumentsAsync(
            rental => rental.MotorcycleLicencePlate == request.MotocycleLicencePlate &&
                      rental.Status == RentalStatus.Active));
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var factory = _factory ?? throw new InvalidOperationException("The test database has not started.");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken("Rider", "rider-1"));
        return client;
    }

    private static BsonDocument CreateLegacyOpenRental(string userId, DateTime startDate) => new()
    {
        ["MotorcycleLicencePlate"] = "LEGACY-0001",
        ["userId"] = userId,
        ["startDate"] = startDate,
        ["endDate"] = DateTime.MinValue,
        ["predictedEndDate"] = DateTime.UtcNow.Date.AddDays(7),
        ["initCost"] = 210m
    };

    private static string CreateToken(string role, string userId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(MongoRentalApiFactory.JwtKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "projecty.auth-gate",
            audience: "projecty.rental-operations",
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role)
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

internal sealed class MongoRentalApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-key-with-at-least-32-bytes";
    private readonly string _connectionString;
    private readonly string _databaseName;

    public MongoRentalApiFactory(string connectionString, string databaseName)
    {
        _connectionString = connectionString;
        _databaseName = databaseName;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = JwtKey,
                ["Jwt:Issuer"] = "projecty.auth-gate",
                ["Jwt:Audience"] = "projecty.rental-operations",
                ["MongoDbSettings:ConnectionString"] = _connectionString,
                ["MongoDbSettings:DatabaseName"] = _databaseName
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<MongoDbContext>();
            services.RemoveAll<IRentalRepository>();
            services.RemoveAll<IRiderManagerService>();
            services.RemoveAll<IMotorcycleService>();

            services.AddSingleton(new MongoDbContext(_connectionString, _databaseName));
            services.AddScoped<RentalRepository>();
            services.AddSingleton<ConcurrentCreateGate>();
            services.AddScoped<IRentalRepository>(provider => new SynchronizingRentalRepository(
                provider.GetRequiredService<RentalRepository>(),
                provider.GetRequiredService<ConcurrentCreateGate>()));
            services.AddSingleton<IRiderManagerService, StubRiderManagerService>();
            services.AddSingleton<IMotorcycleService, StubMotorcycleService>();
            services.AddHostedService<MongoRentalIndexInitializer>();

            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                        ValidateIssuer = true,
                        ValidIssuer = "projecty.auth-gate",
                        ValidateAudience = true,
                        ValidAudience = "projecty.rental-operations"
                    };
                });
        });
    }
}

internal sealed class ConcurrentCreateGate
{
    private readonly TaskCompletionSource _bothRequestsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remainingRequests = 2;

    public async Task WaitAsync()
    {
        if (Interlocked.Decrement(ref _remainingRequests) == 0)
        {
            _bothRequestsReady.TrySetResult();
        }

        await _bothRequestsReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

internal sealed class SynchronizingRentalRepository : IRentalRepository
{
    private readonly RentalRepository _repository;
    private readonly ConcurrentCreateGate _gate;

    public SynchronizingRentalRepository(RentalRepository repository, ConcurrentCreateGate gate)
    {
        _repository = repository;
        _gate = gate;
    }

    public async Task<Rental> CreateRentalAsync(Rental rental)
    {
        await _gate.WaitAsync();
        return await _repository.CreateRentalAsync(rental);
    }

    public Task<Rental> GetRentalByIdAsync(string id) => _repository.GetRentalByIdAsync(id);

    public Task<List<Rental>> GetRentalsByUserId(string userId) =>
        _repository.GetRentalsByUserId(userId);

    public Task<List<Rental>> GetRentalsByMotorcycleIdAsync(string licencePlate) =>
        _repository.GetRentalsByMotorcycleIdAsync(licencePlate);

    public Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate) =>
        _repository.IsMotorcycleCurrentlyRentedAsync(licencePlate);

    public Task UpdateRentalAsync(Rental rental) => _repository.UpdateRentalAsync(rental);

    public Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate) =>
        _repository.UpdateLicensePlateForAllRentalsAsync(oldLicensePlate, newLicensePlate);

    public Task DeleteRentalAsync(string id) => _repository.DeleteRentalAsync(id);
}

internal sealed class StubRiderManagerService : IRiderManagerService
{
    public Task<Rider> GetRiderByIdAsync(string riderId) => Task.FromResult(new Rider
    {
        Id = riderId,
        UserId = riderId,
        CNHType = "A"
    });
}

internal sealed class StubMotorcycleService : IMotorcycleService
{
    public Task<Motorcycle> GetMotorcycleByIdAsync(string motorcycleId) => Task.FromResult(new Motorcycle
    {
        licensePlate = motorcycleId,
        model = "Concurrency test",
        year = 2026
    });
}
