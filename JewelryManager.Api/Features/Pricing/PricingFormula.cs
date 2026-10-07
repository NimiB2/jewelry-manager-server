using JewelryManager.Api.Common.Exceptions;

namespace JewelryManager.Api.Features.Pricing;

public record PricingFormulaInput(
    double MetalWeightGrams,
    double MetalPricePerGram,
    double LaborHours,
    double LaborHourlyRate,
    double StonesCost,
    double AdditionalProductionCost,
    double ShippingCost,
    double PackagingCost,
    // Fraction added on top of direct costs, e.g. 0.17 for 17%.
    double FixedExpenseRate,
    // Markup multiplier (e.g. 1.5), not a percentage. The resulting profit rate is
    // 1 - 1/ProfitMultiplier, independent of card fee and VAT (the formula grosses up).
    double ProfitMultiplier,
    // Card fee, charged on the final VAT-inclusive price.
    double CardFeeRate,
    double VatRate);

public record PricingFormulaResult(
    double MetalCost,
    double LaborCost,
    double DirectCosts,
    double CostWithFixedExpenses,
    double PriceExclVat,
    double FinalPriceInclVat,
    double CardFeeCost,
    double PricingProfit,
    double ProfitRate);

/// <summary>
/// The one place a sale price is derived from cost. Its structure is fixed (core financial
/// logic); only the numbers it receives come from settings.
/// </summary>
public static class PricingFormula
{
    public static PricingFormulaResult Calculate(PricingFormulaInput i)
    {
        if (i.ProfitMultiplier <= 1)
            throw new BadRequestException("מקדם רווח חייב להיות גדול מ-1");

        // Zero or negative means the fee/VAT load swallows the whole margin: the price
        // would be infinite or negative.
        var denominator = 1 / i.ProfitMultiplier - i.CardFeeRate * (1 + i.VatRate);
        if (denominator <= 0)
            throw new BadRequestException("מקדם הרווח נמוך מדי יחסית לעמלת הסליקה והמע\"מ — לא ניתן לחשב מחיר");

        var metalCost = i.MetalWeightGrams * i.MetalPricePerGram;
        var laborCost = i.LaborHours * i.LaborHourlyRate;
        var directCosts = metalCost + laborCost + i.StonesCost
            + i.AdditionalProductionCost + i.ShippingCost + i.PackagingCost;
        var costWithFixed = directCosts * (1 + i.FixedExpenseRate);

        var priceExclVat = costWithFixed / denominator;
        var finalPrice = priceExclVat * (1 + i.VatRate);
        var cardFeeCost = finalPrice * i.CardFeeRate;
        var profit = priceExclVat - costWithFixed - cardFeeCost;

        return new PricingFormulaResult(metalCost, laborCost, directCosts, costWithFixed,
            priceExclVat, finalPrice, cardFeeCost, profit, profit / priceExclVat);
    }
}
