using System.Security.Cryptography;
using System.Text;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.RiseUp;
using JewelryManager.Api.Features.RiseUp.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Statements;

// RiseUp is a second source of checklist lines, next to uploaded files. The same rules apply: a line is
// never an expense by itself, and a transaction that was already read is never added twice.
public partial class StatementsService
{
    public async Task<RiseUpSyncResponse> ImportRiseUpAsync(string month, List<RiseUpTransaction> transactions)
    {
        var businessId = tenant.GetBusinessId();
        var (from, to) = MonthRange(month);
        var now = DateTime.UtcNow;

        var files = new Dictionary<StatementSource, StatementFile>();
        var candidates = new List<StatementLine>();
        foreach (var t in transactions.OrderBy(t => t.Date))
        {
            var source = SourceOf(t);
            if (!files.TryGetValue(source, out var file))
            {
                file = await RiseUpFileAsync(businessId, month, source, from, to);
                files[source] = file;
            }

            // Income and transactions RiseUp excludes from the cashflow are not expenses.
            candidates.Add(new StatementLine
            {
                Id = Guid.NewGuid(), BusinessId = businessId, FileId = file.Id, Source = source,
                Date = t.Date, Amount = t.IsIncome ? -t.Amount : t.Amount,
                Description = Truncate(t.BusinessName, 300),
                Reference = string.IsNullOrWhiteSpace(t.AccountNickname) ? null : Truncate(t.AccountNickname, 60),
                Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"riseup|{t.TransactionId}"))),
                Status = t.IsIncome || t.IsExcluded ? StatementLineStatus.Ignored : StatementLineStatus.Pending,
                CreatedAt = now,
            });
        }

        // The file total is the money out of the whole month, so a bank settlement can be covered by it.
        foreach (var (source, file) in files)
            file.TotalOut = transactions.Where(t => SourceOf(t) == source && !t.IsIncome && !t.IsExcluded).Sum(t => t.Amount);

        var known = await KnownFingerprintsAsync(businessId, candidates.Select(l => l.Fingerprint).ToList());
        var fresh = candidates.Where(l => !known.Contains(l.Fingerprint)).ToList();

        foreach (var day in fresh.Select(l => l.Date).Distinct()) await EnsureMonthOpenAsync(day);
        db.StatementLines.AddRange(fresh);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new BadRequestException("הסנכרון לא הסתיים, אפשר לנסות שוב");
        }

        await LinkSettlementsAsync(businessId);
        var pendingBefore = fresh.Count(l => l.Status == StatementLineStatus.Pending);
        var autoMatched = await AutoMatchAsync(businessId, fresh.Where(l => l.Status == StatementLineStatus.Pending).Select(l => l.Id).ToList());

        return new RiseUpSyncResponse(
            fresh.Count,
            candidates.Count - fresh.Count,
            fresh.Count(l => l.Status == StatementLineStatus.Ignored),
            pendingBefore - autoMatched,
            autoMatched);
    }

    private static StatementSource SourceOf(RiseUpTransaction t) =>
        t.SourceType == "creditCard" ? StatementSource.Card : StatementSource.Bank;

    // One file per month and source, so a sync again finds the same file instead of making a new one.
    private async Task<StatementFile> RiseUpFileAsync(Guid businessId, string month, StatementSource source, DateOnly from, DateOnly to)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"riseup|{month}|{source}")));
        var file = await db.StatementFiles.FirstOrDefaultAsync(f => f.BusinessId == businessId && f.FileHash == hash);
        if (file is not null) return file;

        file = new StatementFile
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Source = source,
            FileName = $"RiseUp {month} ({(source == StatementSource.Card ? "אשראי" : "בנק")})",
            FileHash = hash, PeriodFrom = from, PeriodTo = to, UploadedAt = DateTime.UtcNow,
        };
        db.StatementFiles.Add(file);
        return file;
    }
}
