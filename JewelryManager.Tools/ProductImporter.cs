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
    decimal? SitePrice,
    // More than one collection (a label like "not on the site" next to the sheet's own); falls back to Collection.
    List<string>? Collections = null,
    // The online store's side of the product, when it is known.
    string? ShopifyName = null,
    string? ShopifyProductId = null,
    List<ImportVariant>? ShopifyVariants = null);

public record ImportVariant(string Title, decimal Price, string? Sku);

public record RepriceResult(int Updated, int Unchanged, int NotFound, List<string> Failed);

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
            var key = Key(row.Type, row.Name, row.Material, row.Weight, CollectionNames(row), row.Additions);
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

                var ids = CollectionNames(row).Select(n => collectionIds[n]).ToList();
                var dto = new SaveProductDto(
                    row.Type, row.Name, row.Material, row.Weight, 0, sitePrice.Value, row.Additions, ids, row.ShopifyName);

                if (!dryRun)
                {
                    var saved = await products.CreateProductAsync(dto);
                    await TieToStoreAsync(saved.Id, row);
                }
                created++;
            }
            catch (Exception ex)
            {
                failed.Add($"{label} — {ex.Message}");
            }
        }

        return new ImportResult(created, skipped, fromRecommendation, failed);
    }

    /// <summary>
    /// For the rows that had no price in the sheet, sets the site price to the current recommended
    /// price (rounded). Rows that did have a price are never touched.
    /// </summary>
    public async Task<RepriceResult> RepriceFromRecommendationAsync(string jsonPath, bool dryRun)
    {
        var rows = (JsonSerializer.Deserialize<List<ImportRow>>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []).Where(r => r.SitePrice is null).ToList();

        var tenant = TenantFor(businessId);
        var products = new ProductsService(db, tenant, new PricingService(db, tenant));
        var existing = await LoadExistingProductsAsync();

        int updated = 0, unchanged = 0, notFound = 0;
        var failed = new List<string>();

        foreach (var row in rows)
        {
            var label = $"גיליון {row.Sheet} שורה {row.SourceRow}: {row.Name} ({row.Material})";
            if (!existing.TryGetValue(Key(row.Type, row.Name, row.Material, row.Weight, CollectionNames(row), row.Additions), out var id))
            {
                notFound++;
                continue;
            }

            try
            {
                var product = await products.GetProductAsync(id);
                if (product.Price is null) throw new InvalidOperationException(product.PriceError ?? "no price");

                var newPrice = Math.Round(product.Price.RecommendedPrice);
                if (newPrice == product.SitePrice)
                {
                    unchanged++;
                    continue;
                }

                if (!dryRun) await products.UpdateSitePriceAsync(id, new UpdateSitePriceDto(newPrice));
                updated++;
            }
            catch (Exception ex)
            {
                failed.Add($"{label} — {ex.Message}");
            }
        }

        return new RepriceResult(updated, unchanged, notFound, failed);
    }

    private async Task<Dictionary<string, Guid>> LoadExistingProductsAsync()
    {
        var products = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId)
            .Include(p => p.Additions)
            .Include(p => p.Collections).ThenInclude(c => c.Collection)
            .ToListAsync();

        return products.ToDictionary(p => Key(
            p.Type, p.Name, p.Material, p.Weight,
            p.Collections.Select(c => c.Collection.Name).Where(n => n is not "כללי"),
            p.Additions.Select(a => new ProductAdditionDto(a.TypeName, a.CustomName, a.Price, a.Quantity))), p => p.Id);
    }

    // The sheets map to collections: the main sheet goes to "General" (the default), the others to their own.
    private async Task<Dictionary<string, Guid>> ResolveCollectionsAsync(
        CollectionsService collections, List<ImportRow> rows, bool dryRun)
    {
        var result = new Dictionary<string, Guid>();
        var current = await collections.GetCollectionsAsync();

        foreach (var name in rows.SelectMany(CollectionNames).Distinct())
        {
            var found = current.FirstOrDefault(c => c.Name == name);
            if (found is null && !dryRun) found = await collections.CreateCollectionAsync(new CreateCollectionDto(name));
            result[name] = found?.Id ?? Guid.Empty;
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
            p.Collections.Select(c => c.Collection.Name).Where(n => n is not "כללי"),
            p.Additions.Select(a => new ProductAdditionDto(a.TypeName, a.CustomName, a.Price, a.Quantity)))).ToHashSet();
    }

    // The collections a row belongs to, without the default one (a product with none lands in "General").
    private static List<string> CollectionNames(ImportRow row) =>
        (row.Collections ?? (row.Collection is null ? [] : [row.Collection]))
            .Where(n => n is not "כללי").Distinct().ToList();

    // The store's id and variants are not part of the normal product form, so they are written straight after the create.
    private async Task TieToStoreAsync(Guid productId, ImportRow row)
    {
        if (row.ShopifyProductId is null && row.ShopifyVariants is null) return;

        var product = await db.Products.FirstAsync(p => p.Id == productId && p.BusinessId == businessId);
        product.ShopifyProductId = row.ShopifyProductId;
        if (row.ShopifyVariants is { Count: > 0 })
            product.ShopifyVariants = JsonSerializer.Serialize(row.ShopifyVariants.Select(v => new ShopifyVariantDto(v.Title, v.Price, v.Sku)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static string Key(string type, string name, string material, decimal weight, IEnumerable<string> collections,
        IEnumerable<ProductAdditionDto> additions) =>
        string.Join("|", type, name, material, weight.ToString("0.####"), string.Join("+", collections.OrderBy(c => c)),
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
