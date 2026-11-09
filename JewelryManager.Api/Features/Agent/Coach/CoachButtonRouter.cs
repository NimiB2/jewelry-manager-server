namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Sends the buttons under the agent's own messages to whoever sent them: task reminders (c:), occasions (o:),
/// customers (a:), the metal index (m:), next week's focus (f:) and the first-week questions (i:).
/// Returns null for a button that is not one of these, so the ordinary approve/cancel buttons carry on.
/// </summary>
public class CoachButtonRouter(
    TaskCoach tasks,
    OccasionPlanner occasions,
    AnniversaryCoach anniversaries,
    MetalIndexService metals,
    RhythmService rhythm,
    IntroService intro)
{
    public async Task<AgentReply?> HandleAsync(Data.Entities.AgentUser user, string buttonId, CancellationToken ct)
    {
        if (buttonId.StartsWith(TaskCoach.Prefix, StringComparison.Ordinal)) return await tasks.HandleButtonAsync(user, buttonId, ct);
        if (buttonId.StartsWith(OccasionPlanner.Prefix, StringComparison.Ordinal)) return await occasions.HandleButtonAsync(user, buttonId, ct);
        if (buttonId.StartsWith(AnniversaryCoach.Prefix, StringComparison.Ordinal)) return await anniversaries.HandleButtonAsync(user, buttonId, ct);
        if (buttonId.StartsWith(MetalIndexService.Prefix, StringComparison.Ordinal)) return await metals.HandleButtonAsync(user, buttonId, ct);
        if (buttonId.StartsWith(RhythmService.FocusPrefix, StringComparison.Ordinal)) return await rhythm.HandleFocusButtonAsync(user, buttonId, ct);
        if (buttonId.StartsWith(IntroService.Prefix, StringComparison.Ordinal)) return await intro.HandleButtonAsync(user, buttonId, ct);
        return null;
    }
}
