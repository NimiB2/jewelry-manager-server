using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Features.Pricing;

namespace JewelryManager.Tests;

public class PricingFormulaTests
{
    private static readonly PricingFormulaInput Base = new(
        MetalWeightGrams: 5, MetalPricePerGram: 10, LaborHours: 2, LaborHourlyRate: 50,
        StonesCost: 20, AdditionalProductionCost: 5, ShippingCost: 10, PackagingCost: 15,
        FixedExpenseRate: 0, ProfitMultiplier: 1.5, CardFeeRate: 0.03, VatRate: 0.17);

    [Fact]
    public void ProfitRate_EqualsOneMinusInverseMultiplier_RegardlessOfFeeAndVat()
    {
        var result = PricingFormula.Calculate(Base);

        Assert.Equal(1 - 1 / Base.ProfitMultiplier, result.ProfitRate, 6);
    }

    [Fact]
    public void Calculate_MatchesHandCheckedExample()
    {
        var result = PricingFormula.Calculate(Base with { FixedExpenseRate = 0.17 });

        // directCosts = 50 + 100 + 20 + 5 + 10 + 15 = 200; x1.17 = 234
        Assert.Equal(200, result.DirectCosts, 6);
        Assert.Equal(234, result.CostWithFixedExpenses, 6);
        // denominator = 1/1.5 - 0.03 x 1.17 = 0.6316 -> priceExclVat = 234 / 0.6316 ~ 370.5
        Assert.InRange(result.PriceExclVat, 370, 371);
    }

    [Fact]
    public void Calculate_Throws_WhenFeeLoadSwallowsTheMargin()
    {
        var input = Base with { ProfitMultiplier = 1.01, CardFeeRate = 0.9, VatRate = 0.5 };

        Assert.Throws<BadRequestException>(() => PricingFormula.Calculate(input));
    }

    [Fact]
    public void Calculate_Throws_WhenMultiplierIsNotAboveOne()
    {
        Assert.Throws<BadRequestException>(() =>
            PricingFormula.Calculate(Base with { ProfitMultiplier = 1 }));
    }
}
