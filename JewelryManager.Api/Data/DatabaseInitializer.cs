using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations and seeds the default data. Always in Development; elsewhere only when
    /// Database:MigrateOnStart is true (a single server instance, so two never migrate at once).
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>("Database:MigrateOnStart")) return;

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
    }
}
