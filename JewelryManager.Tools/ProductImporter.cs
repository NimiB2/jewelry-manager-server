using System.Text.Json;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Collections.Dtos;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Pricing.Dtos;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tools;

/// <summary>One product row as prepared from the owner's spreadsheet.</summary>
public record ImportRow(
    int Sheet,
    int SourceRow,
    string? Collection,
    string Type,
    string Name,
    string Material,
    decimal Weight,
    List<ProductAdditionDto> Additions,
    decimal? SitePrice);

public record ImportResult(
    int Created,
    int Skipped,
    List<string> PricedFromRecommendation,
    List<string> Failed);

/// <summary>
/// Bulk-adds products through the very same services the API uses (validation, default collection,
/// live pricing), so imported products are indistinguishable from ones made in the calculator.
/// Re-running is safe: products that already exist are skipped.
/// </summary>
public class ProductImporter(AppDbContext db, Guid businessId)
{
    public async Task<ImportResult> ImportAsync(string jsonPath, bool dryRun)
    {
        var rows = JsonSerializer.Deserialize<List<ImportRow>>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

        var tenant = TenantFor(businessId);
        var collections = new CollectionsService(db, tenant);
        var pricing = new PricingService(db, tenant);
        var products = new ProductsService(db, tenant, pricing);

        var collectionIds = await ResolveCollectionsAsync(collections, rows, dryRun);
        var existing = await LoadExistingKeysAsync();

        int created = 0, skipped = 0;
        var fromRecommendation = new List<string>();
        var failed = new List<string>();

        foreach (var row in rows)
        {
            var label = $"גיליון {row.Sheet} שורה {row.SourceRow}: {row.Name} ({row.Material})";
            var key = Key(row.Type, row.Name, row.Material, row.Weight, row.Collection, row.Additions);
            if (!existing.Add(key))
            {
                skipped++;
                continue;
            }

            try
            {
                var sitePrice = row.SitePrice;
                if (sitePrice is null)
                {
                    // No price in the sheet: start from the recommended price, like the calculator does.
                    var preview = await pricing.CalculateAsync(
                        new CalculatePriceDto(row.Material, row.Weight, 0, row.Additions));
                    sitePrice = Math.Round(preview.RecommendedPrice);
                    fromRecommendation.Add(label);
                }

                var ids = row.Collection is null ? new List<Guid>() : [collectionIds[row.Collection]];
                var dto = new SaveProductDto(row.Type, row.Name, row.Material, row.Weight, 0, sitePrice.Value, row.Additions, ids);

                if (!dryRun) await products.CreateProductAsync(dto);
                created++;
            }
            catch (Exception ex)
            {
                failed.Add($"{label} — {ex.Message}");
            }
        }

        return new ImportResult(created, skipped, fromRecommendation, failed);
    }

    // The sheets map to collections: the main sheet goes to "General" (the default), the others to their own.
    private async Task<Dictionary<string, Guid>> ResolveCollectionsAsync(
        CollectionsService collections, List<ImportRow> rows, bool dryRun)
    {
        var result = new Dictionary<string, Guid>();
        var current = await collections.GetCollectionsAsync();

        foreach (var name in rows.Select(r => r.Collection).Where(n => n is not null).Distinct())
        {
            var found = current.FirstOrDefault(c => c.Name == name);
            if (found is null && !dryRun) found = await collections.CreateCollectionAsync(new CreateCollectionDto(name!));
            result[name!] = found?.Id ?? Guid.Empty;
        }

        return result;
    }

    private async Task<HashSet<string>> LoadExistingKeysAsync()
    {
        var products = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId)
            .Include(p => p.Additions)
            .Include(p => p.Collections).ThenInclude(c => c.Collection)
            .ToListAsync();

        return products.Select(p => Key(
            p.Type, p.Name, p.Material, p.Weight,
            p.Collections.Select(c => c.Collection.Name).FirstOrDefault(n => n is not "כללי"),
            p.Additions.Select(a => new ProductAdditionDto(a.TypeName, a.CustomName, a.Price, a.Quantity)))).ToHashSet();
    }

    private static string Key(string type, string name, string material, decimal weight, string? collection,
        IEnumerable<ProductAdditionDto> additions) =>
        string.Join("|", type, name, material, weight.ToString("0.####"), collection ?? "",
            string.Join(",", additions.OrderBy(a => a.TypeName).Select(a => $"{a.TypeName}:{a.Price:0.##}x{a.Quantity}")));

    // The services read the tenant from the signed-in user; the tool acts as that business's owner.
    private static CurrentUserAccessor TenantFor(Guid businessId)
    {
        var http = new DefaultHttpContext();
        var accessor = new CurrentUserAccessor(new HttpContextAccessor { HttpContext = http });
        accessor.SetUser(http, new User { Id = Guid.NewGuid(), BusinessId = businessId, Email = "import-tool", Role = Role.Owner });
        return accessor;
    }
}
