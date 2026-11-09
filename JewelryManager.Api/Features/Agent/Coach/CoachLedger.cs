using System.Text.RegularExpressions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Services;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent.Coach;

public static class CoachKind
{
    public const string Task = "task";
    public const string Season = "season";
    public const string Growth = "growth";
    public const string Review = "review";
    public const string Morning = "morning";
    public const string MonthEnd = "monthend";
    public const string Anniversary = "anniversary";
    public const string Metal = "metal";
    public const string Mute = "mute";
    public const string System = "system";

    /// <summary>Bookkeeping items (a morning already sent, a mute switch): never shown to the model as suggestions or as closed ideas.</summary>
    public static readonly string[] Markers = [Mute, System, Morning, Review, MonthEnd];

    /// <summary>The topics she can switch off with "תפסיקי לשאול על ...".</summary>
    public static readonly string[] Mutable = [Task, Season, Growth, Review, Morning, MonthEnd, Anniversary, Metal];
}

public static class CoachState
{
    public const string Suggested = "suggested";
    public const string Accepted = "accepted";
    public const string Done = "done";
    public const string Declined = "declined";
    public const string Snoozed = "snoozed";
    public const string Expired = "expired";
    public const string Parked = "parked";
}

/// <summary>
/// The memory of what the agent has already said and what became of it. The code asks the ledger before it
/// speaks, so something she finished or refused is never offered again, whatever the model would like to say.
/// </summary>
public class CoachLedger(AppDbContext db)
{
    public const int MaxMentions = 2;

    public Task<CoachItem?> FindAsync(AgentUser user, string key, CancellationToken ct = default) =>
        db.CoachItems.FirstOrDefaultAsync(i => i.BusinessId == user.BusinessId && i.Key == key, ct);

    public Task<CoachFocus?> GetFocusAsync(AgentUser user, CancellationToken ct = default) =>
        db.CoachFocuses.AsNoTracking().FirstOrDefaultAsync(f => f.BusinessId == user.BusinessId, ct);

    public Task<CoachItem?> GetAsync(AgentUser user, Guid id, CancellationToken ct = default) =>
        db.CoachItems.FirstOrDefaultAsync(i => i.BusinessId == user.BusinessId && i.Id == id, ct);

