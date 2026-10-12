using JewelryManager.Api.Common.Configuration;
using JewelryManager.Api.Data;
using JewelryManager.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// Usage (from server-dotnet/):
//   dotnet run --project JewelryManager.Tools -- import-products <file.json> [--dry-run]
//   dotnet run --project JewelryManager.Tools -- import-orders <orders_export.csv> [--dry-run]
//   dotnet run --project JewelryManager.Tools -- export-products <products.xlsx>
//   dotnet run --project JewelryManager.Tools -- update-products <products.xlsx> [--dry-run]
// The connection string comes from JewelryManager.Api/appsettings.Development.json or the
// ConnectionStrings__Default environment variable. Nothing secret is printed.

if (args.Length < 1 || (args[0] != "status" && args.Length < 2)
    || args[0] is not ("status" or "import-products" or "import-orders" or "export-products" or "update-products" or "prepare-review" or "reprice-products" or "read-invoices" or "export-settings" or "import-settings"))
{
    Console.WriteLine("Usage: status   (what is in the database this tool points at)");
    Console.WriteLine("       import-products <file.json> [--dry-run]");
    Console.WriteLine("       import-orders <orders_export.csv> [--dry-run]   (the store's old orders, as finished sales)");
    Console.WriteLine("       export-products <products.xlsx>   (every product in an Excel file to fill in)");
    Console.WriteLine("       update-products <products.xlsx> [--dry-run]   (applies the edited Excel file back)");
    Console.WriteLine("       prepare-review <in.xlsx> <out.xlsx>   (fills what can be worked out from the names and marks the rest, for a careful look; needs no database)");
    Console.WriteLine("       reprice-products <file.json> [--dry-run]   (rows without a sheet price get the recommended price)");
    Console.WriteLine("       export-settings <file.json>   (saves the settings of this database)");
    Console.WriteLine("       import-settings <file.json> [--dry-run]   (replaces the settings of this database)");
    Console.WriteLine("       read-invoices <folder> [--provider gemini|openai|claude] [--model <name>] [--key-env <ENV_VAR>] [--types a,b,c]");
    return 1;
}

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile(Path.Combine("JewelryManager.Api", "appsettings.json"), optional: true)
    .AddJsonFile(Path.Combine("JewelryManager.Api", "appsettings.Development.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();

// Needs no database: it only rewrites an Excel file.
if (args[0] == "prepare-review")
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: prepare-review <in.xlsx> <out.xlsx>");
        return 1;
    }

    var review = ReviewSheet.Prepare(args[1], args[2]);
    Console.WriteLine($"Saved {args[2]}");
    Console.WriteLine($"Products: {review.Products}   Complete: {review.Complete}");
    Console.WriteLine($"Material filled: {review.MaterialsSuggested} clear (gold), {review.MaterialsGuessed} guessed (silver, to check)   Type filled: {review.TypesSuggested}");
    Console.WriteLine($"Still without: type {review.StillNoType}, material {review.StillNoMaterial}, weight {review.NoWeight}, price {review.NoPrice}");
    foreach (var name in review.Unresolved) Console.WriteLine("UNRESOLVED  " + name);
    return 0;
}

// Needs no database: it only sends invoice files to the configured AI provider.
if (args[0] == "read-invoices")
    return await InvoiceReaderRunner.RunAsync(args[1], args, config);

// Accepts the key=value form and the postgres:// URL that hosting dashboards hand out (e.g. Render).
var connectionString = PostgresConnectionString.Normalize(
    config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."));

await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

if (args[0] == "status")
{
    await StatusReport.PrintAsync(db, connectionString);
    return 0;
}

var business = await db.Businesses.AsNoTracking().ToListAsync();
if (business.Count != 1)
    throw new InvalidOperationException($"Expected exactly one business, found {business.Count}.");

var dryRun = args.Contains("--dry-run");
Console.WriteLine($"{(dryRun ? "DRY RUN — nothing is saved. " : "")}Business: {business[0].Name}");

if (args[0] is "export-settings" or "import-settings")
{
    var exporting = args[0] == "export-settings";
    var settings = exporting
        ? await SettingsTransfer.ExportAsync(db, business[0].Id, args[1])
        : await SettingsTransfer.ImportAsync(db, business[0].Id, args[1], dryRun);

    Console.WriteLine(exporting ? $"Saved the settings to {args[1]}" : dryRun ? "Would replace the settings with:" : "Settings replaced with:");
    foreach (var line in SettingsTransfer.Describe(settings)) Console.WriteLine("  " + line);
    return 0;
}

if (args[0] == "import-orders")
{
    var history = await new OrderHistoryImporter(db, business[0].Id).ImportAsync(args[1], dryRun);

    Console.WriteLine($"{(dryRun ? "Would create" : "Created")}: {history.Created} (completed: {history.Completed}, still open: {history.Open})   Already there (skipped): {history.AlreadyThere}");
    Console.WriteLine($"Item lines tied to a catalog product: {history.MatchedLines}   Not found in the catalog (kept with their store name): {history.UnmatchedLines}");
    Console.WriteLine($"Left out on purpose: {history.Skipped.Count}   Failed: {history.Failed.Count}");

    foreach (var title in history.UnmatchedTitles) Console.WriteLine($"NO PRODUCT  {title}");
    foreach (var skipped in history.Skipped) Console.WriteLine($"SKIPPED  {skipped.Name} — {skipped.Reason}");
    foreach (var line in history.Failed) Console.WriteLine($"FAILED  {line}");
    return history.Failed.Count == 0 ? 0 : 2;
}

if (args[0] == "export-products")
{
    var exported = await ProductSheet.ExportAsync(db, business[0].Id, args[1]);
    Console.WriteLine($"Saved {exported.Products} products ({exported.Additions} additions) to {args[1]}");
    Console.WriteLine($"Still missing a type or a material: {exported.NeedingDetails}");
    return 0;
}

if (args[0] == "update-products")
{
    var sheet = await ProductSheet.ImportAsync(db, business[0].Id, args[1], dryRun);

    Console.WriteLine($"{(dryRun ? "Would update" : "Updated")}: {sheet.Updated}   Unchanged: {sheet.Unchanged}   New products: {sheet.Created}   Problems: {sheet.Problems.Count}");
    foreach (var change in sheet.Changes) Console.WriteLine("CHANGE   " + change);
    foreach (var problem in sheet.Problems) Console.WriteLine("PROBLEM  " + problem);
    return sheet.Problems.Count == 0 ? 0 : 2;
}

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
