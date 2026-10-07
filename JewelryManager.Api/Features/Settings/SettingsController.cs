using JewelryManager.Api.Features.Settings.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Settings;

[ApiController]
[Route("settings")]
public class SettingsController(SettingsService service) : ControllerBase
{
    [HttpGet]
    public Task<SettingsResponse> GetSettings() => service.GetSettingsAsync();

    [HttpPatch]
    public Task<SettingsResponse> UpdateSettings(UpdateSettingsDto dto) =>
        service.UpdateSettingsAsync(dto);
}