    /// <summary>
    /// Gets the item to speak about, or null if the ledger says to stay quiet: it is done, declined, expired,
    /// parked, snoozed, or has already been mentioned the allowed number of times.
    /// </summary>
    public async Task<CoachItem?> OfferAsync(AgentUser user, string key, string kind, string title, Guid? taskId = null, CancellationToken ct = default)
    {
        var existing = await FindAsync(user, key, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            var item = new CoachItem
            {
                Id = Guid.NewGuid(),
                BusinessId = user.BusinessId,
                Key = key,
                Kind = kind,
                Title = title.Length > 300 ? title[..300] : title,
                TaskId = taskId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CoachItems.Add(item);
            try
            {
                await db.SaveChangesAsync(ct);
                return item;
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.Entry(item).State = EntityState.Detached;
                return null;
            }
        }

        if (!CanSpeak(existing, now)) return null;
        return existing;
    }

    /// <summary>Finds the item or creates it, with no speaking limit: the task ladder does its own pacing.</summary>
    public async Task<CoachItem> EnsureAsync(AgentUser user, string key, string kind, string title, Guid? taskId = null, CancellationToken ct = default)
    {
        var existing = await FindAsync(user, key, ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var item = new CoachItem
        {
            Id = Guid.NewGuid(), BusinessId = user.BusinessId, Key = key, Kind = kind,
            Title = title.Length > 300 ? title[..300] : title, TaskId = taskId, CreatedAt = now, UpdatedAt = now,
        };
        db.CoachItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.Entry(item).State = EntityState.Detached;
            return await FindAsync(user, key, ct) ?? throw ex;
        }
    }

    public async Task<Dictionary<string, CoachItem>> ByKeysAsync(AgentUser user, IEnumerable<string> keys, CancellationToken ct = default)
    {
        var list = keys.Distinct().ToList();
        return await db.CoachItems.Where(i => i.BusinessId == user.BusinessId && list.Contains(i.Key)).ToDictionaryAsync(i => i.Key, ct);
    }

    public static bool CanSpeak(CoachItem item, DateTime now) => item.State switch
    {
        CoachState.Suggested or CoachState.Accepted => item.MentionCount < MaxMentions,
        CoachState.Snoozed => item.SnoozedUntil is { } until && until <= now && item.MentionCount < MaxMentions + 1,
        _ => false,
    };

    public async Task MentionedAsync(CoachItem item, CancellationToken ct = default)
    {
        item.LastMentionedAt = DateTime.UtcNow;
        item.MentionCount++;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStateAsync(CoachItem item, string state, int? stage = null, DateTime? snoozedUntil = null, CancellationToken ct = default)
    {
        item.State = state;
        if (stage is { } s) item.Stage = s;
        item.SnoozedUntil = snoozedUntil;
        item.UpdatedAt = DateTime.UtcNow;
        item.ResolvedAt = state is CoachState.Done or CoachState.Declined or CoachState.Expired or CoachState.Parked ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Closes the ledger item of a task (by task id) as done. Quiet if there is none.</summary>
    public async Task TaskDoneAsync(AgentUser user, Guid taskId, CancellationToken ct = default)
    {
        var item = await FindAsync(user, TaskKey(taskId), ct);
        if (item is not null && item.State != CoachState.Done) await SetStateAsync(item, CoachState.Done, ct: ct);
    }

    /// <summary>After an undo of "done": the task is open again, so its ladder starts over.</summary>
    public async Task TaskReopenedAsync(AgentUser user, Guid taskId, CancellationToken ct = default)
    {
        var item = await FindAsync(user, TaskKey(taskId), ct);
        if (item is not null && item.State == CoachState.Done)
        {
            item.MentionCount = 0;
            await SetStateAsync(item, CoachState.Suggested, stage: 0, ct: ct);
        }
    }

    public static string TaskKey(Guid taskId) => $"task:{taskId}";

    /// <summary>Open suggestions, newest first: shown to the model so "עשיתי" can be matched to the right item.</summary>
    public Task<List<CoachItem>> OpenItemsAsync(AgentUser user, int take, CancellationToken ct = default) =>
        db.CoachItems.AsNoTracking()
            .Where(i => i.BusinessId == user.BusinessId && !CoachKind.Markers.Contains(i.Kind) && (i.State == CoachState.Suggested || i.State == CoachState.Accepted))
            .OrderByDescending(i => i.UpdatedAt).Take(take).ToListAsync(ct);

    /// <summary>What was finished or refused lately: the model must not suggest it again.</summary>
    public async Task<List<string>> ClosedTitlesAsync(AgentUser user, int days, CancellationToken ct = default) =>
        await db.CoachItems.AsNoTracking()
            .Where(i => i.BusinessId == user.BusinessId && !CoachKind.Markers.Contains(i.Kind)
                        && (i.State == CoachState.Done || i.State == CoachState.Declined)
                        && i.UpdatedAt >= DateTime.UtcNow.AddDays(-days))
            .OrderByDescending(i => i.UpdatedAt).Select(i => i.Title).Take(30).ToListAsync(ct);

    /// <summary>Focus ideas offered in the Friday review and never answered fade away after a week (they count as ignored).</summary>
    public async Task ExpireStaleIdeasAsync(CancellationToken ct = default) =>
        await db.CoachItems.Where(i => i.Kind == CoachKind.Growth && i.State == CoachState.Suggested && i.CreatedAt < DateTime.UtcNow.AddDays(-8))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, CoachState.Expired).SetProperty(i => i.ResolvedAt, DateTime.UtcNow), ct);

    public async Task<bool> IsMutedAsync(AgentUser user, string kind, CancellationToken ct = default) =>
        await db.CoachItems.AsNoTracking().AnyAsync(i => i.BusinessId == user.BusinessId && i.Key == "mute:" + kind && i.State == CoachState.Declined, ct);

    public async Task MuteAsync(AgentUser user, string kind, CancellationToken ct = default)
    {
        var item = await FindAsync(user, "mute:" + kind, ct);
        if (item is null)
        {
            db.CoachItems.Add(new CoachItem
            {
                Id = Guid.NewGuid(), BusinessId = user.BusinessId, Key = "mute:" + kind, Kind = CoachKind.Mute,
                Title = kind, State = CoachState.Declined, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (item.State != CoachState.Declined)
        {
            await SetStateAsync(item, CoachState.Declined, ct: ct);
        }
    }

    public async Task UnmuteAsync(AgentUser user, string kind, CancellationToken ct = default) =>
        await db.CoachItems.Where(i => i.BusinessId == user.BusinessId && i.Key == "mute:" + kind).ExecuteDeleteAsync(ct);

    /// <summary>How many ideas of this kind she turned down or ignored lately (three in 60 days puts the kind to sleep).</summary>
    public async Task<int> RecentRefusalsAsync(AgentUser user, string keyPrefix, int days, CancellationToken ct = default) =>
        await db.CoachItems.AsNoTracking().CountAsync(i => i.BusinessId == user.BusinessId && i.Key.StartsWith(keyPrefix)
            && (i.State == CoachState.Declined || i.State == CoachState.Expired) && i.UpdatedAt >= DateTime.UtcNow.AddDays(-days), ct);

    // ── Similarity: the second line of defence against repeating something already closed ────────────────────

    /// <summary>True if the new idea looks like one already finished or refused (same words, in any order).</summary>
    public static bool LooksLikeAny(string candidate, IEnumerable<string> closedTitles)
    {
        var words = Words(candidate);
        if (words.Count == 0) return false;

        foreach (var closed in closedTitles)
        {
            var other = Words(closed);
            if (other.Count == 0) continue;
            var shared = words.Intersect(other).Count();
            var smaller = Math.Min(words.Count, other.Count);
            if (shared >= 2 && (double)shared / smaller >= 0.6) return true;
            if (words.SetEquals(other)) return true;
        }

        return false;
    }

    private static HashSet<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3).ToHashSet();
}
