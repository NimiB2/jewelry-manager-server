using System.IO.Compression;
using System.Text;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Finances;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Invoices;

/// <summary>Attaching, reading and removing an expense's invoice file, and the ZIP export of a period.</summary>
public class InvoicesService(AppDbContext db, CurrentUserAccessor tenant, IInvoiceStorage storage, FinancesService finances)
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    // The type served back is decided here from the extension, never taken from the upload.
    internal static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
    };

    public async Task AttachAsync(Guid expenseId, string originalName, long length, Stream content)
    {
        var businessId = tenant.GetBusinessId();
        var expense = await FindExpenseAsync(expenseId);

        var extension = Path.GetExtension(originalName);
        if (!ContentTypes.TryGetValue(extension, out var contentType))
            throw new BadRequestException("אפשר לצרף תמונה (JPG, PNG, WEBP, HEIC) או קובץ PDF");
        if (length is <= 0 or > MaxFileBytes)
            throw new BadRequestException("הקובץ ריק או גדול מ-10MB");

        var oldName = expense.InvoiceStoredName;
        var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        await storage.SaveAsync(businessId, storedName, content);

        expense.InvoiceStoredName = storedName;
        expense.InvoiceFileName = SafeDisplayName(originalName);
        expense.InvoiceContentType = contentType;
        expense.HasReceipt = true;
        expense.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Only after the new file is recorded is the old one dropped.
        if (oldName is not null) await storage.DeleteAsync(businessId, oldName);
    }

    public async Task<InvoiceFile> OpenAsync(Guid expenseId)
    {
        var businessId = tenant.GetBusinessId();
        var expense = await FindExpenseAsync(expenseId, asNoTracking: true);
        if (expense.InvoiceStoredName is null) throw new NotFoundException("No invoice file for this expense");

        var stream = await storage.OpenReadAsync(businessId, expense.InvoiceStoredName)
            ?? throw new NotFoundException("The invoice file is missing");

        return new InvoiceFile(stream, expense.InvoiceContentType ?? "application/octet-stream", expense.InvoiceFileName ?? "invoice");
    }

    public async Task RemoveAsync(Guid expenseId)
    {
        var businessId = tenant.GetBusinessId();
        var expense = await FindExpenseAsync(expenseId);
        if (expense.InvoiceStoredName is null) return;

        var stored = expense.InvoiceStoredName;
        expense.InvoiceStoredName = null;
        expense.InvoiceFileName = null;
        expense.InvoiceContentType = null;
        expense.HasReceipt = false;
        expense.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await storage.DeleteAsync(businessId, stored);
    }

    /// <summary>
    /// A ZIP of every invoice file of the period's expenses, plus a list (CSV) of all the expenses with
    /// whether each has an invoice, so the accountant sees what is missing.
    /// </summary>
    public async Task<byte[]> ExportZipAsync(DateOnly? from, DateOnly? to)
    {
        var businessId = tenant.GetBusinessId();

        var query = db.Expenses.AsNoTracking().Where(e => e.BusinessId == businessId && e.InvoiceStoredName != null);
        if (from is { } f) query = query.Where(e => e.Date >= f);
        if (to is { } t) query = query.Where(e => e.Date <= t);
        var withFiles = await query.OrderBy(e => e.Date).ToListAsync();

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var expense in withFiles)
            {
                await using var source = await storage.OpenReadAsync(businessId, expense.InvoiceStoredName!);
                if (source is null) continue;

                var entry = zip.CreateEntry(UniqueName(usedNames, expense), CompressionLevel.NoCompression);
                await using var target = entry.Open();
                await source.CopyToAsync(target);
            }

            var list = zip.CreateEntry("רשימת הוצאות.csv");
            await using var listStream = list.Open();
            var csv = await finances.ExportCsvAsync(from, to, "expense", null);
            await listStream.WriteAsync(csv);
        }

        return output.ToArray();
    }

    // "2026-10-03_description_120.pdf"; a repeat of the same name gets a counter so nothing overwrites.
    private static string UniqueName(HashSet<string> used, Expense expense)
    {
        var extension = Path.GetExtension(expense.InvoiceStoredName!);
        var baseName = $"{expense.Date:yyyy-MM-dd}_{Sanitize(expense.Description)}_{expense.Amount:0.##}";
        var name = baseName + extension;
        for (var i = 2; !used.Add(name); i++) name = $"{baseName} ({i}){extension}";
        return name;
    }

    private static string Sanitize(string value)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length > 60 ? clean[..60] : clean;
    }

    private static string SafeDisplayName(string name) => Sanitize(Path.GetFileName(name));

    private async Task<Expense> FindExpenseAsync(Guid id, bool asNoTracking = false)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Expenses.AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(e => e.Id == id && e.BusinessId == businessId)
            ?? throw new NotFoundException("Expense not found");
    }
}

public record InvoiceFile(Stream Content, string ContentType, string FileName);
