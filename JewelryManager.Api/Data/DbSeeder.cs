using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Data;

/// <summary>
/// Idempotent development seed: one business, its default settings and the permanent
/// collections. Safe to run on every startup.
/// </summary>
public class DbSeeder(AppDbContext db, IConfiguration config)
{
    private static readonly Guid BusinessId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string DefaultSettingsJson = """
        {
          "materials": {
            "silver": { "pricePerGram": 2.5, "laborHoursPerGram": 0.4, "profitMultiplier": 1.5 },
            "gold": { "pricePerGram": 12, "laborHoursPerGram": 0.5, "profitMultiplier": 1.8 }
          },
          "laborHourRate": 100,
          "pricingAdditions": [
            { "name": "אריזה", "basePrice": 0, "items": [
              { "name": "קופסת מתנה סטנדרטית", "price": 8 },
              { "name": "שקית ממותגת", "price": 3 } ] },
            { "name": "משלוח", "basePrice": 0, "items": [] }
          ],
          "feesItems": [
            { "name": "עמלת סליקה", "percent": 3, "isPermanent": true },
            { "name": "מע\"מ", "percent": 18, "isPermanent": true },
            { "name": "עמלת עלויות קבועות", "percent": 17, "isPermanent": true }
          ],
          "profitFloorPercent": 30,
          "preparationStages": ["יציקה", "שיבוץ אבנים", "ליטוש", "ניקוי"]
        }
        """;

    public async Task SeedAsync()
    {
        var now = DateTime.UtcNow;

        if (!await db.Businesses.AnyAsync(b => b.Id == BusinessId))
            db.Businesses.Add(new Business { Id = BusinessId, Name = "Meital Bar", CreatedAt = now, UpdatedAt = now });

        if (!await db.Settings.AnyAsync(s => s.BusinessId == BusinessId))
            db.Settings.Add(new Settings
            {
                Id = Guid.NewGuid(),
                BusinessId = BusinessId,
                Data = JsonDocument.Parse(DefaultSettingsJson),
                CreatedAt = now,
                UpdatedAt = now,
            });

        // Permanent collections are found by Key, never by their renamable display name.
        foreach (var (key, name) in new[] { ("general", "כללי"), ("customOrder", "הזמנה אישית") })
        {
            if (!await db.Collections.AnyAsync(c => c.BusinessId == BusinessId && c.Key == key))
                db.Collections.Add(new Collection
                {
                    Id = Guid.NewGuid(), BusinessId = BusinessId, Name = name, Key = key,
                    IsPermanent = true, CreatedAt = now, UpdatedAt = now,
                });
        }

        // Without any user nobody can sign in. The owner is pre-created by email and linked
        // to their Firebase uid on first sign-in (see FirebaseAuthMiddleware).
        var ownerEmail = config["Seed:OwnerEmail"];
        if (!string.IsNullOrWhiteSpace(ownerEmail) && !await db.Users.AnyAsync(u => u.Email == ownerEmail))
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), BusinessId = BusinessId, Email = ownerEmail,
                Role = Role.Owner, CreatedAt = now, UpdatedAt = now,
            });

        await db.SaveChangesAsync();
    }
}
