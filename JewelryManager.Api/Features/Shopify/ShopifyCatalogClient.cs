using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using JewelryManager.Api.Common.Exceptions;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Shopify;

public record StoreVariant(string Title, decimal Price, string? Sku);

/// <summary>A product as the store lists it. ExternalId is the store's numeric product id.</summary>
public record StoreProduct(string ExternalId, string Title, IReadOnlyList<StoreVariant> Variants)
{
    public decimal LowestPrice => Variants.Count == 0 ? 0 : Variants.Min(v => v.Price);
}

/// <summary>Read-only view of the store's catalog.</summary>
public interface IShopifyCatalogClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<StoreProduct>> GetActiveProductsAsync();
}

/// <summary>Reads one page of the GraphQL products answer. Pure, so it is tested without the network.</summary>
public static class ShopifyCatalogParser
{
    public static (List<StoreProduct> Products, bool HasNext, string? EndCursor) ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new BadRequestException("Shopify דחתה את הבקשה. כדאי לבדוק את ההרשאות של האפליקציה בחנות");

        var products = doc.RootElement.GetProperty("data").GetProperty("products");
        var page = products.GetProperty("pageInfo");
        var result = new List<StoreProduct>();

        foreach (var node in products.GetProperty("nodes").EnumerateArray())
        {
            var id = LastSegment(node.GetProperty("id").GetString());
            var title = node.GetProperty("title").GetString()?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;

            var variants = new List<StoreVariant>();
            foreach (var variant in node.GetProperty("variants").GetProperty("nodes").EnumerateArray())
            {
                if (!decimal.TryParse(variant.GetProperty("price").GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                    continue;

                var variantTitle = variant.GetProperty("title").GetString()?.Trim();
                // A product without options has one variant called "Default Title"; that is no information.
                if (string.Equals(variantTitle, "Default Title", StringComparison.OrdinalIgnoreCase)) variantTitle = "";

                var sku = variant.TryGetProperty("sku", out var skuElement) ? skuElement.GetString()?.Trim() : null;
                variants.Add(new StoreVariant(Cut(variantTitle ?? "", 200), price, string.IsNullOrEmpty(sku) ? null : Cut(sku, 100)));
            }

            if (variants.Count == 0) continue;
            result.Add(new StoreProduct(id, Cut(title, 300), variants));
        }

        var end = page.TryGetProperty("endCursor", out var cursor) ? cursor.GetString() : null;
        return (result, page.GetProperty("hasNextPage").GetBoolean(), end);
    }

    // "gid://shopify/Product/123456" becomes "123456", the id that order lines carry as product_id.
    private static string? LastSegment(string? gid) => gid?[(gid.LastIndexOf('/') + 1)..];

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}

public class ShopifyCatalogClient(HttpClient http, IOptions<ShopifyOptions> options) : IShopifyCatalogClient
{
    private const int MaxPages = 20; // 2,000 products: far above what the shop has

    private const string Query = """
        query($cursor: String) {
          products(first: 100, after: $cursor, query: "status:active") {
            pageInfo { hasNextPage endCursor }
            nodes { id title variants(first: 100) { nodes { title sku price } } }
          }
        }
        """;

    public bool IsConfigured => options.Value.CanReadCatalog;

    public async Task<IReadOnlyList<StoreProduct>> GetActiveProductsAsync()
    {
        var shop = options.Value;
        if (!shop.CanReadCatalog) throw new BadRequestException("החיבור ל-Shopify עדיין לא הוגדר");

        var all = new List<StoreProduct>();
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{shop.ShopDomain}/admin/api/{shop.ApiVersion}/graphql.json")
            {
                Content = JsonContent.Create(new { query = Query, variables = new { cursor } }),
            };
            // The token travels in a header only, never in the address.
            request.Headers.Add("X-Shopify-Access-Token", shop.AccessToken);

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new BadRequestException($"Shopify החזירה שגיאה ({(int)response.StatusCode}). כדאי לבדוק את כתובת החנות והמפתח");

            var (products, hasNext, end) = ShopifyCatalogParser.ParsePage(await response.Content.ReadAsStringAsync());
            all.AddRange(products);

            if (!hasNext || end is null) break;
            cursor = end;
        }

        return all;
    }
}
