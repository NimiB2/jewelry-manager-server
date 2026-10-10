using System.Text.Encodings.Web;
using System.Text.Json;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;

namespace JewelryManager.Tools;

/// <summary>
/// Moves the owner's settings (materials and their prices, labor rate, fees, additions, stages,
/// discount presets, expense lists...) from one database to another through a JSON file, using the
/// same service the settings screen uses.
/// </summary>
public static class SettingsTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Hebrew stays readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<UpdateSettingsDto> ExportAsync(AppDbContext db, Guid businessId, string path)
    {
        var settings = await new SettingsService(db, ProductImporter.TenantFor(businessId)).GetSettingsAsync();
        var data = settings.Data;

        var dto = new UpdateSettingsDto(
            data.Materials, data.LaborHourRate, data.PricingAdditions, data.FeesItems, data.ProfitFloorPercent,
            data.PreparationStages, data.ProductAdditionTypes, data.DiscountPresets, data.TestOrderPrefix,
            data.ExpenseTypes, data.ExpenseSuppliers);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dto, Json));
        return dto;
    }

    public static async Task<UpdateSettingsDto> ImportAsync(AppDbContext db, Guid businessId, string path, bool dryRun)
    {
        var dto = JsonSerializer.Deserialize<UpdateSettingsDto>(await File.ReadAllTextAsync(path), Json)
            ?? throw new InvalidOperationException("The settings file is empty.");

        if (!dryRun)
            await new SettingsService(db, ProductImporter.TenantFor(businessId)).UpdateSettingsAsync(dto);

        return dto;
    }

    /// <summary>A short, readable summary for the console (no secrets live in settings).</summary>
    public static IEnumerable<string> Describe(UpdateSettingsDto s)
    {
        foreach (var (name, m) in s.Materials ?? [])
            yield return $"חומר: {name} · {m.PricePerGram} ₪/גרם · {m.LaborHours} שעות · מכפיל {m.ProfitMultiplier}";
        yield return $"שעת עבודה: {s.LaborHourRate} ₪ · רצפת רווח: {s.ProfitFloorPercent}%";
        foreach (var f in s.FeesItems ?? [])
            yield return $"עמלה: {f.Name} {f.Percent}%";
        foreach (var c in s.PricingAdditions ?? [])
            yield return $"אריזה/משלוח: {c.Name} · בסיס {c.BasePrice} · {c.Items.Count} פריטים";
        yield return $"שלבי הכנה: {string.Join(" > ", s.PreparationStages ?? [])}";
        yield return $"סוגי תוספות: {string.Join(", ", (s.ProductAdditionTypes ?? []).Select(t => t.Name))}";
        yield return $"הנחות מהירות: {string.Join(", ", s.DiscountPresets ?? [])}";
        yield return $"סוגי הוצאה: {string.Join(", ", s.ExpenseTypes ?? [])}";
        yield return $"ספקים: {(s.ExpenseSuppliers ?? []).Count}";
    }
}
