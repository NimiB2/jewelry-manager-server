using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace JewelryManager.Mcp;

/// <summary>
/// To-do tools. Tasks are found by their title; nothing here deletes a task. A task can be completed, given a
/// date, set aside ("בצד") or broken into small steps, and each of these can be taken back.
/// </summary>
[McpServerToolType]
public class TaskTools(IBackendApi api)
{
    private const string SummaryHint = "One short Hebrew sentence saying exactly what will be done.";

    // ── Read ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_tasks", ReadOnly = true)]
    [Description("Her tasks. scope: open (default), parked (set aside), done_recent (finished in the last 14 days) or all. At most 15.")]
    public Task<string> ListTasks(string? scope = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/tasks", ct);
            var all = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : [];
            var wanted = (scope ?? "open").Trim().ToLowerInvariant();

            var picked = all.Where(t => wanted switch
            {
                "parked" => !IsCompleted(t) && Has(t, "parkedAt"),
                "done_recent" => IsCompleted(t) && CompletedWithin(t, 14),
                "all" => true,
                _ => !IsCompleted(t) && !Has(t, "parkedAt"),
            }).Take(15).Select(t => new JsonObject
            {
                ["id"] = ToolSupport.Str(t, "id"),
                ["title"] = ToolSupport.Str(t, "title"),
                ["status"] = ToolSupport.Str(t, "status"),
                ["dueDate"] = ToolSupport.Str(t, "dueDate"),
                ["postponed"] = t.TryGetProperty("deferCount", out var d) && d.TryGetInt32(out var n) ? n : 0,
                ["stepOf"] = ToolSupport.Str(t, "parentTaskId"),
            }).ToList();

            var array = new JsonArray();
            foreach (var item in picked) array.Add(item);
            return ToolEnvelope.Ok(new JsonObject { ["count"] = picked.Count, ["tasks"] = array });
        });

    // ── Small and reversible: run at once ─────────────────────────────────────

    [McpServerTool(Name = "add_task")]
    [Description("Add a task. dueDate is yyyy-MM-dd and optional. The result says if it looks big enough to offer a breakdown.")]
    public Task<string> AddTask(string title, string? dueDate = null, string? notes = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var clean = title.Trim();
            if (clean.Length is < 2 or > 200) throw new ArgumentException("A title is between 2 and 200 characters");
            if (dueDate is not null && !DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("dueDate must look like 2026-11-20");

            var created = await api.SendAsync(HttpMethod.Post, "/tasks", new { title = clean, content = notes, status = "NEW", dueDate }, ct);
            var id = ToolSupport.IdOf(created);
            return ToolEnvelope.Ok(new JsonObject { ["id"] = id.ToString(), ["title"] = clean, ["suggestBreakdown"] = TaskHeuristics.LooksBig(clean) },
                new UndoCall("POST", "/tasks/batch-delete", new { ids = new[] { id } }));
        });

    [McpServerTool(Name = "complete_task")]
    [Description("Mark a task as done, found by its title (a unique part of the title is enough).")]
    public Task<string> CompleteTask(string title, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var task = await FindOpenAsync(title, ct);
            var id = ToolSupport.IdOf(task);
            var previous = ToolSupport.Str(task, "status") ?? "NEW";

            await api.SendAsync(HttpMethod.Patch, $"/tasks/{id}/status", new { status = "COMPLETED" }, ct);
            return ToolEnvelope.Ok(new JsonObject { ["id"] = id.ToString(), ["title"] = ToolSupport.Str(task, "title") },
                new UndoCall("PATCH", $"/tasks/{id}/status", new { status = previous }));
        });

    [McpServerTool(Name = "set_task_due")]
    [Description("Give a task a date (yyyy-MM-dd), found by its title.")]
    public Task<string> SetTaskDue(string title, string dueDate, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (!DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("dueDate must look like 2026-11-20");

            var task = await FindOpenAsync(title, ct);
            var id = ToolSupport.IdOf(task);
            var before = ToolSupport.Str(task, "dueDate");

            await api.SendAsync(HttpMethod.Put, $"/tasks/{id}", FullBody(task, dueDate), ct);
            UndoCall undo = before is null
                ? new UndoCall("PATCH", $"/tasks/{id}/coach", new { action = "clear-due" })
                : new UndoCall("PUT", $"/tasks/{id}", FullBody(task, before));
            return ToolEnvelope.Ok(new JsonObject { ["id"] = id.ToString(), ["title"] = ToolSupport.Str(task, "title"), ["dueDate"] = dueDate }, undo);
        });

    [McpServerTool(Name = "park_task")]
    [Description("Set a task aside (\"בצד\") at her request: it stops being raised but can be listed and brought back.")]
    public Task<string> ParkTask(string title, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var task = await FindOpenAsync(title, ct);
            var id = ToolSupport.IdOf(task);
            await api.SendAsync(HttpMethod.Patch, $"/tasks/{id}/coach", new { action = "park" }, ct);
            return ToolEnvelope.Ok(new JsonObject { ["id"] = id.ToString(), ["title"] = ToolSupport.Str(task, "title") },
                new UndoCall("PATCH", $"/tasks/{id}/coach", new { action = "unpark" }));
        });

    [McpServerTool(Name = "unpark_task")]
    [Description("Bring a task back from \"בצד\" into the open list.")]
    public Task<string> UnparkTask(string title, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/tasks", ct);
            var task = Match(data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Where(t => !IsCompleted(t) && Has(t, "parkedAt")) : [], title);
            var id = ToolSupport.IdOf(task);
            await api.SendAsync(HttpMethod.Patch, $"/tasks/{id}/coach", new { action = "unpark" }, ct);
            return ToolEnvelope.Ok(new JsonObject { ["id"] = id.ToString(), ["title"] = ToolSupport.Str(task, "title") },
                new UndoCall("PATCH", $"/tasks/{id}/coach", new { action = "park" }));
        });

    // ── Waits for her approval ────────────────────────────────────────────────

    [McpServerTool(Name = "break_down_task")]
    [Description("Split a big task into small steps (3 to 6 short Hebrew steps of 10 to 30 minutes). taskId comes from list_tasks.")]
    public Task<string> BreakDownTask(Guid taskId, List<string> steps, [Description(SummaryHint)] string summary = "", CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var created = await CreateStepsAsync(steps, taskId, ct);
            return ToolEnvelope.Ok(new JsonObject { ["created"] = created.Count },
                new UndoCall("POST", "/tasks/batch-delete", new { ids = created }));
        });

    [McpServerTool(Name = "add_tasks")]
    [Description("Add several tasks at once (a checklist), at most 12.")]
    public Task<string> AddTasks(List<string> steps, [Description(SummaryHint)] string summary = "", CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var created = await CreateStepsAsync(steps, null, ct);
            return ToolEnvelope.Ok(new JsonObject { ["created"] = created.Count },
                new UndoCall("POST", "/tasks/batch-delete", new { ids = created }));
        });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<Guid>> CreateStepsAsync(List<string> steps, Guid? parent, CancellationToken ct)
    {
        var clean = (steps ?? []).Select(s => s.Trim()).Where(s => s.Length is >= 2 and <= 200).Take(12).ToList();
        if (clean.Count < 2) throw new ArgumentException("Give at least two steps");

        var tasks = clean.Select(s => new { title = s, status = "NEW", parentTaskId = parent }).ToList();
        var created = await api.SendAsync(HttpMethod.Post, "/tasks/batch", new { tasks }, ct);
        return created.EnumerateArray().Select(ToolSupport.IdOf).ToList();
    }

    private async Task<JsonElement> FindOpenAsync(string title, CancellationToken ct)
    {
        var data = await api.GetAsync("/tasks", ct);
        return Match(data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Where(t => !IsCompleted(t)) : [], title);
    }

    /// <summary>One task by title: an exact match wins, otherwise a part of the title must point to exactly one.</summary>
    public static JsonElement Match(IEnumerable<JsonElement> tasks, string title)
    {
        var wanted = title.Trim();
        var list = tasks.ToList();

        var exact = list.Where(t => string.Equals(ToolSupport.Str(t, "title"), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var partial = list.Where(t => ToolSupport.Str(t, "title")?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count == 0) throw new BackendApiException(404, $"No open task matches '{wanted}'");

        throw new BackendApiException(409, "Several tasks match: " + string.Join(" | ", partial.Take(4).Select(t => ToolSupport.Str(t, "title"))) + ". Ask which one.");
    }

    private static object FullBody(JsonElement task, string? dueDate) => new
    {
        title = ToolSupport.Str(task, "title"),
        content = ToolSupport.Str(task, "content"),
        status = ToolSupport.Str(task, "status") ?? "NEW",
        orderId = ToolSupport.Str(task, "orderId"),
        category = ToolSupport.Str(task, "category"),
        dueDate,
    };

    private static bool IsCompleted(JsonElement t) => ToolSupport.Str(t, "status") == "COMPLETED";

    private static bool Has(JsonElement t, string name) => t.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool CompletedWithin(JsonElement t, int days) =>
        DateTime.TryParse(ToolSupport.Str(t, "completedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
        && at >= DateTime.UtcNow.AddDays(-days);
}
