using System.Text.Json;

namespace JewelryManager.Api.Data.Entities;

public class Settings
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Business Business { get; set; } = null!;

    // The whole pricing-settings tree lives in one jsonb column so new settings
    // never need a migration.
    public JsonDocument Data { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
