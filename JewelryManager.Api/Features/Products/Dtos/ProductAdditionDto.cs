using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Products.Dtos;

/// <summary>One priced extra on a product. CustomName is required only for "free text" addition types.</summary>
public record ProductAdditionDto(
    [Required] string TypeName,
    string? CustomName,
    [Range(0, 1_000_000)] decimal Price,
    [Range(1, 1_000)] int Quantity = 1);
