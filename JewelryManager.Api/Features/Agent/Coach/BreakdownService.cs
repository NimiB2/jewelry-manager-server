using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>
/// Turns a big or stuck task into a few small steps. The model only words the steps; the result is checked by
/// code and waits for her approval (the same button as any other change) before a single task is created.
/// </summary>
public class BreakdownService(
    IAgentModel model,
    UsageService usage,
    PendingActionService pending,
    PreferenceService preferences,
    CoachLedger ledger,
    ILogger<BreakdownService> log)
{
    private const string Prompt = """
        You help the owner of a small handmade-jewelry business who keeps postponing a task.
        Split the task into 3 to 5 small concrete steps, each doable in 10 to 30 minutes, in the order she would do them.
        The first step must be almost trivial (open something, list three things, choose one) so starting costs no effort.
        Write each step in Hebrew, short, starting with an infinitive verb (לבחור, לצלם, לכתוב). No numbering, no explanations.
        Do not repeat anything from the "already finished or refused" list.
        Answer with only a JSON array of strings.
        """;

    public async Task<AgentReply> ProposeAsync(AgentUser user, CoachItem item, Guid taskId, CancellationToken ct)
    {
        var steps = await GenerateAsync(user, item.Title, ct);
        if (steps is null)
            return new AgentReply("לא הצלחתי לפרק את זה עכשיו. אפשר לכתוב לי במילים שלך מה צריך לעשות, ואעזור לסדר.");

        var summary = $"לפרק את \"{item.Title}\" ל-{steps.Count} צעדים קטנים?\n" + string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));
        var args = JsonSerializer.Serialize(new { taskId, steps, summary });
        var action = await pending.CreateAsync(user, "break_down_task", args, summary, ct);
        return new AgentReply(summary, action.Id);
    }

    /// <summary>Asks the model for the steps. Null if the answer cannot be used.</summary>
    public async Task<List<string>?> GenerateAsync(AgentUser user, string title, CancellationToken ct)
    {
        var closed = await ledger.ClosedTitlesAsync(user, 30, ct);
        var personal = string.Join("\n", (await preferences.ListAsync(user, ct)).Select(p => "- " + p.Instruction));
        var context = (personal.Length > 0 ? "Her saved style preferences:\n" + personal + "\n" : "")
                      + (closed.Count > 0 ? "Already finished or refused (do not repeat):\n" + string.Join("\n", closed.Select(c => "- " + c)) : "");

        var turn = await model.CompleteAsync(new ModelRequest(Prompt, context, [], [ChatMessage.User("המשימה: " + title)]), ct);
        await usage.RecordAsync(user, model.ModelName, turn.Usage, ct);

        var steps = ParseSteps(turn.Text);
        if (steps is null) log.LogWarning("The breakdown answer could not be used");
        return steps?.Where(s => !CoachLedger.LooksLikeAny(s, closed)).ToList() is { Count: >= 2 } clean ? clean : steps is { Count: >= 2 } ? steps : null;
    }

    /// <summary>Reads a JSON array of 2 to 6 short strings out of the model's answer; null if it is not that.</summary>
    public static List<string>? ParseSteps(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        try
        {
            var raw = JsonSerializer.Deserialize<List<string>>(text[start..(end + 1)]);
            var steps = raw?.Select(s => System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"^\s*(\d+[\.\)]|[-*•])\s*", "").Trim())
                .Where(s => s.Length > 0).Select(s => s.Length > 120 ? s[..120] : s).ToList();
            return steps is { Count: >= 2 and <= 6 } ? steps : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
