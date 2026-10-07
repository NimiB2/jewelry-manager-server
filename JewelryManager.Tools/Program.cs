using JewelryManager.Api.Data;
using JewelryManager.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// Usage (from server-dotnet/):
//   dotnet run --project JewelryManager.Tools -- import-products <file.json> [--dry-run]
// The connection string comes from JewelryManager.Api/appsettings.Development.json or the
// ConnectionStrings__Default environment variable. Nothing secret is printed.

if (args.Length < 2 || args[0] is not ("import-products" or "reprice-products"))
{
    Console.WriteLine("Usage: import-products <file.json> [--dry-run]");
    Console.WriteLine("       reprice-products <file.json> [--dry-run]   (rows without a sheet price get the recommended price)");
    return 1;
}

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile(Path.Combine("JewelryManager.Api", "appsettings.Development.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

var business = await db.Businesses.AsNoTracking().ToListAsync();
if (business.Count != 1)
    throw new InvalidOperationException($"Expected exactly one business, found {business.Count}.");

var dryRun = args.Contains("--dry-run");
Console.WriteLine($"{(dryRun ? "DRY RUN — nothing is saved. " : "")}Business: {business[0].Name}");

var importer = new ProductImporter(db, business[0].Id);

if (args[0] == "reprice-products")
{
    var reprice = await importer.RepriceFromRecommendationAsync(args[1], dryRun);
    Console.WriteLine($"Updated: {reprice.Updated}   Already at the recommended price: {reprice.Unchanged}   Not found: {reprice.NotFound}   Failed: {reprice.Failed.Count}");
    foreach (var line in reprice.Failed) Console.WriteLine($"FAILED  {line}");
    return reprice.Failed.Count == 0 ? 0 : 2;
}

var result = await importer.ImportAsync(args[1], dryRun);

Console.WriteLine($"Created: {result.Created}   Already existed (skipped): {result.Skipped}   Failed: {result.Failed.Count}");
Console.WriteLine($"Priced from the recommended price (no price in the sheet): {result.PricedFromRecommendation.Count}");
foreach (var line in result.Failed) Console.WriteLine($"FAILED  {line}");
return result.Failed.Count == 0 ? 0 : 2;
