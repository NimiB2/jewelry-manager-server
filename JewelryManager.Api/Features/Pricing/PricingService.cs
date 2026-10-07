using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Pricing.Dtos;
using JewelryManager.Api.Features.Products.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Pricing;

/// <summary>The current pricing settings of one business, loaded once and reused for many products.</summary>
public record PricingSettings(
    decimal LaborHourRate,
    decimal ProfitFloorPercent,
    IReadOnlyDictionary<string, Material> Materials,
    decimal FixedExpenseRate,
    decimal CardFeeRate,
    decimal VatRate,
    decimal PackagingAndShippingCost)
{
    public PricingMeta ToMeta() => new(VatRate, CardFeeRate, ProfitFloorPercent);
}

/// <summary>
/// Turns a product's inputs plus the live settings into a price, always through the single
/// <see cref="PricingFormula"/>. Nothing here is stored: prices follow the settings.
/// </summary>
public class PricingService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<PricingSettings> LoadSettingsAsync()
    {
        var businessId = tenant.GetBusinessId();

        var settings = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BusinessId == businessId)
            ?? throw new NotFoundException("Settings not found for this business");

        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.BusinessId == businessId)
            .ToDictionaryAsync(m => m.Name);

        var fees = await db.FeeItems.AsNoTracking()
            .Where(f => f.BusinessId == businessId && f.Key != null)
            .ToDictionaryAsync(f => f.Key!);

        if (FeeKeys.Required.Any(k => !fees.ContainsKey(k)))
            throw new BadRequestException("Fee settings are incomplete: card fee, VAT and fixed expenses are required");

        // Packaging, shipping and any other pricing category are charged on every product.
        var categories = await db.PricingAdditionCategories.AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .Select(c => new { c.BasePrice, ItemsTotal = c.Items.Sum(i => (decimal?)i.Price) ?? 0m })
            .ToListAsync();

        return new PricingSettings(
            settings.LaborHourRate,
            settings.ProfitFloorPercent,
            materials,
            fees[FeeKeys.FixedExpenses].Percent / 100,
            fees[FeeKeys.CardFee].Percent / 100,
            fees[FeeKeys.Vat].Percent / 100,
            categories.Sum(c => c.BasePrice + c.ItemsTotal));
    }

    public async Task<PriceBreakdown> CalculateAsync(CalculatePriceDto dto)
    {
        var settings = await LoadSettingsAsync();
        return Calculate(settings, dto.Material, dto.Weight, dto.AdditionalWorkHours, AdditionsCost(dto.Additions));
    }

    public static decimal AdditionsCost(IEnumerable<ProductAdditionDto>? additions) =>
        additions?.Sum(a => a.Price * a.Quantity) ?? 0;

    /// <exception cref="BadRequestException">If the material no longer exists or the margins can't produce a price.</exception>
    public static PriceBreakdown Calculate(
        PricingSettings s, string material, decimal weight, decimal additionalWorkHours, decimal additionsCost)
    {
        if (!s.Materials.TryGetValue(material, out var m))
            throw new BadRequestException($"החומר '{material}' לא קיים יותר בהגדרות");

        // Work time is fixed per piece by material; the product can add extra hours on top.
        var laborHours = m.LaborHours + additionalWorkHours;

        var r = PricingFormula.Calculate(new PricingFormulaInput(
            MetalWeightGrams: (double)weight,
            MetalPricePerGram: (double)m.PricePerGram,
            LaborHours: (double)laborHours,
            LaborHourlyRate: (double)s.LaborHourRate,
            StonesCost: 0,
            AdditionalProductionCost: (double)additionsCost,
            ShippingCost: 0,
            PackagingCost: (double)s.PackagingAndShippingCost,
            FixedExpenseRate: (double)s.FixedExpenseRate,
            ProfitMultiplier: (double)m.ProfitMultiplier,
            CardFeeRate: (double)s.CardFeeRate,
            VatRate: (double)s.VatRate));

        return new PriceBreakdown(
            MetalCost: Money(r.MetalCost),
            LaborHours: Math.Round(laborHours, 4),
            LaborCost: Money(r.LaborCost),
            AdditionsCost: Math.Round(additionsCost, 2),
            PackagingAndShippingCost: Math.Round(s.PackagingAndShippingCost, 2),
            DirectCosts: Money(r.DirectCosts),
            CostWithFixedExpenses: Money(r.CostWithFixedExpenses),
            PriceExclVat: Money(r.PriceExclVat),
            RecommendedPrice: Money(r.FinalPriceInclVat),
            CardFeeCost: Money(r.CardFeeCost),
            Profit: Money(r.PricingProfit),
            ProfitRate: Math.Round((decimal)r.ProfitRate, 4));
    }

    private static decimal Money(double value) => Math.Round((decimal)value, 2);
}
