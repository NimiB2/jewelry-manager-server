using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Settings.Dtos;

public record MaterialSettingsDto(double PricePerGram, double LaborHoursPerGram, double ProfitMultiplier);

public record PricingItemDto([Required] string Name, double Price);

public record PricingAdditionDto([Required] string Name, double BasePrice, List<PricingItemDto> Items);

// IsPermanent hides the delete button client-side so the fee rates the pricing
// formula relies on can't be removed by accident.
public record FeeItemDto([Required] string Name, double Percent, bool? IsPermanent);

/// <summary>
/// Every field is optional: PATCH /settings only touches the keys that were sent
/// (null = not sent).
/// </summary>
public record UpdateSettingsDto(
    Dictionary<string, MaterialSettingsDto>? Materials,
    double? LaborHourRate,
    List<PricingAdditionDto>? PricingAdditions,
    List<FeeItemDto>? FeesItems,
    double? ProfitFloorPercent,
    List<string>? PreparationStages
);
