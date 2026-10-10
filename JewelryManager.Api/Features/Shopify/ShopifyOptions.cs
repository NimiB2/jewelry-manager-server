namespace JewelryManager.Api.Features.Shopify;

/// <summary>
/// The connection to the store. Bound from the "Shopify" configuration section; the token and the
/// webhook secret come from env vars (Render), never from the repository.
/// </summary>
public class ShopifyOptions
{
    public const string SectionName = "Shopify";

    // Values pasted into a hosting dashboard often carry a stray space or newline.
    private string _shopDomain = "";
    private string _accessToken = "";
    private string _webhookSecret = "";
    private string _apiVersion = "2026-07";

    /// <summary>The store's own address, e.g. my-shop.myshopify.com (no https://).</summary>
    public string ShopDomain
    {
        get => _shopDomain;
        set => _shopDomain = Clean(value).Replace("https://", "").TrimEnd('/');
    }

    /// <summary>Admin API access token of the custom app (read-only scopes).</summary>
    public string AccessToken { get => _accessToken; set => _accessToken = Clean(value); }

    /// <summary>The app's secret, used to verify that a webhook really comes from the store.</summary>
    public string WebhookSecret { get => _webhookSecret; set => _webhookSecret = Clean(value); }

    public string ApiVersion
    {
        get => _apiVersion;
        set => _apiVersion = Clean(value) is { Length: > 0 } version ? version : "2026-07";
    }

    /// <summary>Reading the catalog needs the domain and the token.</summary>
    public bool CanReadCatalog => ShopDomain.Length > 0 && AccessToken.Length > 0;

    /// <summary>Receiving orders needs the secret (and the domain, to know which store is allowed).</summary>
    public bool CanReceiveOrders => ShopDomain.Length > 0 && WebhookSecret.Length > 0;

    private static string Clean(string? value) => value?.Trim() ?? "";
}
