using System.Text.Json;
using System.Text.Json.Serialization;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Settings.Dtos;
using Microsoft.EntityFrameworkCore;
using SettingsEntity = JewelryManager.Api.Data.Entities.Settings;

namespace JewelryManager.Api.Features.Settings;

public class SettingsService(AppDbContext db, CurrentUserAccessor tenant)
{
    // Unsent (null) fields must be dropped, otherwise the merge below would erase them.
    private static readonly JsonSerializerOptions PatchOptions =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<SettingsEntity> GetSettingsAsync() =>
        await db.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.BusinessId == tenant.GetBusinessId())
        ?? throw new NotFoundException("Settings not found for this business");

    public async Task<SettingsEntity> UpdateSettingsAsync(UpdateSettingsDto dto)
    {
        var existing = await GetSettingsAsync();
        var patch = JsonSerializer.Serialize(dto, PatchOptions);
        var businessId = tenant.GetBusinessId();
        var now = DateTime.UtcNow;

        // Merge inside the database (jsonb ||) instead of read-modify-write: each settings
        // section autosaves on its own timer, so overlapping PATCHes must not overwrite
        // each other with stale data.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Settings\" SET \"Data\" = \"Data\" || {patch}::jsonb, \"UpdatedAt\" = {now} WHERE \"Id\" = {existing.Id} AND \"BusinessId\" = {businessId}");

        return await GetSettingsAsync();
    }
}
