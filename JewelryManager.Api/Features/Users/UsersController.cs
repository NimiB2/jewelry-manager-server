using JewelryManager.Api.Auth;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Users.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Users;

[ApiController]
[Route("users")]
public class UsersController(UsersService service) : ControllerBase
{
    [HttpGet]
    public Task<List<UserResponse>> GetUsers() => service.GetUsersAsync();

    [HttpPost]
    [RequireRole(Role.Owner, Role.SuperAdmin)]
    public Task<UserResponse> CreateUser(CreateUserDto dto) => service.CreateUserAsync(dto);
}
