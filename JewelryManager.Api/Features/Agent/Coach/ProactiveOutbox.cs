using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// The only door for messages the agent starts on its own. It asks the attention gate, sends, and writes the
/// message into the same timeline as the chat, so her reply ("כן", "תפרקי") is understood in context.
/// </summary>
public class ProactiveOutbox(
    AppDbContext db,
    IWhatsAppClient whatsapp,
    ConversationStore conversation,
    CoachLedger ledger,
    Microsoft.Extensions.Options.IOptions<AgentOptions> options,
    ILogger<ProactiveOutbox> log)
{
    public const string CoachSource = "coach";
    public const string ScheduledSource = "scheduled";
    public const string AlertSource = "alert";

    /// <summary>Sends if the gate allows. Returns false (and sends nothing) if it does not or the send fails.</summary>
    public async Task<bool> SendAsync(AgentUser user, string text, MessageClass cls, string kind,
        IReadOnlyList<ReplyButton>? buttons = null, CancellationToken ct = default, string? template = null)
    {
        var decision = await DecideAsync(user, cls, kind, ct);
        if (!decision.Allowed)
        {
            log.LogDebug("Proactive {Kind} held back: {Reason}", kind, decision.Reason);
            return false;
        }

        try
        {
            if (buttons is { Count: > 0 }) await whatsapp.SendButtonsAsync(user.Phone, text, buttons, ct);
            else await whatsapp.SendTextAsync(user.Phone, text, ct);
        }
        catch (HttpRequestException ex)
        {
            // Outside the 24-hour window only an approved template may start a conversation.
            if (string.IsNullOrWhiteSpace(template) || !await TrySendTemplateAsync(user, template, text, ct))
            {
                log.LogWarning(ex, "Proactive {Kind} could not be sent", kind);
                return false;
            }
        }

        var source = cls switch { MessageClass.Coaching => CoachSource, MessageClass.Alert => AlertSource, _ => ScheduledSource };
        await conversation.AddAsync(user, "assistant", text, source, ct);

        var now = DateTime.UtcNow;
        if (cls == MessageClass.Coaching)
            await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.LastOutboundAt, now).SetProperty(u => u.LastCoachAt, now).SetProperty(u => u.CoachUnanswered, u => u.CoachUnanswered + 1), ct);
        else
            await db.AgentUsers.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastOutboundAt, now), ct);
        return true;
    }

    private async Task<bool> TrySendTemplateAsync(AgentUser user, string template, string text, CancellationToken ct)
    {
        try
        {
            await whatsapp.SendTemplateAsync(user.Phone, template, options.Value.Proactive.TemplateLanguage, [text], ct);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<GateDecision> DecideAsync(AgentUser user, MessageClass cls, string kind, CancellationToken ct = default)
    {
        // The row may have changed since the caller loaded it (she wrote, a counter moved).
        var fresh = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == user.Id, ct) ?? user;

        var todayStart = AgentClock.StartOfTodayUtc();
        var weekStart = todayStart.AddDays(-6);
        return AttentionGate.Decide(new GateInput(
            cls,
            AgentClock.LocalNow(),
            fresh.RestDays,
            await ledger.IsMutedAsync(user, kind, ct),
            ToLocal(fresh.LastUserMessageAt),
            ToLocal(fresh.LastOutboundAt),
            ToLocal(fresh.LastCoachAt),
            fresh.CoachUnanswered,
            cls == MessageClass.Coaching ? await conversation.CountSentAsync(user, CoachSource, todayStart, ct) : 0,
            cls == MessageClass.Coaching ? await conversation.CountSentAsync(user, CoachSource, weekStart, ct) : 0,
            fresh.FewerMessages));
    }

    private static DateTime? ToLocal(DateTime? utc) =>
        utc is null ? null : AgentClock.ToLocal(utc.Value);
}
