using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>
/// Real PostgreSQL (the docker-compose one) with a dedicated, freshly migrated test database.
/// "Restarting the server" is simulated by creating brand-new DbContext instances:
/// everything they read must come from the database, not from memory.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    public const string TestDatabase = "jewelry_manager_test";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? $"Host=localhost;Port=5432;Database={TestDatabase};Username=jewelry;Password=jewelry_dev_password";

    public Guid BusinessA { get; } = Guid.NewGuid();
    public Guid BusinessB { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        db.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "A", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessB, Name = "B", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>A tenant accessor as FirebaseAuthMiddleware would leave it for a signed-in user.</summary>
    public static CurrentUserAccessor TenantFor(Guid businessId, Role role = Role.Owner)
    {
        var http = new DefaultHttpContext();
        var accessor = new CurrentUserAccessor(new HttpContextAccessor { HttpContext = http });
        accessor.SetUser(http, new User { Id = Guid.NewGuid(), BusinessId = businessId, Email = "t@test", Role = role });
        return accessor;
    }
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
