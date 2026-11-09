using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Analytics;

/// <summary>Orders and revenue for one day, week (its Monday) or month (its first day).</summary>
public record SalesRow(string Period, int Orders, decimal Revenue);

public record GrowthSnapshot(
    int OrdersLast30Days,
    decimal OrdersPrior30DayAverage,
    decimal AverageOrderValue90Days,
    decimal RepeatCustomerRate,
    int Products,
    int ProductsNeedingDetails,
    int? DaysSinceLastProductAdded,
    decimal StoreShare90Days,
    string? TopMaterial,
    decimal TopMaterialShare);

public record SellerRow(string Name, int Quantity, decimal Revenue);

public record SlowProductRow(string Name, string Type, int DaysInCatalog);

public record SellersResponse(int Months, List<SellerRow> Best, List<SlowProductRow> Slow);

public record AnniversaryRow(int OrderNumber, string Customer, DateOnly Date, string? Item);

public record MaterialPriceRow(string Name, decimal PricePerGram);

public record MaterialChange([Required, MaxLength(100)] string Material, decimal? NewPricePerGram, decimal? Percent);

/// <summary>Body of POST /analytics/what-if: what the recommended prices would be if these material prices changed.</summary>
public record WhatIfRequest([Required, MinLength(1), MaxLength(10)] List<MaterialChange> Changes);

public record WhatIfTypeRow(string Type, int Products, decimal AverageChangePercent);

public record WhatIfResponse(
    int Products,
    decimal AverageChangePercent,
    List<WhatIfTypeRow> ByType,
    int NewlyBelowProfitFloor,
    List<string> NewlyBelowExamples);

public record DiscountCeilingRow(string Type, int Products, decimal SafeForAllPercent, decimal MedianPercent);

public record DiscountCeilingResponse(decimal ProfitFloorPercent, List<DiscountCeilingRow> ByType, List<string> AlreadyBelowFloor);
