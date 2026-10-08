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

    // The password is never hardcoded: it comes from TEST_DB_CONNECTION, or from the
    // git-ignored .env next to docker-compose.yml (the same file the container uses).
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? $"Host=localhost;Port=5432;Database={TestDatabase};Username=jewelry;Password={ReadDbPassword()}";

    private static string ReadDbPassword()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var envFile = Path.Combine(dir.FullName, ".env");
                if (!File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) || !File.Exists(envFile)) continue;

                var line = File.ReadLines(envFile).FirstOrDefault(l => l.StartsWith("DB_PASSWORD="));
                if (line is not null) return line["DB_PASSWORD=".Length..].Trim();
            }
        }

        throw new InvalidOperationException("Set TEST_DB_CONNECTION or create server-dotnet/.env with DB_PASSWORD.");
    }

    public Guid BusinessA { get; } = Guid.NewGuid();
    public Guid BusinessB { get; } = Guid.NewGuid();
    public Guid BusinessC { get; } = Guid.NewGuid();
    public Guid BusinessD { get; } = Guid.NewGuid();
    public Guid BusinessE { get; } = Guid.NewGuid();
    public Guid BusinessF { get; } = Guid.NewGuid();
    public Guid BusinessG { get; } = Guid.NewGuid();
    public Guid BusinessH { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        db.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "A", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessB, Name = "B", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessC, Name = "C", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessD, Name = "D", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessE, Name = "E", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessF, Name = "F", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessG, Name = "G", CreatedAt = now, UpdatedAt = now },
            new Business { Id = BusinessH, Name = "H", CreatedAt = now, UpdatedAt = now });
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
