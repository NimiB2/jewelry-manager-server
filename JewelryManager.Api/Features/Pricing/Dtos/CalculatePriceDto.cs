using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Features.Products.Dtos;

namespace JewelryManager.Api.Features.Pricing.Dtos;

/// <summary>Live preview request from the calculator screen; nothing is saved.</summary>
public record CalculatePriceDto(
    [Required] string Material,
    [Range(0, 1_000_000)] decimal Weight,
    [Range(0, 1_000_000)] decimal AdditionalWorkHours,
    List<ProductAdditionDto>? Additions);
