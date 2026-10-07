namespace JewelryManager.Api.Features.Settings.Dtos;

/// <summary>The settings tree rebuilt from the relational tables, in the shape the client expects.</summary>
public record SettingsDataResponse(
    Dictionary<string, MaterialSettingsDto> Materials,
    decimal LaborHourRate,
    List<PricingAdditionDto> PricingAdditions,
    List<FeeItemDto> FeesItems,
    decimal ProfitFloorPercent,
    List<string> PreparationStages);

public record SettingsResponse(Guid Id, Guid BusinessId, SettingsDataResponse Data, DateTime UpdatedAt);
