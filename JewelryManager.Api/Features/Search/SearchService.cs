using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Search.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Search;

/// <summary>
/// One search over orders, products and expenses. A few results of each kind are enough: it is for finding
/// the one thing she is looking for, and the screens have their own filters for browsing.
/// </summary>
public class SearchService(AppDbContext db, CurrentUserAccessor tenant)
{
    private const int MinLength = 2;
    private const int PerKind = 8;

    // The escape character of the pattern, stated explicitly so a typed % or _ is a letter, not a wildcard.
    private const string Esc = "\\";

    public async Task<SearchResponse> SearchAsync(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length < MinLength) return new SearchResponse(text, [], [], []);

        var businessId = tenant.GetBusinessId();
        var like = $"%{Escape(text)}%";
        var asNumber = int.TryParse(text.TrimStart('#'), out var number) ? number : (int?)null;

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && !o.IsDeleted
                && ((o.Customer != null && EF.Functions.ILike(o.Customer, like, Esc))
                    || (o.ExternalName != null && EF.Functions.ILike(o.ExternalName, like, Esc))
                    || (asNumber != null && o.Number == asNumber)))
            .OrderByDescending(o => o.Date).ThenByDescending(o => o.Number)
            .Take(PerKind)
            .Select(o => new OrderHit(o.Id, o.Number, o.Customer, o.ExternalName, o.Date, o.FinalAmount, o.Status, o.IsPendingApproval))
            .ToListAsync();

        var products = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId
                && (EF.Functions.ILike(p.Name, like, Esc) || (p.ShopifyName != null && EF.Functions.ILike(p.ShopifyName, like, Esc))))
            .OrderBy(p => p.Name)
            .Take(PerKind)
            .Select(p => new ProductHit(p.Id, p.Name, p.Type, p.Material, p.SitePrice, p.ShopifyName))
            .ToListAsync();

        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId
                && (EF.Functions.ILike(e.Description, like, Esc)
                    || (e.Supplier != null && EF.Functions.ILike(e.Supplier, like, Esc))
                    || (e.Notes != null && EF.Functions.ILike(e.Notes, like, Esc))))
            .OrderByDescending(e => e.Date)
            .Take(PerKind)
            .Select(e => new ExpenseHit(e.Id, e.Date, e.Description, e.Supplier, e.Amount, e.HasReceipt || e.InvoiceStoredName != null))
            .ToListAsync();

        return new SearchResponse(text, orders, products, expenses);
    }

    // A typed % or _ must be found as itself, not act as a wildcard.
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
