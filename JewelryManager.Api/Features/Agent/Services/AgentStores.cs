using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace JewelryManager.Api.Features.Agent.Services;

/// <summary>
/// The agent's own whitelist lookup. This is the one place that reads across businesses on purpose: it is how a
/// phone number becomes a person and a business. Everything after it is scoped to that business.
/// </summary>
public class AgentUserDirectory(AppDbContext db)
{
    public Task<AgentUser?> FindActiveByPhoneAsync(string phone, CancellationToken ct = default) =>
        db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Phone == phone && u.IsActive, ct);

    public Task<AgentUser?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);

    public Task<List<AgentUser>> ListActiveAsync(CancellationToken ct = default) =>
        db.AgentUsers.AsNoTracking().Where(u => u.IsActive).ToListAsync(ct);

    public async Task SetNotificationAsync(Guid id, string kind, bool enabled, CancellationToken ct = default)
    {
        var user = await db.AgentUsers.FirstAsync(u => u.Id == id, ct);
        switch (kind)
        {
            case "morning_summary": user.MorningSummary = enabled; break;
            case "store_orders": user.StoreOrderAlerts = enabled; break;
            case "stuck_orders": user.StuckOrderAlerts = enabled; break;
            default: throw new ArgumentException("kind must be morning_summary, store_orders or stuck_orders");
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The short conversation memory: the last few final texts, nothing from tool results.</summary>
public class ConversationStore(AppDbContext db, IOptions<AgentOptions> options)
{
    public async Task AddAsync(AgentUser user, string role, string text, CancellationToken ct = default)
    {
        db.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            BusinessId = user.BusinessId,
            AgentUserId = user.Id,
            Role = role,
            Text = text.Length > 4000 ? text[..4000] : text,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ChatMessage>> RecentAsync(AgentUser user, CancellationToken ct = default)
    {
        var limits = options.Value.Limits;
        var since = DateTime.UtcNow.AddMinutes(-limits.HistoryMinutes);

        var rows = await db.AgentMessages.AsNoTracking()
            .Where(m => m.BusinessId == user.BusinessId && m.AgentUserId == user.Id && m.CreatedAt >= since)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limits.HistoryMessages)
            .ToListAsync(ct);
        rows.Reverse();

        // The model wants the conversation to start with the person.
        var firstUser = rows.FindIndex(m => m.Role == "user");
        if (firstUser < 0) return [];

        return rows.Skip(firstUser)
            .Select(m => m.Role == "assistant" ? ChatMessage.Assistant(m.Text) : ChatMessage.User(m.Text))
            .ToList();
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-options.Value.Limits.MessageRetentionDays);
        return await db.AgentMessages.Where(m => m.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Actions waiting for a yes. Each is answered at most once: the claim is a single atomic update.</summary>
public class PendingActionService(AppDbContext db, IOptions<AgentOptions> options)
{
    public async Task<AgentPendingAction> CreateAsync(AgentUser user, string tool, string argsJson, string summary, CancellationToken ct = default)
    {
        // One question at a time: an older open proposal is dropped when a new one is made.
        await db.AgentPendingActions
            .Where(a => a.BusinessId == user.BusinessId && a.AgentUserId == user.Id && a.ResolvedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvedAt, DateTime.UtcNow).SetProperty(a => a.Resolution, "replaced"), ct);

        var now = DateTime.UtcNow;
        var action = new AgentPendingAction
        {
            Id = Guid.NewGuid(),
            BusinessId = user.BusinessId,
            AgentUserId = user.Id,
            ToolName = tool,
            ArgsJson = argsJson,
            Summary = summary.Length > 500 ? summary[..500] : summary,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(options.Value.Limits.PendingMinutes),
        };
        db.AgentPendingActions.Add(action);
        await db.SaveChangesAsync(ct);
        return action;
    }

    /// <summary>The only open proposal of this person, if there is exactly one (for a typed "כן").</summary>
    public async Task<AgentPendingAction?> FindOpenAsync(AgentUser user, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var open = await db.AgentPendingActions.AsNoTracking()
            .Where(a => a.BusinessId == user.BusinessId && a.AgentUserId == user.Id && a.ResolvedAt == null && a.ExpiresAt > now)
            .OrderByDescending(a => a.CreatedAt)
            .Take(2)
            .ToListAsync(ct);
        return open.Count == 1 ? open[0] : null;
    }

    /// <summary>Claims the proposal for this person. Returns null if it is missing, someone else's, answered, or expired.</summary>
    public async Task<AgentPendingAction?> ClaimAsync(AgentUser user, Guid id, string resolution, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var claimed = await db.AgentPendingActions
            .Where(a => a.Id == id && a.AgentUserId == user.Id && a.BusinessId == user.BusinessId && a.ResolvedAt == null && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvedAt, now).SetProperty(a => a.Resolution, resolution), ct);

        return claimed == 1 ? await db.AgentPendingActions.AsNoTracking().FirstAsync(a => a.Id == id, ct) : null;
    }
}

/// <summary>The audit trail and the undo recipes of what the agent changed.</summary>
public class AuditService(AppDbContext db, IOptions<AgentOptions> options)
{
    public async Task<AgentAuditEntry> RecordAsync(AgentUser user, string tool, string argsJson, bool ok, string? note, UndoCall? undo, CancellationToken ct = default)
    {
        var entry = new AgentAuditEntry
        {
            Id = Guid.NewGuid(),
            BusinessId = user.BusinessId,
            AgentUserId = user.Id,
            Tool = tool,
            ArgsJson = argsJson.Length > 4000 ? argsJson[..4000] : argsJson,
            Ok = ok,
            Note = note is { Length: > 300 } ? note[..300] : note,
            UndoJson = undo is null ? null : JsonSerializer.Serialize(undo, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
        };
        db.AgentAuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>The newest change by this person that can still be taken back.</summary>
    public Task<AgentAuditEntry?> LastUndoableAsync(AgentUser user, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddMinutes(-options.Value.Limits.UndoMinutes);
        return db.AgentAuditEntries.AsNoTracking()
            .Where(a => a.BusinessId == user.BusinessId && a.AgentUserId == user.Id && a.Ok
                        && a.UndoJson != null && a.UndoneAt == null && a.CreatedAt >= since)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Marks the entry as taken back; false if it already was (a double "בטלי" must not undo twice).</summary>
    public async Task<bool> MarkUndoneAsync(AgentUser user, Guid id, CancellationToken ct = default) =>
        await db.AgentAuditEntries.Where(a => a.Id == id && a.BusinessId == user.BusinessId && a.AgentUserId == user.Id && a.UndoneAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UndoneAt, DateTime.UtcNow), ct) == 1;

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-options.Value.Limits.AuditRetentionMonths);
        var processed = DateTime.UtcNow.AddDays(-7);
        await db.AgentProcessedMessages.Where(m => m.ReceivedAt < processed).ExecuteDeleteAsync(ct);
        return await db.AgentAuditEntries.Where(a => a.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
