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
    decimal ProfitRate);

/// <summary>The few settings the client needs to simulate discounts against the profit floor.</summary>
public record PricingMeta(decimal VatRate, decimal CardFeeRate, decimal ProfitFloorPercent);
