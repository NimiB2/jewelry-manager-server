using System.Globalization;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Analytics;

/// <summary>
/// Read-only views of her own business used by the agent to advise her: sales over time, growth signals,
/// best and slow sellers, repeat customers, and what a change in metal prices or a discount would do.
/// Real orders only (numbers from 1000), never deleted ones or ones still waiting for approval.
/// </summary>
public class AnalyticsService(AppDbContext db, CurrentUserAccessor tenant, ProductsService products, PricingService pricing)
{
    private const int FirstRealOrderNumber = 1000;

    private IQueryable<Order> RealOrders(Guid businessId) =>
        db.Orders.AsNoTracking().Where(o => o.BusinessId == businessId && !o.IsDeleted && !o.IsPendingApproval && o.Number >= FirstRealOrderNumber);

    public async Task<List<SalesRow>> SalesAsync(DateOnly from, DateOnly to, string groupBy)
    {
        if (to < from) throw new BadRequestException("'to' is before 'from'");
        if (to.DayNumber - from.DayNumber > 366 * 6) throw new BadRequestException("The range is longer than six years");
        var mode = groupBy.Trim().ToLowerInvariant();
        if (mode is not ("day" or "week" or "month")) throw new BadRequestException("groupBy must be day, week or month");

        var businessId = tenant.GetBusinessId();
        var rows = await RealOrders(businessId).Where(o => o.Date >= from && o.Date <= to)
            .Select(o => new { o.Date, o.FinalAmount }).ToListAsync();

        return rows
            .GroupBy(r => PeriodStart(r.Date, mode))
            .OrderBy(g => g.Key)
            .Select(g => new SalesRow(g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Count(), g.Sum(r => r.FinalAmount)))
            .ToList();
    }

    public static DateOnly PeriodStart(DateOnly date, string mode) => mode switch
    {
        "month" => new DateOnly(date.Year, date.Month, 1),
        "week" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        _ => date,
    };

