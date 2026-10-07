using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Users.Dtos;

/// <summary>Public shape of a user — deliberately omits FirebaseUid.</summary>
public record UserResponse(Guid Id, string? Name, string Email, string? Phone, Role Role, DateTime CreatedAt);
