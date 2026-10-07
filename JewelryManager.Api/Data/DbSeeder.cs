using JewelryManager.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Data;

/// <summary>
/// Idempotent development seed: one business, its default settings and the permanent
/// collections. Safe to run on every startup — it only creates what is missing.
/// </summary>
public class DbSeeder(AppDbContext db, IConfiguration config)
{
    private static readonly Guid BusinessId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Name, price per gram, labor hours per gram, profit multiplier.
    // The profit multipliers are placeholders (1.5 silver-based, 1.8 gold) — set the real ones in Settings.
    private static readonly (string Name, decimal Price, decimal Hours, decimal Multiplier)[] Materials =
    [
        ("כסף", 8, 1, 1.5m),
        ("כסף יציקה", 12, 1, 1.5m),
        ("14K זהב", 330, 2, 1.8m),
        ("ציפוי- כסף", 12, 1, 1.5m),
        ("ציפוי- יציקה", 12, 1, 1.5m),
    ];

    private static readonly (string Name, decimal Price)[] PackagingItems =
    [
        ("קופסאת תכשיט", 3.00m),
        ("קופסאת אריזה", 7.17m),
        ("מדבקה קטנה", 1.26m),
        ("מדבקה גדולה", 1.26m),
        ("ברושור", 2.86m),
        ("תעודת אחריות", 1.17m),
        ("נייר משי", 0.41m),
        ("שקיות ניילון/נייר", 1.25m),
    ];

    private static readonly (string Name, decimal Percent)[] Fees =
    [
        ("עמלת סליקה", 3),
        ("מע\"מ", 18),
        ("עמלת עלויות קבועות", 17),
    ];

    private static readonly string[] Stages = ["יציקה", "שיבוץ אבנים", "ליטוש", "ניקוי"];

    public async Task SeedAsync()
    {
        var now = DateTime.UtcNow;

        if (!await db.Businesses.AnyAsync(b => b.Id == BusinessId))
            db.Businesses.Add(new Business { Id = BusinessId, Name = "Meital Bar", CreatedAt = now, UpdatedAt = now });

        // Settings and its lists are seeded together, only when the business has no settings yet,
        // so edits made in the app are never overwritten.
        if (!await db.Settings.AnyAsync(s => s.BusinessId == BusinessId))
        {
            db.Settings.Add(new Settings
            {
                Id = Guid.NewGuid(), BusinessId = BusinessId, LaborHourRate = 100, ProfitFloorPercent = 30,
                CreatedAt = now, UpdatedAt = now,
            });

            db.Materials.AddRange(Materials.Select((m, i) => new Material
            {
                Id = Guid.NewGuid(), BusinessId = BusinessId, Name = m.Name, PricePerGram = m.Price,
                LaborHoursPerGram = m.Hours, ProfitMultiplier = m.Multiplier, SortOrder = i,
            }));

            db.FeeItems.AddRange(Fees.Select((f, i) => new FeeItem
            {
                Id = Guid.NewGuid(), BusinessId = BusinessId, Name = f.Name, Percent = f.Percent,
                IsPermanent = true, SortOrder = i,
            }));

            db.PricingAdditionCategories.AddRange(
                new PricingAdditionCategory
                {
                    Id = Guid.NewGuid(), BusinessId = BusinessId, Name = "אריזה", BasePrice = 0, SortOrder = 0,
                    Items = PackagingItems.Select((p, i) => new PricingAdditionItem
                    {
                        Id = Guid.NewGuid(), BusinessId = BusinessId, Name = p.Name, Price = p.Price, SortOrder = i,
                    }).ToList(),
                },
                new PricingAdditionCategory
                {
                    Id = Guid.NewGuid(), BusinessId = BusinessId, Name = "משלוח", BasePrice = 0, SortOrder = 1,
                });

            db.PreparationStages.AddRange(Stages.Select((name, i) => new PreparationStage
            {
                Id = Guid.NewGuid(), BusinessId = BusinessId, Name = name, SortOrder = i,
            }));
        }

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