    public async Task<GrowthSnapshot> GrowthAsync()
    {
        var businessId = tenant.GetBusinessId();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var recent = await RealOrders(businessId).Where(o => o.Date >= today.AddDays(-365))
            .Select(o => new { o.Date, o.FinalAmount, o.Customer, o.Source }).ToListAsync();

        var last30 = recent.Count(o => o.Date > today.AddDays(-30));
        var prior = recent.Count(o => o.Date <= today.AddDays(-30) && o.Date > today.AddDays(-120)) / 3m;

        var last90 = recent.Where(o => o.Date > today.AddDays(-90)).ToList();
        var average = last90.Count == 0 ? 0 : Math.Round(last90.Average(o => o.FinalAmount), 2);
        var storeShare = last90.Count == 0 ? 0 : Math.Round((decimal)last90.Count(o => o.Source == OrderSource.Shopify) / last90.Count, 3);

        var customers = recent.Where(o => !string.IsNullOrWhiteSpace(o.Customer))
            .GroupBy(o => o.Customer!.Trim().ToLowerInvariant()).ToList();
        var repeat = customers.Count == 0 ? 0 : Math.Round((decimal)customers.Count(c => c.Count() >= 2) / customers.Count, 3);

        var catalog = await db.Products.AsNoTracking().Where(p => p.BusinessId == businessId)
            .Select(p => new { p.Type, p.Material, p.CreatedAt }).ToListAsync();
        var needing = catalog.Count(p => string.IsNullOrWhiteSpace(p.Type) || string.IsNullOrWhiteSpace(p.Material));
        int? sinceLast = catalog.Count == 0 ? null : (int)(DateTime.UtcNow - catalog.Max(p => p.CreatedAt)).TotalDays;

        var since = today.AddDays(-180);
        var materials = await db.OrderLineItems.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.Order.Date >= since && !l.Order.IsDeleted && !l.Order.IsPendingApproval && l.Order.Number >= FirstRealOrderNumber)
            .GroupBy(l => l.Material).Select(g => new { Material = g.Key, Quantity = g.Sum(l => l.Quantity) }).ToListAsync();
        var totalQuantity = materials.Sum(m => m.Quantity);
        var top = materials.OrderByDescending(m => m.Quantity).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Material));

        return new GrowthSnapshot(last30, Math.Round(prior, 1), average, repeat, catalog.Count, needing, sinceLast, storeShare,
            top?.Material, totalQuantity == 0 || top is null ? 0 : Math.Round((decimal)top.Quantity / totalQuantity, 3));
    }

    public async Task<SellersResponse> SellersAsync(int months)
    {
        months = Math.Clamp(months, 1, 36);
        var businessId = tenant.GetBusinessId();
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-months);

        var lines = await db.OrderLineItems.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.Order.Date >= since && !l.Order.IsDeleted && !l.Order.IsPendingApproval && l.Order.Number >= FirstRealOrderNumber)
            .Select(l => new { l.Name, l.ProductId, l.Quantity, l.UnitPrice }).ToListAsync();

        var best = lines.GroupBy(l => l.Name)
            .Select(g => new SellerRow(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Quantity * l.UnitPrice)))
            .OrderByDescending(r => r.Quantity).ThenByDescending(r => r.Revenue).Take(5).ToList();

        var soldIds = lines.Where(l => l.ProductId != null).Select(l => l.ProductId!.Value).ToHashSet();
        var cutoff = DateTime.UtcNow.AddMonths(-months);
        var slow = (await db.Products.AsNoTracking().Where(p => p.BusinessId == businessId && p.CreatedAt < cutoff)
                .Select(p => new { p.Id, p.Name, p.Type, p.CreatedAt }).ToListAsync())
            .Where(p => !soldIds.Contains(p.Id))
            .OrderBy(p => p.CreatedAt).Take(12)
            .Select(p => new SlowProductRow(p.Name, p.Type, (int)(DateTime.UtcNow - p.CreatedAt).TotalDays)).ToList();

        return new SellersResponse(months, best, slow);
    }

    /// <summary>Customers who ordered about a year ago (give or take a week): the ones worth a thank-you or an anniversary note.</summary>
    public async Task<List<AnniversaryRow>> AnniversariesAsync(int daysBack, int windowDays)
    {
        daysBack = Math.Clamp(daysBack, 30, 1500);
        windowDays = Math.Clamp(windowDays, 1, 21);
        var businessId = tenant.GetBusinessId();
        var center = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-daysBack);

        var orders = await RealOrders(businessId)
            .Where(o => o.Date >= center.AddDays(-windowDays) && o.Date <= center.AddDays(windowDays) && o.Customer != null && o.Customer != "")
            .OrderBy(o => o.Date)
            .Select(o => new { o.Number, o.Customer, o.Date, Item = o.Items.OrderBy(i => i.SortOrder).Select(i => i.Name).FirstOrDefault() })
            .Take(10).ToListAsync();

        return orders.Select(o => new AnniversaryRow(o.Number, o.Customer!, o.Date, o.Item)).ToList();
    }

    public async Task<List<MaterialPriceRow>> MaterialsAsync()
    {
        var businessId = tenant.GetBusinessId();
        return await db.Materials.AsNoTracking().Where(m => m.BusinessId == businessId)
            .OrderBy(m => m.SortOrder).Select(m => new MaterialPriceRow(m.Name, m.PricePerGram)).ToListAsync();
    }

    public async Task<WhatIfResponse> WhatIfAsync(WhatIfRequest request)
    {
        var settings = await pricing.LoadSettingsAsync();
        var modified = settings.Materials.ToDictionary(m => m.Key, m => new Material
        {
            Id = m.Value.Id, BusinessId = m.Value.BusinessId, Name = m.Value.Name, PricePerGram = m.Value.PricePerGram,
            LaborHours = m.Value.LaborHours, ProfitMultiplier = m.Value.ProfitMultiplier, SortOrder = m.Value.SortOrder,
        });

        foreach (var change in request.Changes)
        {
            if (!modified.TryGetValue(change.Material, out var material)) throw new BadRequestException($"There is no material named '{change.Material}'");
            material.PricePerGram = change.NewPricePerGram
                ?? (change.Percent is { } p ? Math.Round(material.PricePerGram * (1 + p / 100), 4) : throw new BadRequestException("Give a new price or a percent"));
            if (material.PricePerGram <= 0) throw new BadRequestException("A price per gram must be above zero");
        }

        var after = settings with { Materials = modified };
        var list = await products.GetProductsAsync();

        // Only products made of a material whose price changes are part of the picture.
        var changed = request.Changes.Select(c => c.Material).ToHashSet();
        var rows = new List<(string Name, string Type, decimal ChangePercent, bool NewlyBelow)>();
        foreach (var p in list.Products.Where(p => p.Price is not null && changed.Contains(p.Material)))
        {
            var newPrice = PricingService.Calculate(after, p.Material, p.Weight, p.AdditionalWorkHours, PricingService.AdditionsCost(p.Additions));
            var change = p.Price!.RecommendedPrice == 0 ? 0 : (newPrice.RecommendedPrice / p.Price.RecommendedPrice - 1) * 100;

            var floor = settings.ProfitFloorPercent;
            var wasBelow = ProfitMath.RateAt(p.SitePrice, p.Price.CostWithFixedExpenses, settings.VatRate, settings.CardFeeRate) * 100 < floor;
            var nowBelow = ProfitMath.RateAt(p.SitePrice, newPrice.CostWithFixedExpenses, settings.VatRate, settings.CardFeeRate) * 100 < floor;
            rows.Add((p.Name, string.IsNullOrWhiteSpace(p.Type) ? "-" : p.Type, Math.Round(change, 1), nowBelow && !wasBelow));
        }

        return new WhatIfResponse(
            rows.Count,
            rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.ChangePercent), 1),
            rows.GroupBy(r => r.Type).Select(g => new WhatIfTypeRow(g.Key, g.Count(), Math.Round(g.Average(r => r.ChangePercent), 1))).OrderByDescending(r => r.AverageChangePercent).ToList(),
            rows.Count(r => r.NewlyBelow),
            rows.Where(r => r.NewlyBelow).Take(5).Select(r => r.Name).ToList());
    }

    public async Task<DiscountCeilingResponse> DiscountCeilingAsync()
    {
        var list = await products.GetProductsAsync();
        var meta = list.Pricing ?? throw new NotFoundException("Pricing settings are missing");

        var rows = list.Products.Where(p => p.Price is not null && p.SitePrice > 0)
            .Select(p => (p.Name, Type: string.IsNullOrWhiteSpace(p.Type) ? "-" : p.Type, p.SitePrice,
                Max: ProfitMath.MaxDiscountPercent(p.SitePrice, p.Price!.CostWithFixedExpenses, meta.VatRate, meta.CardFeeRate, meta.ProfitFloorPercent),
                Below: ProfitMath.RateAt(p.SitePrice, p.Price.CostWithFixedExpenses, meta.VatRate, meta.CardFeeRate) * 100 < meta.ProfitFloorPercent))
            .ToList();

        var byType = rows.GroupBy(r => r.Type).Select(g =>
        {
            var sorted = g.Select(r => r.Max).OrderBy(x => x).ToList();
            return new DiscountCeilingRow(g.Key, sorted.Count, sorted[0], sorted[sorted.Count / 2]);
        }).OrderBy(r => r.Type).ToList();

        return new DiscountCeilingResponse(meta.ProfitFloorPercent, byType, rows.Where(r => r.Below).Select(r => r.Name).Take(8).ToList());
    }
}
