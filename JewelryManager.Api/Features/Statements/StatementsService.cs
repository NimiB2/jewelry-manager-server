using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Statements.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Statements;

/// <summary>
/// Statement lines are a checklist: every outgoing line must end as matched to an expense she already
/// entered, as a new expense, or as ignored. Nothing becomes an expense on its own.
/// </summary>
public partial class StatementsService(
    AppDbContext db, CurrentUserAccessor tenant, IStatementFileParser parser, ExpensesService expenses)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly string[] DefaultKeywords =
        ["ויזה", "מקס", "כאל", "ישראכרט", "אמריקן אקספרס", "דיינרס", "לאומי קארד", "visa", "max", "cal", "isracard"];

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<UploadResultResponse> UploadAsync(StatementSource source, string fileName, long length, Stream content)
    {
        if (length <= 0) throw new BadRequestException("הקובץ ריק");
        if (length > MaxFileBytes) throw new BadRequestException("הקובץ גדול מדי (עד 5MB)");

        var businessId = tenant.GetBusinessId();

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        buffer.Position = 0;
        var parsed = parser.Parse(fileName, buffer, source);
        var keywords = await GetKeywordsAsync();

        // The same file again is read again: rows already known are skipped, so only rows she cleared come back.
        var file = await db.StatementFiles.FirstOrDefaultAsync(f => f.BusinessId == businessId && f.FileHash == hash);
        var reuploaded = file is not null;
        if (file is null)
        {
            file = new StatementFile
            {
                Id = Guid.NewGuid(), BusinessId = businessId, Source = source, FileName = Truncate(Path.GetFileName(fileName), 260),
                FileHash = hash, PeriodFrom = parsed.Min(l => l.Date), PeriodTo = parsed.Max(l => l.Date),
                TotalOut = parsed.Sum(l => l.Amount), UploadedAt = DateTime.UtcNow,
            };
            db.StatementFiles.Add(file);
        }

        var candidates = BuildLines(file, parsed, keywords);

        // A row already read from an overlapping statement is skipped, not added twice.
        var known = await KnownFingerprintsAsync(businessId, candidates.Select(l => l.Fingerprint).ToList());
        var fresh = candidates.Where(l => !known.Contains(l.Fingerprint)).ToList();

        foreach (var month in fresh.Select(l => l.Date).Distinct()) await EnsureMonthOpenAsync(month);
        db.StatementLines.AddRange(fresh);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new BadRequestException("ההעלאה לא הסתיימה, אפשר לנסות שוב");
        }

        await LinkSettlementsAsync(businessId);
        var pendingBefore = fresh.Count(l => l.Status == StatementLineStatus.Pending);
        var autoMatched = await AutoMatchAsync(businessId, fresh.Where(l => l.Status == StatementLineStatus.Pending).Select(l => l.Id).ToList());

        return new UploadResultResponse(
            file.Id, reuploaded, fresh.Count, candidates.Count - fresh.Count,
            fresh.Count(l => l.Status == StatementLineStatus.CardSettlement),
            fresh.Count(l => l.Status == StatementLineStatus.Ignored),
            pendingBefore - autoMatched,
            autoMatched);
    }

    // ── Automatic matching ────────────────────────────────────────────────────

    /// <summary>Matches the month's undecided lines to expenses that are clearly the same. Returns how many were matched.</summary>
    public async Task<int> AutoMatchMonthAsync(string month)
    {
        var businessId = tenant.GetBusinessId();
        var (from, to) = MonthRange(month);
        await EnsureMonthOpenAsync(from);

        var ids = await db.StatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.Status == StatementLineStatus.Pending && l.Date >= from && l.Date <= to)
            .Select(l => l.Id)
            .ToListAsync();
        return await AutoMatchAsync(businessId, ids);
    }

    // Only a sure match is made by itself: same amount, the same name, and exactly one such expense. A line
    // with no clear partner stays pending for her to decide.
    private async Task<int> AutoMatchAsync(Guid businessId, List<Guid> lineIds)
    {
        if (lineIds.Count == 0) return 0;

        var lines = await db.StatementLines
            .Where(l => l.BusinessId == businessId && lineIds.Contains(l.Id) && l.Status == StatementLineStatus.Pending && l.Amount > 0)
            .OrderBy(l => l.Date)
            .ToListAsync();
        if (lines.Count == 0) return 0;

        var closed = (await db.StatementMonthCloses.AsNoTracking().Where(m => m.BusinessId == businessId).Select(m => m.Month).ToListAsync()).ToHashSet();
        var pool = await UnlinkedExpensesAsync(
            businessId, lines.Min(l => l.Date).AddDays(-StatementMatching.NamedWindowDays), lines.Max(l => l.Date).AddDays(StatementMatching.NamedWindowDays));

        var taken = new HashSet<Guid>();
        var matched = 0;
        foreach (var line in lines)
        {
            if (closed.Contains($"{line.Date.Year:0000}-{line.Date.Month:00}")) continue;

            var lineKey = StatementMatching.KeyOf(line.Description);
            var sure = pool
                .Where(e => !taken.Contains(e.Id) && StatementMatching.IsCandidate(line, e, lineKey) && StatementMatching.NameMatches(lineKey, e))
                .ToList();
            if (sure.Count != 1) continue;

            line.Status = StatementLineStatus.Matched;
            line.ExpenseId = sure[0].Id;
            taken.Add(sure[0].Id);
            matched++;
        }

        if (matched == 0) return 0;

        try
        {
            await db.SaveChangesAsync();
            return matched;
        }
        catch (DbUpdateException)
        {
            // An expense got linked elsewhere in the meantime. Nothing is lost: the lines stay pending.
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    private static List<StatementLine> BuildLines(StatementFile file, List<ParsedStatementLine> parsed, List<string> keywords)
    {
        var now = DateTime.UtcNow;
        var seen = new Dictionary<string, int>();
        var lines = new List<StatementLine>();

        foreach (var p in parsed)
        {
            // Two identical rows in one statement (two coffees on one day) are both real: the count tells them apart.
            var basis = $"{file.Source}|{p.Date:yyyy-MM-dd}|{p.Amount}|{p.Description.Trim().ToLowerInvariant()}|{p.Reference}";
            var occurrence = seen.GetValueOrDefault(basis) + 1;
            seen[basis] = occurrence;

            lines.Add(new StatementLine
            {
                Id = Guid.NewGuid(), BusinessId = file.BusinessId, FileId = file.Id, Source = file.Source,
                Date = p.Date, Amount = p.Amount, Description = p.Description, Reference = p.Reference,
                Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{basis}|{occurrence}"))),
                Status = InitialStatus(file.Source, p, keywords), CreatedAt = now,
            });
        }

        return lines;
    }

    // Money coming in is not an expense, and the bank's lump-sum card charge is covered by the card statement.
    private static StatementLineStatus InitialStatus(StatementSource source, ParsedStatementLine line, List<string> keywords)
    {
        if (line.Amount < 0) return StatementLineStatus.Ignored;
        if (source == StatementSource.Bank && IsSettlement(line.Description, keywords)) return StatementLineStatus.CardSettlement;
        return StatementLineStatus.Pending;
    }

    private static bool IsSettlement(string description, List<string> keywords) =>
        keywords.Any(k => description.Contains(k, StringComparison.OrdinalIgnoreCase));

    private async Task<HashSet<string>> KnownFingerprintsAsync(Guid businessId, List<string> fingerprints)
    {
        var known = new HashSet<string>();
        foreach (var batch in fingerprints.Chunk(500))
        {
            var found = await db.StatementLines.AsNoTracking()
                .Where(l => l.BusinessId == businessId && batch.Contains(l.Fingerprint))
                .Select(l => l.Fingerprint)
                .ToListAsync();
            known.UnionWith(found);
        }
        return known;
    }

    // A bank settlement is covered when a card statement was uploaded whose total is the same amount.
    private async Task LinkSettlementsAsync(Guid businessId)
    {
        var open = await db.StatementLines
            .Where(l => l.BusinessId == businessId && l.Status == StatementLineStatus.CardSettlement && l.CoveredByFileId == null)
            .ToListAsync();
        if (open.Count == 0) return;

        var cards = await db.StatementFiles.AsNoTracking()
            .Where(f => f.BusinessId == businessId && f.Source == StatementSource.Card)
            .ToListAsync();
        var used = await db.StatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.CoveredByFileId != null)
            .Select(l => l.CoveredByFileId!.Value)
            .ToListAsync();
        var taken = used.ToHashSet();

        foreach (var line in open.OrderBy(l => l.Date))
        {
            var card = cards.FirstOrDefault(c => !taken.Contains(c.Id) && Math.Abs(c.TotalOut - line.Amount) < 0.01m);
            if (card is null) continue;
            line.CoveredByFileId = card.Id;
            taken.Add(card.Id);
        }

        await db.SaveChangesAsync();
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    public async Task<List<StatementFileResponse>> GetFilesAsync()
    {
        var businessId = tenant.GetBusinessId();
        var files = await db.StatementFiles.AsNoTracking()
            .Where(f => f.BusinessId == businessId)
            .OrderByDescending(f => f.UploadedAt)
            .ToListAsync();

        var counts = await db.StatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId)
            .GroupBy(l => l.FileId)
            .Select(g => new { FileId = g.Key, Lines = g.Count(), Pending = g.Count(l => l.Status == StatementLineStatus.Pending) })
            .ToDictionaryAsync(x => x.FileId);

        return files.Select(f =>
        {
            counts.TryGetValue(f.Id, out var c);
            return new StatementFileResponse(f.Id, f.Source, f.FileName, f.PeriodFrom, f.PeriodTo, f.TotalOut, c?.Lines ?? 0, c?.Pending ?? 0, f.UploadedAt);
        }).ToList();
    }

    /// <summary>Removes the file and its lines. Expenses created or matched from them stay.</summary>
    public async Task DeleteFileAsync(Guid id)
    {
        var businessId = tenant.GetBusinessId();
        var file = await db.StatementFiles.FirstOrDefaultAsync(f => f.Id == id && f.BusinessId == businessId)
            ?? throw new NotFoundException("Statement not found");

        var months = await db.StatementLines.AsNoTracking()
            .Where(l => l.FileId == id)
            .Select(l => new { l.Date.Year, l.Date.Month })
            .Distinct()
            .ToListAsync();
        foreach (var m in months) await EnsureMonthOpenAsync(new DateOnly(m.Year, m.Month, 1));

        db.StatementFiles.Remove(file);
        await db.SaveChangesAsync();
    }

    // ── Lines ─────────────────────────────────────────────────────────────────

    public async Task<List<StatementLineResponse>> GetLinesAsync(string? month, StatementLineStatus? status)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.StatementLines.AsNoTracking().Where(l => l.BusinessId == businessId);

        if (month is not null)
        {
            var (from, to) = MonthRange(month);
            query = query.Where(l => l.Date >= from && l.Date <= to);
        }
        if (status is { } s) query = query.Where(l => l.Status == s);

        var lines = await query.OrderByDescending(l => l.Date).ThenBy(l => l.Description).ToListAsync();
        if (lines.Count == 0) return [];

        var linkedIds = lines.Where(l => l.ExpenseId != null).Select(l => l.ExpenseId!.Value).ToList();
        var linked = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId && linkedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Description);

        var pending = lines.Where(l => l.Status == StatementLineStatus.Pending).ToList();
        var pool = pending.Count == 0 ? [] : await UnlinkedExpensesAsync(
            businessId, pending.Min(l => l.Date).AddDays(-StatementMatching.NamedWindowDays), pending.Max(l => l.Date).AddDays(StatementMatching.NamedWindowDays));
        var memory = pending.Count == 0 ? null : await LoadMemoryAsync(businessId);

        return lines.Select(l =>
        {
            var isPending = l.Status == StatementLineStatus.Pending;
            return new StatementLineResponse(
                l.Id, l.Source, l.Date, l.Amount, l.Description, l.Reference, l.Status, l.ExpenseId,
                l.ExpenseId is { } id && linked.TryGetValue(id, out var text) ? text : null,
                l.CoveredByFileId != null,
                isPending ? StatementMatching.SuggestionsFor(l, pool) : [],
                isPending && l.Amount > 0 ? memory!.LearnedFor(l) : null,
                isPending && l.Amount > 0 && memory!.LooksRecurring(l));
        }).ToList();
    }

    private async Task<List<Expense>> UnlinkedExpensesAsync(Guid businessId, DateOnly from, DateOnly to) =>
        await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId && e.Date >= from && e.Date <= to
                && !db.StatementLines.Any(l => l.ExpenseId == e.Id))
            .ToListAsync();

    private async Task<StatementMemory> LoadMemoryAsync(Guid businessId)
    {
        // Expenses she already decided on a line, plus every supplier she has bought from.
        var fromLines = await db.StatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.ExpenseId != null)
            .Join(db.Expenses.AsNoTracking(), l => l.ExpenseId!.Value, e => e.Id, (l, e) => new { l.Description, Expense = e })
            .ToListAsync();
        var fromSuppliers = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == businessId && e.Supplier != null)
            .OrderByDescending(e => e.Date)
            .Take(1000)
            .ToListAsync();

        var decided = fromLines.Select(x => (StatementMatching.KeyOf(x.Description), x.Expense))
            .Concat(fromSuppliers.Select(e => (StatementMatching.KeyOf(e.Supplier!), e)))
            .ToList();

        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-14);
        var history = (await db.StatementLines.AsNoTracking()
                .Where(l => l.BusinessId == businessId && l.Amount > 0 && l.Date >= since)
                .Select(l => new { l.Description, l.Date, l.Amount })
                .ToListAsync())
            .Select(l => (StatementMatching.KeyOf(l.Description), l.Date, l.Amount))
            .ToList();

        return new StatementMemory(decided, history);
    }


    // ── Decisions on one line ─────────────────────────────────────────────────

    public async Task MatchAsync(Guid lineId, Guid expenseId)
    {
        var businessId = tenant.GetBusinessId();
        var line = await FindPendingLineAsync(lineId);

        if (!await db.Expenses.AsNoTracking().AnyAsync(e => e.Id == expenseId && e.BusinessId == businessId))
            throw new NotFoundException("Expense not found");

        line.Status = StatementLineStatus.Matched;
        line.ExpenseId = expenseId;
        await SaveLinkAsync();
    }

    public async Task<ExpenseResponse> CreateExpenseAsync(Guid lineId, CreateExpenseFromLineDto dto)
    {
        var line = await FindPendingLineAsync(lineId);
        if (line.Amount <= 0) throw new BadRequestException("שורה של כסף שנכנס לא הופכת להוצאה");

        var description = string.IsNullOrWhiteSpace(dto.Description) ? line.Description : dto.Description.Trim();
        var supplier = string.IsNullOrWhiteSpace(dto.Supplier) ? null : dto.Supplier.Trim();
        var typeName = string.IsNullOrWhiteSpace(dto.TypeName) ? null : dto.TypeName.Trim();
        var groupName = string.IsNullOrWhiteSpace(dto.GroupName) ? await GroupOfTypeAsync(typeName) : dto.GroupName.Trim();

        // The expense and its link are saved together, so a failure in between cannot leave an expense that
        // the next attempt would create a second time.
        await using var tx = await db.Database.BeginTransactionAsync();

        var created = await expenses.CreateExpenseAsync(new CreateExpenseDto(
            line.Date, dto.RepeatEveryMonths is null ? ExpenseCategory.Variable : ExpenseCategory.Fixed, description, line.Amount,
            dto.RepeatEveryMonths, typeName, null, supplier, null, groupName));

        var fresh = await db.StatementLines.FirstAsync(l => l.Id == lineId && l.BusinessId == line.BusinessId);
        fresh.Status = StatementLineStatus.Created;
        fresh.ExpenseId = created.Id;
        await SaveLinkAsync();

        await tx.CommitAsync();
        return created;
    }

    // The group she set for a category in her settings, so choosing the category is enough.
    private async Task<string?> GroupOfTypeAsync(string? typeName)
    {
        if (typeName is null) return null;

        var businessId = tenant.GetBusinessId();
        return await db.ExpenseTypes.AsNoTracking()
            .Where(t => t.BusinessId == businessId && t.Name == typeName)
            .Select(t => t.GroupName)
            .FirstOrDefaultAsync();
    }

    /// <summary>Adds an expense for every undecided line whose payee was filed before, using the same details. Returns how many.</summary>
    public async Task<int> ApplyKnownAsync(string month)
    {
        var businessId = tenant.GetBusinessId();
        var (from, to) = MonthRange(month);
        await EnsureMonthOpenAsync(from);

        var pending = await db.StatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.Status == StatementLineStatus.Pending && l.Amount > 0 && l.Date >= from && l.Date <= to)
            .OrderBy(l => l.Date)
            .ToListAsync();
        if (pending.Count == 0) return 0;

        var memory = await LoadMemoryAsync(businessId);
        var created = 0;
        foreach (var line in pending)
        {
            if (memory.LearnedFor(line) is not { } learned) continue;

            await CreateExpenseAsync(line.Id, new CreateExpenseFromLineDto(learned.Description, learned.TypeName, learned.GroupName, learned.Supplier));
            created++;
        }
        return created;
    }

    public async Task IgnoreAsync(Guid lineId)
    {
        var line = await FindLineAsync(lineId);
        if (line.Status is not (StatementLineStatus.Pending or StatementLineStatus.CardSettlement))
            throw new BadRequestException("אפשר להתעלם רק משורה שממתינה להחלטה");

        await EnsureMonthOpenAsync(line.Date);
        line.Status = StatementLineStatus.Ignored;
        line.CoveredByFileId = null;
        await db.SaveChangesAsync();
    }

    /// <summary>For a card charge the keywords did not recognise.</summary>
    public async Task MarkSettlementAsync(Guid lineId)
    {
        var line = await FindPendingLineAsync(lineId);
        line.Status = StatementLineStatus.CardSettlement;
        await db.SaveChangesAsync();
        await LinkSettlementsAsync(line.BusinessId);
    }

    /// <summary>Takes a decision back. A created expense stays; only the link to the line is dropped.</summary>
    public async Task ResetAsync(Guid lineId)
    {
        var line = await FindLineAsync(lineId);
        await EnsureMonthOpenAsync(line.Date);

        line.Status = StatementLineStatus.Pending;
        line.ExpenseId = null;
        line.CoveredByFileId = null;
        await db.SaveChangesAsync();
    }

    private async Task<StatementLine> FindLineAsync(Guid id)
    {
        var businessId = tenant.GetBusinessId();
        return await db.StatementLines.FirstOrDefaultAsync(l => l.Id == id && l.BusinessId == businessId)
            ?? throw new NotFoundException("Statement line not found");
    }

    private async Task<StatementLine> FindPendingLineAsync(Guid id)
    {
        var line = await FindLineAsync(id);
        if (line.Status != StatementLineStatus.Pending) throw new BadRequestException("השורה כבר טופלה");
        await EnsureMonthOpenAsync(line.Date);
        return line;
    }

    private async Task SaveLinkAsync()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new BadRequestException("ההוצאה כבר מקושרת לשורה אחרת");
        }
    }

    // ── Months ────────────────────────────────────────────────────────────────

    public async Task<MonthCheckResponse> CheckMonthAsync(string month)
    {
        var businessId = tenant.GetBusinessId();
        var (from, to) = MonthRange(month);

        var lines = await db.StatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId && l.Date >= from && l.Date <= to)
            .ToListAsync();

        var uncovered = lines
            .Where(l => l.Status == StatementLineStatus.CardSettlement && l.CoveredByFileId == null)
            .OrderBy(l => l.Date)
            .Select(l => new UncoveredSettlementResponse(l.Id, l.Date, l.Description, l.Amount))
            .ToList();

        // Entered by hand but in none of the statements: fine for cash, but also how a double entry shows up.
        var notInStatements = (await UnlinkedExpensesAsync(businessId, from, to))
            .OrderBy(e => e.Date)
            .Select(e => new UnlinkedExpenseResponse(e.Id, e.Date, e.Description, e.Supplier, e.Amount))
            .ToList();

        var pending = lines.Count(l => l.Status == StatementLineStatus.Pending);
        var closed = await db.StatementMonthCloses.AnyAsync(m => m.BusinessId == businessId && m.Month == month);

        return new MonthCheckResponse(month, closed, pending, uncovered, notInStatements, pending == 0 && uncovered.Count == 0);
    }

    /// <summary>
    /// Deletes the month's lines that have no expense behind them, so the statement can be uploaded again
    /// and read afresh. Lines linked to an expense stay, and no expense is ever touched.
    /// </summary>
    public async Task<int> ClearOpenLinesAsync(string month)
    {
        var businessId = tenant.GetBusinessId();
        var (from, to) = MonthRange(month);
        await EnsureMonthOpenAsync(from);

        return await db.StatementLines
            .Where(l => l.BusinessId == businessId && l.Date >= from && l.Date <= to && l.ExpenseId == null)
            .ExecuteDeleteAsync();
    }

    public async Task CloseMonthAsync(string month)
    {
        var check = await CheckMonthAsync(month);
        if (check.IsClosed) return;
        if (!check.CanClose) throw new BadRequestException("עוד יש שורות שלא טופלו בחודש הזה");

        db.StatementMonthCloses.Add(new StatementMonthClose
        {
            Id = Guid.NewGuid(), BusinessId = tenant.GetBusinessId(), Month = month, ClosedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task ReopenMonthAsync(string month)
    {
        var businessId = tenant.GetBusinessId();
        MonthRange(month);
        await db.StatementMonthCloses.Where(m => m.BusinessId == businessId && m.Month == month).ExecuteDeleteAsync();
    }

    private async Task EnsureMonthOpenAsync(DateOnly date)
    {
        var businessId = tenant.GetBusinessId();
        var month = $"{date.Year:0000}-{date.Month:00}";
        if (await db.StatementMonthCloses.AsNoTracking().AnyAsync(m => m.BusinessId == businessId && m.Month == month))
            throw new BadRequestException("החודש סגור. צריך לפתוח אותו מחדש כדי לשנות");
    }

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex MonthPattern();

    private static (DateOnly From, DateOnly To) MonthRange(string month)
    {
        if (!MonthPattern().IsMatch(month)) throw new BadRequestException("Month must look like 2026-09");
        var from = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    // ── Keywords ──────────────────────────────────────────────────────────────

    public async Task<List<string>> GetKeywordsAsync()
    {
        var businessId = tenant.GetBusinessId();
        var keywords = await db.CardSettlementKeywords.AsNoTracking()
            .Where(k => k.BusinessId == businessId)
            .OrderBy(k => k.Keyword)
            .Select(k => k.Keyword)
            .ToListAsync();
        if (keywords.Count > 0) return keywords;

        // The first time, the usual card companies are her starting list.
        db.CardSettlementKeywords.AddRange(DefaultKeywords.Select(k => new CardSettlementKeyword
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Keyword = k,
        }));
        await db.SaveChangesAsync();
        return DefaultKeywords.Order().ToList();
    }

    public async Task<List<string>> SaveKeywordsAsync(List<string> keywords)
    {
        var businessId = tenant.GetBusinessId();
        var clean = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Select(k => Truncate(k, 60))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clean.Count == 0) throw new BadRequestException("צריכה להישאר לפחות מילה אחת");

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.CardSettlementKeywords.Where(k => k.BusinessId == businessId).ExecuteDeleteAsync();
        db.CardSettlementKeywords.AddRange(clean.Select(k => new CardSettlementKeyword
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Keyword = k,
        }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return clean.Order().ToList();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
