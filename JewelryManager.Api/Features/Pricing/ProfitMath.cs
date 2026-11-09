namespace JewelryManager.Api.Features.Pricing;

/// <summary>
/// Profit at a given selling price, with the same structure as the pricing formula and the client's discount
/// simulator: VAT is carved out of the final price, the card fee is charged on it, and the cost already
/// includes the fixed-expense factor.
/// </summary>
public static class ProfitMath
{
    /// <summary>Profit divided by the price without VAT, for a selling price that includes VAT.</summary>
    public static decimal RateAt(decimal finalPrice, decimal costWithFixed, decimal vatRate, decimal cardFeeRate)
    {
        var excl = finalPrice / (1 + vatRate);
        if (excl <= 0) return 0;
        return (excl - costWithFixed - finalPrice * cardFeeRate) / excl;
    }

    /// <summary>The lowest selling price (VAT included) that still keeps the profit rate at the floor. Null if no price can.</summary>
    public static decimal? MinPriceForFloor(decimal costWithFixed, decimal vatRate, decimal cardFeeRate, decimal floorPercent)
    {
        var floor = floorPercent / 100;
        var denominator = (1 - floor) / (1 + vatRate) - cardFeeRate;
        return denominator <= 0 ? null : costWithFixed / denominator;
    }

    /// <summary>The largest discount (percent, one decimal, rounded down) that keeps the floor. Zero when the price is already at it.</summary>
    public static decimal MaxDiscountPercent(decimal sitePrice, decimal costWithFixed, decimal vatRate, decimal cardFeeRate, decimal floorPercent)
    {
        if (sitePrice <= 0 || MinPriceForFloor(costWithFixed, vatRate, cardFeeRate, floorPercent) is not { } minimum) return 0;
        var percent = (1 - minimum / sitePrice) * 100;
        return percent <= 0 ? 0 : Math.Floor(percent * 10) / 10;
    }
}
