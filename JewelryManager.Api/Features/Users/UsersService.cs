using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Users.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Users;

public class UsersService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<List<UserResponse>> GetUsersAsync() =>
        await db.Users
            .Where(u => u.BusinessId == tenant.GetBusinessId())
            .OrderBy(u => u.CreatedAt)
            .Select(u => new UserResponse(u.Id, u.Name, u.Email, u.Phone, u.Role, u.CreatedAt))
            .ToListAsync();

    // Pre-creates a placeholder by email with no FirebaseUid; FirebaseAuthMiddleware links it
    // on the person's first Google sign-in, so no invitation email is needed.
    public async Task<UserResponse> CreateUserAsync(CreateUserDto dto)
    {
        var role = dto.Role ?? Role.Employee;
        if (role == Role.SuperAdmin)
            throw new BadRequestException("Role must be Owner or Employee");

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            BusinessId = tenant.GetBusinessId(),
            Email = dto.Email,
            Name = dto.Name,
            Phone = dto.Phone,
            Role = role,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new UserResponse(user.Id, user.Name, user.Email, user.Phone, user.Role, user.CreatedAt);
    }
}
