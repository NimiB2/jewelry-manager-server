using System.Text;
using System.Text.Json;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Once a day, reads what she wrote in the last 24 hours and closes the open items she says she finished. This
/// is the safety net for the model forgetting to record something mid-conversation: a "done" that nobody wrote
/// down must not come back as a reminder. It makes one cheap call, and only if there is something to compare.
/// </summary>
public class DoneReconciler(AppDbContext db, CoachLedger ledger, IAgentModel model, UsageService usage, ILogger<DoneReconciler> log)
{
    private const string Prompt = """
        You compare what a business owner wrote with a list of open items.
        Return a JSON array with the numbers of the items she clearly says she already did, finished or handled.
        Return [] if she did not say so clearly. Never guess. Answer with only the JSON array of numbers.
        """;

    public async Task RunAsync(AgentUser user, CancellationToken ct)
    {
        var key = $"reconcile:{AgentClock.LocalToday():yyyy-MM-dd}:{user.Id}";
        if (await ledger.FindAsync(user, key, ct) is not null) return;

        var open = await ledger.OpenItemsAsync(user, 10, ct);
        var written = await db.AgentMessages.AsNoTracking()
            .Where(m => m.BusinessId == user.BusinessId && m.AgentUserId == user.Id && m.Role == "user" && m.Source == "chat"
                        && m.CreatedAt >= DateTime.UtcNow.AddHours(-24))
            .OrderByDescending(m => m.CreatedAt).Take(15).Select(m => m.Text).ToListAsync(ct);

        // Nothing to compare: no call, and no marker, so a later message today can still be checked.
        if (open.Count == 0 || written.Count == 0) return;

        var item = await ledger.EnsureAsync(user, key, CoachKind.System, "reconcile", ct: ct);
        await ledger.SetStateAsync(item, CoachState.Done, ct: ct);

        var text = new StringBuilder("Open items:\n");
        for (var i = 0; i < open.Count; i++) text.AppendLine($"{i + 1}. {open[i].Title}");
        text.AppendLine("\nHer messages:");
        foreach (var message in written) text.AppendLine("- " + (message.Length > 300 ? message[..300] : message));

        var turn = await model.CompleteAsync(new ModelRequest(Prompt, "", [], [ChatMessage.User(text.ToString())]), ct);
        await usage.RecordAsync(user, model.ModelName, turn.Usage, ct);

        foreach (var number in ParseNumbers(turn.Text, open.Count))
        {
            var done = await ledger.GetAsync(user, open[number - 1].Id, ct);
            if (done is null) continue;
            log.LogInformation("Closed an item she said she finished");
            await ledger.SetStateAsync(done, CoachState.Done, ct: ct);
        }
    }

    /// <summary>Reads a JSON array of item numbers (1-based), ignoring anything out of range or repeated.</summary>
    public static List<int> ParseNumbers(string text, int max)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        try
        {
            return (JsonSerializer.Deserialize<List<int>>(text[start..(end + 1)]) ?? [])
                .Where(n => n >= 1 && n <= max).Distinct().ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
