using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Shopify;

public enum StoreOrderImportResult
{
    Created,
    Duplicate,
    NoBusiness,
}

/// <summary>
/// Turns a verified store order into a pending order of the right business. A webhook has no signed-in
/// user, so the business is found from the store's address and set explicitly on the tenant accessor.
/// </summary>
public class ShopifyOrderImportService(
    AppDbContext db, CurrentUserAccessor tenant, OrdersService orders, IOptions<ShopifyOptions> options)
{
    public async Task<StoreOrderImportResult> ImportAsync(IncomingOrder incoming)
    {
        var businessId = await ResolveBusinessAsync();
        if (businessId is null) return StoreOrderImportResult.NoBusiness;

        tenant.UseBusiness(businessId.Value);
        return await orders.ImportStoreOrderAsync(incoming) ? StoreOrderImportResult.Created : StoreOrderImportResult.Duplicate;
    }

    // The business is the one whose Shopify settings name this store; with a single business there is no doubt.
    private async Task<Guid?> ResolveBusinessAsync()
    {
        var domain = options.Value.ShopDomain;

        var businesses = await db.Businesses.AsNoTracking()
            .Select(b => new { b.Id, b.ShopifySettings })
            .ToListAsync();

        if (businesses.Count == 1) return businesses[0].Id;

        return businesses
            .FirstOrDefault(b => b.ShopifySettings is not null
                                 && b.ShopifySettings.Contains(domain, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}
