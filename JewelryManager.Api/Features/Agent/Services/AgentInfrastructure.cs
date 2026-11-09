using System.Collections.Concurrent;
using System.Threading.Channels;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.WhatsApp;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent.Services;

/// <summary>Messages accepted by the webhook, waiting for the worker. The webhook answers Meta at once and works later.</summary>
public class AgentInbox
{
    private readonly Channel<IncomingMessage> _channel = Channel.CreateBounded<IncomingMessage>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite });

    public bool TryEnqueue(IncomingMessage message) => _channel.Writer.TryWrite(message);

    public ChannelReader<IncomingMessage> Reader => _channel.Reader;
}

/// <summary>A small sliding-window limit on messages per minute for each phone number.</summary>
public class MessageRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    public bool TryAcquire(string phone, int perMinute, DateTime? now = null)
    {
        var time = now ?? DateTime.UtcNow;
        var queue = _hits.GetOrAdd(phone, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < time.AddMinutes(-1)) queue.Dequeue();
            if (queue.Count >= perMinute) return false;
            queue.Enqueue(time);
            return true;
        }
    }
}

/// <summary>Holds the photo of an invoice while its proposal waits for a yes, so it can be attached to the expense.</summary>
public class PendingMediaCache
{
    private readonly ConcurrentDictionary<Guid, (byte[] Content, string FileName, DateTime Expires)> _items = new();

    public void Put(Guid pendingId, byte[] content, string fileName, TimeSpan lifetime)
    {
        foreach (var stale in _items.Where(i => i.Value.Expires < DateTime.UtcNow).Select(i => i.Key).ToList())
            _items.TryRemove(stale, out _);
        _items[pendingId] = (content, fileName, DateTime.UtcNow + lifetime);
    }

    public (byte[] Content, string FileName)? Take(Guid pendingId) =>
        _items.TryRemove(pendingId, out var item) && item.Expires >= DateTime.UtcNow ? (item.Content, item.FileName) : null;
}

/// <summary>Remembers which proactive messages and warnings were already sent, so none goes out twice.</summary>
public class ProactiveLog(AppDbContext db)
{
    /// <returns>True the first time this (person, kind, key) is claimed; false if it was claimed before.</returns>
    public async Task<bool> TryClaimAsync(Guid agentUserId, string kind, string key, CancellationToken ct = default)
    {
        db.AgentProactiveLog.Add(new AgentProactiveSent
        {
            Id = Guid.NewGuid(),
            AgentUserId = agentUserId,
            Kind = kind,
            Key = key.Length > 120 ? key[..120] : key,
            SentAt = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>Forgets a claim whose message could not be sent, so the next round tries again.</summary>
    public async Task ReleaseAsync(Guid agentUserId, string kind, string key, CancellationToken ct = default) =>
        await db.AgentProactiveLog.Where(s => s.AgentUserId == agentUserId && s.Kind == kind && s.Key == key).ExecuteDeleteAsync(ct);
}
