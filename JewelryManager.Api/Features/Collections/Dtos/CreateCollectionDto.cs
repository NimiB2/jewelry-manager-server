using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Collections.Dtos;

/// <summary>
/// Shape of the request body for POST /collections.
/// [ApiController] validates this automatically before the action method runs.
/// </summary>
public record CreateCollectionDto(
    [Required, MinLength(1), MaxLength(100)]
    string Name
);
