using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Settings.Dtos;

// Shared by requests and responses: the API keeps the same shape the client has always used.

public record MaterialSettingsDto(
    [Range(0, 1_000_000)] decimal PricePerGram,
    [Range(0, 1_000_000)] decimal LaborHours,
    [Range(0, 1_000_000)] decimal ProfitMultiplier);

public record PricingItemDto([Required] string Name, [Range(0, 1_000_000)] decimal Price);

public record PricingAdditionDto(
    [Required] string Name,
    [Range(0, 1_000_000)] decimal BasePrice,
    [Required] List<PricingItemDto> Items);

// IsPermanent hides the delete button client-side so the fee rates the pricing
// formula relies on can't be removed by accident. Key marks the fees the formula reads
// (see FeeKeys); the client must send it back unchanged.
public record FeeItemDto(
    [Required] string Name,
    [Range(0, 1_000_000)] decimal Percent,
    bool? IsPermanent,
    string? Key = null);

// An option the owner can add to a product (stone, setting, plating, "other"...).
public record ProductAdditionTypeDto([Required] string Name, bool AllowsCustomName);

/// <summary>
/// Every field is optional: PATCH /settings replaces only the sections that were sent
/// (null = not sent). A sent list or dictionary replaces that whole section.
/// </summary>
public record UpdateSettingsDto(
    Dictionary<string, MaterialSettingsDto>? Materials,
    [Range(0, 1_000_000)] decimal? LaborHourRate,
    List<PricingAdditionDto>? PricingAdditions,
    List<FeeItemDto>? FeesItems,
    [Range(0, 1_000_000)] decimal? ProfitFloorPercent,
    List<string>? PreparationStages,
    List<ProductAdditionTypeDto>? ProductAdditionTypes = null,
    // Quick-pick discount percentages, each between 0.01 and 100.
    List<decimal>? DiscountPresets = null,
    // A customer name starting with this marks a test order; empty turns the feature off.
    [MaxLength(50)] string? TestOrderPrefix = null,
    // The expense types she picks from when recording an expense.
    List<string>? ExpenseTypes = null,
    // The suppliers she picks from when recording an expense.
    List<string>? ExpenseSuppliers = null
);
