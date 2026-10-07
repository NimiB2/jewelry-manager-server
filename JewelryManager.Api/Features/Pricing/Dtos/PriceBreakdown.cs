namespace JewelryManager.Api.Features.Pricing.Dtos;

/// <summary>Everything the UI needs to show how a recommended price was derived.</summary>
public record PriceBreakdown(
    decimal MetalCost,
    decimal LaborHours,
    decimal LaborCost,
    decimal AdditionsCost,
    decimal PackagingAndShippingCost,
    decimal DirectCosts,
    decimal CostWithFixedExpenses,
    decimal PriceExclVat,
    decimal RecommendedPrice,
    decimal CardFeeCost,
    decimal Profit,
    decimal ProfitRate,
    // The inputs behind the numbers, so the UI can show each formula with its real values.
    decimal Weight,
    decimal PricePerGram,
    decimal LaborHourRate,
    decimal FixedExpenseRate,
    decimal ProfitMultiplier,
    decimal CardFeeRate,
    decimal VatRate);

/// <summary>The few settings the client needs to simulate discounts against the profit floor.</summary>
public record PricingMeta(decimal VatRate, decimal CardFeeRate, decimal ProfitFloorPercent);
