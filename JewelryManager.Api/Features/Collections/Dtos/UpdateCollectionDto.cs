using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Collections.Dtos;

/// <summary>Shape of the request body for PATCH /collections/{id}.</summary>
public record UpdateCollectionDto(
    [Required, MinLength(1), MaxLength(100)]
    string Name
);
