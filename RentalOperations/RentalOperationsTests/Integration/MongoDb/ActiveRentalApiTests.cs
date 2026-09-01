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
using ProjectY.Shared.Pagination;
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
        var database = new MongoClient(_database.GetConnectionString()).GetDatabase(DatabaseName);
        var rentals = database.GetCollection<BsonDocument>("Rentals");
        var legacyWinnerId = ObjectId.GenerateNewId();
        await rentals.InsertManyAsync(
        [
            CreateLegacyOpenRental(
                legacyWinnerId,
                " legacy-0001 ",
                "legacy-rider-1",
                DateTime.UtcNow.Date.AddDays(-14)),
            CreateLegacyOpenRental(
                ObjectId.GenerateNewId(),
                "LEGACY-0001",
                "legacy-rider-2",
                DateTime.UtcNow.Date.AddDays(-7))
        ]);
        await database.GetCollection<MotorcycleClaim>("MotorcycleClaims").InsertOneAsync(
            new MotorcycleClaim
            {
                MotorcycleLicencePlate = " legacy-0001 ",
                Kind = MotorcycleClaimKind.ActiveRental,
                RentalId = legacyWinnerId.ToString(),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-14)
            });
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

        var claims = new MongoClient(_database.GetConnectionString())
            .GetDatabase(DatabaseName)
            .GetCollection<MotorcycleClaim>("MotorcycleClaims");
        var legacyClaim = await claims.Find(claim =>
                claim.MotorcycleLicencePlate == "LEGACY-0001")
            .SingleAsync();
        Assert.Equal(MotorcycleClaimKind.ActiveRental, legacyClaim.Kind);
        Assert.Equal(legacyRentals[0]._id!.Value.ToString(), legacyClaim.RentalId);
        Assert.Equal(0, await claims.CountDocumentsAsync(claim =>
            claim.MotorcycleLicencePlate == " legacy-0001 "));

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
        Assert.Contains("LEGACY-0001", _factory!.MotorcycleService.HistoricalReferences);
        Assert.DoesNotContain(" legacy-0001 ", _factory.MotorcycleService.HistoricalReferences);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RiderCannotCreatePermanentMotorcycleRetirementClaim()
    {
        using var client = CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/Rental/motorcycle-retirements/ADMIN-ONLY-0001",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var claims = new MongoClient(_database.GetConnectionString())
            .GetDatabase(DatabaseName)
            .GetCollection<MotorcycleClaim>("MotorcycleClaims");
        Assert.Equal(0, await claims.CountDocumentsAsync(claim =>
            claim.MotorcycleLicencePlate == "ADMIN-ONLY-0001"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RiderCannotReserveInternalMotorcycleRename()
    {
        using var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/Rental/motorcycle-renames/reservations",
            new MotorcycleRenameReservationDto
            {
                OldLicencePlate = "LEGACY-0001",
                NewLicencePlate = "RENAMED-0001"
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Guarantee", "ADR-0009#database-serialized-rental-claim")]
    public async Task RenameReservation_BlocksCompetingClaimsAndMovesTheActiveClaim()
    {
        using var client = CreateAuthenticatedClient();
        _ = await client.GetAsync("/api/Rental/user?pageSize=1");
        var context = new MongoDbContext(_database.GetConnectionString(), DatabaseName);
        var repository = new RentalRepository(context);

        Assert.True(await repository.TryReserveLicensePlateRenameAsync(
            "LEGACY-0001",
            "RENAMED-0001"));
        Assert.Equal(
            RentalOperations.Domain.MotorcycleClaimResult.ActiveRental,
            await repository.TryClaimRetirementAsync("RENAMED-0001"));

        await repository.UpdateLicensePlateForAllRentalsAsync(
            "LEGACY-0001",
            "RENAMED-0001");

        var claims = context.Database.GetCollection<MotorcycleClaim>("MotorcycleClaims");
        Assert.Equal(0, await claims.CountDocumentsAsync(claim =>
            claim.MotorcycleLicencePlate == "LEGACY-0001"));
        var movedClaim = await claims.Find(claim =>
                claim.MotorcycleLicencePlate == "RENAMED-0001")
            .SingleAsync();
        Assert.Equal(MotorcycleClaimKind.ActiveRental, movedClaim.Kind);
        Assert.Equal("LEGACY-0001", movedClaim.SourceLicencePlate);

        var rentals = context.Database.GetCollection<Rental>("Rentals");
        Assert.Equal(2, await rentals.CountDocumentsAsync(rental =>
            rental.MotorcycleLicencePlate == "RENAMED-0001"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UserListing_IsCursorPagedAndEnforcesTheServerMaximum()
    {
        using var client = CreateAuthenticatedClient();
        _ = await client.GetAsync("/api/Rental/user?pageSize=1");
        var rentals = new MongoClient(_database.GetConnectionString())
            .GetDatabase(DatabaseName)
            .GetCollection<Rental>("Rentals");
        await rentals.InsertManyAsync(Enumerable.Range(0, 105).Select(index => new Rental
        {
            MotorcycleLicencePlate = $"PAGE-{index:D4}",
            UserId = "rider-1",
            StartDate = DateTime.UtcNow.Date.AddDays(-14),
            EndDate = DateTime.UtcNow.Date.AddDays(-7),
            PredictedEndDate = DateTime.UtcNow.Date.AddDays(-7),
            InitCost = 210m,
            Status = RentalStatus.Completed
        }));

        var first = await client.GetFromJsonAsync<CursorPage<ResponseRentalDTO>>(
            "/api/Rental/user?pageSize=1000");
        Assert.NotNull(first);
        Assert.Equal(100, first.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await client.GetFromJsonAsync<CursorPage<ResponseRentalDTO>>(
            $"/api/Rental/user?pageSize=1000&cursor={Uri.EscapeDataString(first.NextCursor)}");
        Assert.NotNull(second);
        Assert.Equal(5, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(item => item.RentalId)
            .Intersect(second.Items.Select(item => item.RentalId)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListingAndAvailabilityQueries_HaveIndexBackedPlans()
    {
        using var client = CreateAuthenticatedClient();
        _ = await client.GetAsync("/api/Rental/user?pageSize=1");
        var database = new MongoClient(_database.GetConnectionString()).GetDatabase(DatabaseName);

        var userPlan = await ExplainFindAsync(database, new BsonDocument
        {
            ["userId"] = "rider-1",
            ["_id"] = new BsonDocument("$gt", ObjectId.Empty)
        }, new BsonDocument("_id", 1));
        Assert.Contains(MongoRentalIndexInitializer.UserRentalPageIndexName, userPlan);
        Assert.DoesNotContain("COLLSCAN", userPlan);

        var now = DateTime.UtcNow;
        var availabilityPlan = await ExplainFindAsync(database, new BsonDocument
        {
            ["MotorcycleLicencePlate"] = "LEGACY-0001",
            ["status"] = RentalStatus.Active.ToString(),
            ["startDate"] = new BsonDocument("$lte", now),
            ["predictedEndDate"] = new BsonDocument("$gte", now)
        });
        Assert.Contains("IXSCAN", availabilityPlan);
        Assert.DoesNotContain("COLLSCAN", availabilityPlan);

        var schedulePlan = await ExplainFindAsync(database, new BsonDocument
        {
            ["MotorcycleLicencePlate"] = "LEGACY-0001",
            ["status"] = new BsonDocument("$in", new BsonArray
            {
                RentalStatus.Active.ToString(),
                RentalStatus.Completed.ToString()
            }),
            ["startDate"] = new BsonDocument("$lt", now.AddDays(1)),
            ["$or"] = new BsonArray
            {
                new BsonDocument
                {
                    ["endDate"] = new BsonDocument
                    {
                        ["$ne"] = BsonNull.Value,
                        ["$gt"] = now.AddDays(-1)
                    }
                },
                new BsonDocument
                {
                    ["endDate"] = BsonNull.Value,
                    ["predictedEndDate"] = new BsonDocument("$gt", now.AddDays(-1))
                }
            }
        });
        Assert.Contains(MongoRentalIndexInitializer.MotorcycleScheduleIndexName, schedulePlan);
        Assert.DoesNotContain("COLLSCAN", schedulePlan);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverlapQuery_RejectsPeriodsInsideCompletedRentalHistory()
    {
        using var client = CreateAuthenticatedClient();
        _ = await client.GetAsync("/api/Rental/user?pageSize=1");
        var context = new MongoDbContext(_database.GetConnectionString(), DatabaseName);
        var repository = new RentalRepository(context);
        var existingStart = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var existingEnd = existingStart.AddDays(7);
        await repository.CreateRentalAsync(new Rental
        {
            MotorcycleLicencePlate = "HISTORY-0001",
            UserId = "rider-history",
            StartDate = existingStart,
            EndDate = existingEnd,
            PredictedEndDate = existingEnd,
            InitCost = 210m,
            Status = RentalStatus.Completed
        });

        Assert.True(await repository.HasOverlappingRentalAsync(
            "HISTORY-0001",
            existingStart.AddDays(1),
            existingEnd.AddDays(-1)));
        Assert.False(await repository.HasOverlappingRentalAsync(
            "HISTORY-0001",
            existingEnd,
            existingEnd.AddDays(7)));
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

    private static async Task<string> ExplainFindAsync(
        IMongoDatabase database,
        BsonDocument filter,
        BsonDocument? sort = null)
    {
        var find = new BsonDocument
        {
            ["find"] = "Rentals",
            ["filter"] = filter,
            ["limit"] = 101
        };
        if (sort is not null)
        {
            find["sort"] = sort;
        }

        var explanation = await database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["explain"] = find,
            ["verbosity"] = "queryPlanner"
        });
        return explanation["queryPlanner"]["winningPlan"].ToJson();
    }

    private static BsonDocument CreateLegacyOpenRental(
        ObjectId id,
        string licencePlate,
        string userId,
        DateTime startDate) => new()
    {
        ["_id"] = id,
        ["MotorcycleLicencePlate"] = licencePlate,
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
    public StubMotorcycleService MotorcycleService { get; } = new();

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
            services.AddSingleton<IMotorcycleService>(MotorcycleService);
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
        return await _repository.CreateRentalAsync(rental);
    }

    public Task<Rental> GetRentalByIdAsync(string id) => _repository.GetRentalByIdAsync(id);

    public Task<CursorPage<Rental>> GetRentalsByUserId(
        string userId,
        string? cursor,
        int? pageSize) => _repository.GetRentalsByUserId(userId, cursor, pageSize);

    public Task<bool> HasOverlappingRentalAsync(
        string licencePlate,
        DateTime startDate,
        DateTime endDate) =>
        _repository.HasOverlappingRentalAsync(licencePlate, startDate, endDate);

    public Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate) =>
        _repository.IsMotorcycleCurrentlyRentedAsync(licencePlate);

    public Task UpdateRentalAsync(Rental rental) => _repository.UpdateRentalAsync(rental);

    public Task UpdateLicensePlateForAllRentalsAsync(string oldLicensePlate, string newLicensePlate) =>
        _repository.UpdateLicensePlateForAllRentalsAsync(oldLicensePlate, newLicensePlate);

    public Task<bool> TryReserveLicensePlateRenameAsync(
        string oldLicensePlate,
        string newLicensePlate) =>
        _repository.TryReserveLicensePlateRenameAsync(oldLicensePlate, newLicensePlate);

    public Task DeleteRentalAsync(string id) => _repository.DeleteRentalAsync(id);

    public Task<RentalOperations.Domain.MotorcycleClaimResult> TryClaimRentalAsync(
        string licencePlate,
        string rentalId) => TryClaimRentalAfterGateAsync(licencePlate, rentalId);

    private async Task<RentalOperations.Domain.MotorcycleClaimResult> TryClaimRentalAfterGateAsync(
        string licencePlate,
        string rentalId)
    {
        await _gate.WaitAsync();
        return await _repository.TryClaimRentalAsync(licencePlate, rentalId);
    }

    public Task<RentalOperations.Domain.MotorcycleClaimResult> TryClaimRetirementAsync(
        string licencePlate) => _repository.TryClaimRetirementAsync(licencePlate);

    public Task ReleaseRentalClaimAsync(string licencePlate, string rentalId) =>
        _repository.ReleaseRentalClaimAsync(licencePlate, rentalId);
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
    public System.Collections.Concurrent.ConcurrentBag<string> HistoricalReferences { get; } = new();

    public Task<Motorcycle> GetMotorcycleByIdAsync(string motorcycleId) => Task.FromResult(new Motorcycle
    {
        licensePlate = motorcycleId,
        model = "Concurrency test",
        year = 2026
    });

    public Task EnsureHistoricalReferencesAsync(IEnumerable<string> licensePlates)
    {
        foreach (var licensePlate in licensePlates)
        {
            HistoricalReferences.Add(licensePlate);
        }

        return Task.CompletedTask;
    }
}
