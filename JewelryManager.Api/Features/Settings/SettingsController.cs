using JewelryManager.Api.Features.Settings.Dtos;
using Microsoft.AspNetCore.Mvc;
using SettingsEntity = JewelryManager.Api.Data.Entities.Settings;

namespace JewelryManager.Api.Features.Settings;

[ApiController]
[Route("settings")]
public class SettingsController(SettingsService service) : ControllerBase
{
    [HttpGet]
    public Task<SettingsEntity> GetSettings() => service.GetSettingsAsync();

    [HttpPatch]
    public Task<SettingsEntity> UpdateSettings(UpdateSettingsDto dto) =>
        service.UpdateSettingsAsync(dto);
}
