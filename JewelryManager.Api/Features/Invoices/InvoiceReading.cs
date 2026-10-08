using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Invoices;

/// <summary>What was read off an invoice. Every field is optional: whatever could not be read stays empty.</summary>
public record InvoiceSuggestion(decimal? Amount, DateOnly? Date, string? Supplier, string? Description, string? TypeName);

public record InvoiceReadResponse(bool Available, InvoiceSuggestion? Suggestion);

/// <summary>
/// Reads the details of an invoice (amount, date, supplier...) from its image or PDF. The AI that does
/// this is one implementation of this interface; until one is registered the form is simply filled by hand.
/// </summary>
public interface IInvoiceReader
{
    bool IsAvailable { get; }

    /// <param name="expenseTypes">The categories the owner defined, so the reader can pick one of them.</param>
    Task<InvoiceSuggestion?> ReadAsync(Stream content, string fileName, string contentType, IReadOnlyList<string> expenseTypes);
}

/// <summary>The default: no reader is connected, so nothing is filled in automatically.</summary>
public class NullInvoiceReader : IInvoiceReader
{
    public bool IsAvailable => false;

    public Task<InvoiceSuggestion?> ReadAsync(Stream content, string fileName, string contentType, IReadOnlyList<string> expenseTypes) =>
        Task.FromResult<InvoiceSuggestion?>(null);
}

/// <summary>Runs the reader on an uploaded invoice and cleans what comes back before it reaches the form.</summary>
public class InvoiceReadingService(AppDbContext db, CurrentUserAccessor tenant, IInvoiceReader reader)
{
    public bool IsAvailable => reader.IsAvailable;

    public async Task<InvoiceReadResponse> ReadAsync(string fileName, long length, Stream content)
    {
        if (!reader.IsAvailable) return new InvoiceReadResponse(false, null);

        if (!InvoicesService.ContentTypes.TryGetValue(Path.GetExtension(fileName), out var contentType))
            throw new BadRequestException("אפשר לצרף תמונה (JPG, PNG, WEBP, HEIC) או קובץ PDF");
        if (length is <= 0 or > InvoicesService.MaxFileBytes)
            throw new BadRequestException("הקובץ ריק או גדול מ-10MB");

        var businessId = tenant.GetBusinessId();
        var types = await db.ExpenseTypes.AsNoTracking()
            .Where(t => t.BusinessId == businessId)
            .OrderBy(t => t.SortOrder)
            .Select(t => t.Name)
            .ToListAsync();

        var raw = await reader.ReadAsync(content, fileName, contentType, types);
        if (raw is null) return new InvoiceReadResponse(true, null);

        // The reader is only a suggestion: nonsense values are dropped, and the category must be one of hers.
        var suggestion = new InvoiceSuggestion(
            raw.Amount is > 0 and < 100_000_000 ? Math.Round(raw.Amount.Value, 2) : null,
            raw.Date,
            Clean(raw.Supplier, 100),
            Clean(raw.Description, 300),
            types.FirstOrDefault(t => string.Equals(t, raw.TypeName?.Trim(), StringComparison.OrdinalIgnoreCase)));

        return new InvoiceReadResponse(true, suggestion);
    }

    private static string? Clean(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > max ? text[..max] : text;
    }
}
