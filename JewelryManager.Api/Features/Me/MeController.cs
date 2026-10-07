using JewelryManager.Api.Auth;
using JewelryManager.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Me;

public record MeResponse(Guid Id, Guid? BusinessId, string Email, string? Name, string? Phone, Role Role);

[ApiController]
[Route("me")]
public class MeController(CurrentUserAccessor tenant) : ControllerBase
{
    [HttpGet]
    public MeResponse GetMe()
    {
        var u = tenant.GetUser();
        return new MeResponse(u.Id, u.BusinessId, u.Email, u.Name, u.Phone, u.Role);
    }
}
