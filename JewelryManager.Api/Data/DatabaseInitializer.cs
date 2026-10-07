using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>Development only: applies pending migrations and seeds the default data.</summary>
    public static async Task InitializeDevelopmentDatabaseAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
    }
}
