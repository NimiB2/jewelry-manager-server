using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Collections.Dtos;

/// <summary>Shape of the request body for PATCH /collections/{id}/work-hours. Null removes the fixed hours.</summary>
public record UpdateCollectionWorkHoursDto(
    [Range(0.01, 1000)]
    decimal? FixedWorkHours
);
