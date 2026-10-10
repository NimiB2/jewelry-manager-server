using JewelryManager.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JewelryManager.Tools;

/// <summary>
/// Says what is in the database the tool is pointed at: which server, and how much of everything.
/// Used to check where an import really went. The password is never printed.
/// </summary>
public static class StatusReport
{
    public static async Task PrintAsync(AppDbContext db, string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Console.WriteLine($"Server:   {builder.Host}:{builder.Port}");
        Console.WriteLine($"Database: {builder.Database}");

        var businesses = await db.Businesses.AsNoTracking().Select(b => b.Name).ToListAsync();
        Console.WriteLine($"Businesses: {string.Join(", ", businesses)}");
        Console.WriteLine($"Users: {await db.Users.CountAsync()}");
        Console.WriteLine($"Materials: {string.Join(", ", await db.Materials.AsNoTracking().Select(m => m.Name).ToListAsync())}");

        var products = await db.Products.AsNoTracking().CountAsync();
        Console.WriteLine($"Products: {products}");
        Console.WriteLine($"  with a store name: {await db.Products.CountAsync(p => p.ShopifyName != null)}");
        var missingDetails = await db.Products.CountAsync(p => p.Type == "" || p.Material == "");
        Console.WriteLine($"  missing type or material: {missingDetails}");

        Console.WriteLine("Collections:");
        var counts = await db.Collections.AsNoTracking()
            .Select(c => new { c.Name, Count = db.ProductCollections.Count(pc => pc.CollectionId == c.Id) })
            .OrderBy(c => c.Name)
            .ToListAsync();
        foreach (var c in counts) Console.WriteLine($"  {c.Name}: {c.Count}");

        Console.WriteLine($"Orders: {await db.Orders.CountAsync()}");
    }
}
