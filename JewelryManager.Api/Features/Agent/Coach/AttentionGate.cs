namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Three kinds of message the agent starts on its own.
/// Alert: something real happened (a store order is waiting). Scheduled: she asked for it at a set time
/// (morning, Friday review, a due-date reminder). Coaching: the agent's own ideas; the only kind that is rationed.
/// </summary>
public enum MessageClass
{
    Alert,
    Scheduled,
    Coaching,
}

public record GateInput(
    MessageClass Class,
    DateTime LocalNow,
    string RestDays,
    bool Muted,
    DateTime? LocalLastUserMessage,
    DateTime? LocalLastOutbound,
    DateTime? LocalLastCoach,
    int CoachUnanswered,
    int CoachSentToday,
    int CoachSentThisWeek,
    bool FewerMessages);

public record GateDecision(bool Allowed, string Reason)
{
    public static GateDecision Yes { get; } = new(true, "ok");

    public static GateDecision No(string reason) => new(false, reason);
}

/// <summary>
/// Decides, in plain code, whether the agent may speak now. A message is a decision with a reason, and
/// silence is a perfectly good result. Every rule here is covered by tests.
/// </summary>
public static class AttentionGate
{
    public const int DayStartHour = 8;
    public const int DayEndHour = 21;
    public const int DailyCoachBudget = 1;
    public const int WeeklyCoachBudget = 3;
    public const int WeeklyCoachBudgetFewer = 1;
    public const int MinGapHours = 3;
    public const int MaxUnanswered = 2;

    public static GateDecision Decide(GateInput i)
    {
        if (i.Muted) return GateDecision.No("muted");

        var now = i.LocalNow;
        if (now.Hour < DayStartHour || now.Hour >= DayEndHour) return GateDecision.No("quiet hours");
        if (IsRestTime(i.RestDays, now)) return GateDecision.No("rest day");

        if (i.Class != MessageClass.Coaching) return GateDecision.Yes;

        // From here on: the agent's own ideas, which are rationed and never push.
        if (i.CoachUnanswered >= MaxUnanswered) return GateDecision.No("paused until she writes");

        if (i.LocalLastUserMessage is { } lastUser)
        {
            if (lastUser.Date == now.Date) return GateDecision.No("talked today");
            if (now - lastUser < TimeSpan.FromMinutes(30)) return GateDecision.No("talking now");
        }

        if (i.LocalLastOutbound is { } outbound && now - outbound < TimeSpan.FromHours(MinGapHours))
            return GateDecision.No("another message just went out");

        if (i.CoachSentToday >= DailyCoachBudget) return GateDecision.No("daily budget");
        if (i.CoachSentThisWeek >= (i.FewerMessages ? WeeklyCoachBudgetFewer : WeeklyCoachBudget)) return GateDecision.No("weekly budget");

        // Each unanswered idea doubles the wait before the next one.
        if (i.LocalLastCoach is { } lastCoach && now - lastCoach < TimeSpan.FromHours(24 * Math.Pow(2, i.CoachUnanswered)))
            return GateDecision.No("backing off");

        return GateDecision.Yes;
    }

    public static bool IsRestTime(string restDays, DateTime local) => restDays switch
    {
        "saturday" => local.DayOfWeek == DayOfWeek.Saturday,
        "friday_saturday" => local.DayOfWeek == DayOfWeek.Saturday || (local.DayOfWeek == DayOfWeek.Friday && local.Hour >= 14),
        _ => false,
    };
}
