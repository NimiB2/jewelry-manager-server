using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent.Users;

/// <summary>Body of POST /agent/users: lets a signed-in person (by their account email) talk to the WhatsApp agent.</summary>
public record AddAgentUserDto(
    [Required, MaxLength(30)] string Phone,
    [Required, MaxLength(200)] string Email,
    [MaxLength(20)] string? Gender,
    bool IsDeveloper = false);

public record AgentUserResponse(Guid Id, string Phone, string Email, string Gender, bool IsActive, bool IsDeveloper,
    bool MorningSummary, bool StoreOrderAlerts, bool StuckOrderAlerts);

/// <summary>Who may talk to the agent. Always inside the signed-in owner's own business.</summary>
public partial class AgentUsersService(AppDbContext db, CurrentUserAccessor tenant)
{
    public async Task<List<AgentUserResponse>> ListAsync()
    {
        var businessId = tenant.GetBusinessId();
        var rows = await (from a in db.AgentUsers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                          where a.BusinessId == businessId
                          orderby a.CreatedAt
                          select new { a, u.Email }).ToListAsync();

        return rows.Select(r => ToResponse(r.a, r.Email)).ToList();
    }

    public async Task<AgentUserResponse> AddAsync(AddAgentUserDto dto)
    {
        var businessId = tenant.GetBusinessId();
        var phone = NormalizePhone(dto.Phone);
        var gender = (dto.Gender ?? "unspecified").Trim().ToLowerInvariant();
        if (gender is not ("female" or "male" or "unspecified")) throw new BadRequestException("gender must be female, male or unspecified");

        var email = EmailNormalizer.Normalize(dto.Email);
        var person = await db.Users.FirstOrDefaultAsync(u => u.BusinessId == businessId && u.Email.ToLower() == email)
            ?? throw new NotFoundException("No user with this email in this business");

        if (await db.AgentUsers.AnyAsync(a => a.Phone == phone))
            throw new BadRequestException("This phone number is already registered");

        var agentUser = new AgentUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserId = person.Id,
            Phone = phone,
            Gender = gender,
            IsDeveloper = dto.IsDeveloper,
            CreatedAt = DateTime.UtcNow,
        };
        db.AgentUsers.Add(agentUser);
        await db.SaveChangesAsync();
        return ToResponse(agentUser, person.Email);
    }

    /// <summary>Switches a number off at once: from the next message the agent no longer answers it.</summary>
    public async Task DeactivateAsync(Guid id)
    {
        var businessId = tenant.GetBusinessId();
        var changed = await db.AgentUsers
            .Where(a => a.Id == id && a.BusinessId == businessId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        if (changed == 0) throw new NotFoundException("No such agent user");
    }

    /// <summary>Digits only, with the country code: what WhatsApp sends as the sender.</summary>
    public static string NormalizePhone(string phone)
    {
        var digits = NonDigits().Replace(phone, "");
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.StartsWith('0') || digits.Length is < 10 or > 15) throw new BadRequestException("The phone number must include the country code, e.g. 9725XXXXXXXX");
        return digits;
    }

    private static AgentUserResponse ToResponse(AgentUser a, string email) =>
        new(a.Id, a.Phone, email, a.Gender, a.IsActive, a.IsDeveloper, a.MorningSummary, a.StoreOrderAlerts, a.StuckOrderAlerts);

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();
}

[ApiController]
[Route("agent/users")]
[RequireRole(Role.Owner, Role.SuperAdmin)]
public class AgentUsersController(AgentUsersService service) : ControllerBase
{
    [HttpGet]
    public Task<List<AgentUserResponse>> List() => service.ListAsync();

    [HttpPost]
    public Task<AgentUserResponse> Add(AddAgentUserDto dto) => service.AddAsync(dto);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        await service.DeactivateAsync(id);
        return NoContent();
    }
}
