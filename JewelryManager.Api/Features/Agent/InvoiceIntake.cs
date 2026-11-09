using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Api.Features.Invoices;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// A photo or PDF of an invoice: the existing invoice reader extracts the details, and the result comes back
/// as a proposal to save as an expense. The model never sees the picture; only the person decides.
/// </summary>
public class InvoiceIntake(
    IWhatsAppClient whatsapp,
    InvoiceReadingService reading,
    InvoicesService invoices,
    PendingActionService pending,
    PendingMediaCache media,
    IOptions<AgentOptions> options,
    ILogger<InvoiceIntake> log) : IInvoiceIntake
{
    public async Task<AgentReply> ReadAsync(AgentUser user, IncomingMessage message, CancellationToken ct)
    {
        if (!reading.IsAvailable) return new AgentReply("קריאת חשבוניות אוטומטית לא מחוברת כרגע. אפשר לכתוב לי את הפרטים.");

        var extension = ExtensionFor(message.MimeType);
        if (extension is null || message.MediaId is null)
            return new AgentReply("אני קוראת חשבוניות כתמונה (JPG, PNG, WEBP) או כקובץ PDF.");

        var limits = options.Value.Limits;
        var file = await whatsapp.DownloadMediaAsync(message.MediaId, limits.MaxMediaBytes, ct);
        if (file is null) return new AgentReply("הקובץ גדול מדי. אפשר עד 10MB.");

        var fileName = "invoice" + extension;
        InvoiceSuggestion? suggestion;
        using (var stream = new MemoryStream(file.Content))
            suggestion = (await reading.ReadAsync(fileName, file.Content.Length, stream)).Suggestion;

        if (suggestion?.Amount is null)
            return new AgentReply("לא הצלחתי לקרוא את החשבונית. אפשר לשלוח תמונה ברורה יותר, או לכתוב לי את הפרטים.");

        var date = suggestion.Date ?? AgentClock.LocalToday();
        var summary = $"חשבונית{(suggestion.Supplier is null ? "" : " מ" + suggestion.Supplier)}: {Money.Format(suggestion.Amount.Value)}, {date:dd/MM}. לשמור כהוצאה?";

        var args = JsonSerializer.Serialize(new
        {
            description = suggestion.Description ?? suggestion.Supplier ?? "חשבונית",
            amount = suggestion.Amount,
            supplier = suggestion.Supplier,
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            typeName = suggestion.TypeName,
            category = "VARIABLE",
        });

        var action = await pending.CreateAsync(user, "add_expense", args, summary, ct);
        media.Put(action.Id, file.Content, fileName, TimeSpan.FromMinutes(limits.PendingMinutes));
        return new AgentReply(summary, action.Id);
    }

    /// <summary>After the expense was saved, files the invoice picture under it. A failure here never undoes the expense.</summary>
    public async Task<bool> AttachAsync(Guid pendingId, Guid expenseId)
    {
        var held = media.Take(pendingId);
        if (held is null) return false;

        try
        {
            using var stream = new MemoryStream(held.Value.Content);
            await invoices.AttachAsync(expenseId, held.Value.FileName, held.Value.Content.Length, stream);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not attach the invoice file to expense {Expense}", expenseId);
            return false;
        }
    }

    private static string? ExtensionFor(string? mime) => mime?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        "application/pdf" => ".pdf",
        _ => null,
    };
}

/// <summary>Amounts the way a person reads them: 1,250 ₪ or 340.50 ₪.</summary>
public static class Money
{
    public static string Format(decimal amount) =>
        (amount == decimal.Truncate(amount)
            ? amount.ToString("N0", CultureInfo.InvariantCulture)
            : amount.ToString("N2", CultureInfo.InvariantCulture)) + " ₪";
}

/// <summary>What the processor needs from the invoice flow; a fake stands in for it in tests.</summary>
public interface IInvoiceIntake
{
    Task<AgentReply> ReadAsync(AgentUser user, IncomingMessage message, CancellationToken ct);

    Task<bool> AttachAsync(Guid pendingId, Guid expenseId);
}
