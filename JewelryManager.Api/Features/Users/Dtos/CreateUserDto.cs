using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Users.Dtos;

/// <summary>Role is optional (defaults to Employee); SuperAdmin is rejected by the service.</summary>
public record CreateUserDto(
    [Required, EmailAddress] string Email,
    string? Name,
    string? Phone,
    Role? Role
);
