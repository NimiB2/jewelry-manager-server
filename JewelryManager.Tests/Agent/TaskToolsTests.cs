using System.Text.Json;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Mcp;

namespace JewelryManager.Tests.Agent;

/// <summary>
/// The to-do tools, with the lessons of Natasha built in: finishing one task never touches another, a task that is
/// not found is an error and not a guess, and a failure never quietly turns into a different action.
/// </summary>
public class TaskToolsTests
{
    private static async Task<ToolOutcome> Run(Task<string> call) => ToolOutcome.Parse(await call);

    [Fact]
    public async Task Completing_one_task_changes_that_task_and_no_other()
    {
        var api = new FakeBackendApi();
        var money = api.AddTask("להזמין כסף");
        var photos = api.AddTask("לצלם טבעות");
        var site = api.AddTask("לעדכן את האתר");

        var outcome = await Run(new TaskTools(api).CompleteTask("להזמין כסף"));

        Assert.True(outcome.Ok);
        Assert.Equal("COMPLETED", money["status"]);
        Assert.Equal("NEW", photos["status"]);
        Assert.Equal("NEW", site["status"]);
        Assert.Equal(["לצלם טבעות", "לעדכן את האתר"], new[] { (string)photos["title"]!, (string)site["title"]! });
        Assert.Single(api.Calls, c => c.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task A_part_of_the_title_is_enough_when_it_points_to_one_task_and_two_matches_are_not_guessed()
    {
        var api = new FakeBackendApi();
        api.AddTask("לשלוח חשבוניות לרואה החשבון");
        api.AddTask("לשלוח הודעה ללקוחה");

        Assert.True((await Run(new TaskTools(api).CompleteTask("חשבוניות"))).Ok);

        var ambiguous = new FakeBackendApi();
        ambiguous.AddTask("לשלוח חשבוניות");
        ambiguous.AddTask("לשלוח הודעה");
        var outcome = await Run(new TaskTools(ambiguous).CompleteTask("לשלוח"));

        Assert.False(outcome.Ok);
        Assert.Contains("Several tasks match", outcome.ModelText);
        Assert.All(ambiguous.Tasks, t => Assert.Equal("NEW", t["status"]));
    }

    [Fact]
    public async Task A_task_that_is_not_there_is_an_error_and_nothing_else_is_tried()
    {
        var api = new FakeBackendApi();
        api.AddTask("להזמין כסף");

        var outcome = await Run(new TaskTools(api).CompleteTask("משהו אחר לגמרי"));

        Assert.False(outcome.Ok);
        Assert.DoesNotContain(api.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task A_finished_task_is_not_found_again_as_an_open_one()
    {
        var api = new FakeBackendApi();
        api.AddTask("להזמין כסף", status: "COMPLETED");

        Assert.False((await Run(new TaskTools(api).CompleteTask("להזמין כסף"))).Ok);

        var listed = await Run(new TaskTools(api).ListTasks());
        Assert.Contains("\"count\":0", listed.ModelText);
    }

    [Fact]
    public async Task The_list_shows_open_tasks_by_default_and_parked_ones_only_on_request()
    {
        var api = new FakeBackendApi();
        api.AddTask("פתוחה");
        api.AddTask("בצד", parked: DateTime.UtcNow);
        api.AddTask("גמורה", status: "COMPLETED");

        var open = (await Run(new TaskTools(api).ListTasks())).ModelText;
        Assert.Contains("פתוחה", open);
        Assert.DoesNotContain("בצד", open);
        Assert.DoesNotContain("גמורה", open);

        var parked = (await Run(new TaskTools(api).ListTasks("parked"))).ModelText;
        Assert.Contains("בצד", parked);
        Assert.DoesNotContain("פתוחה", parked);
    }

    [Fact]
    public async Task Adding_a_task_flags_a_big_one_for_a_breakdown_and_can_be_taken_back()
    {
        var api = new FakeBackendApi();
        var big = await Run(new TaskTools(api).AddTask("לבנות קמפיין לחנוכה", "2026-12-01"));
        var small = await Run(new TaskTools(api).AddTask("להזמין כסף"));

        Assert.Contains("\"suggestBreakdown\":true", big.ModelText);
        Assert.Contains("\"suggestBreakdown\":false", small.ModelText);
        Assert.Equal("/tasks/batch-delete", big.Undo!.Path);
        Assert.Equal("2026-12-01", api.Tasks[0]["dueDate"]);

        Assert.False((await Run(new TaskTools(api).AddTask("x"))).Ok);
        Assert.False((await Run(new TaskTools(api).AddTask("משימה", "בקרוב"))).Ok);
    }

    [Fact]
    public async Task A_breakdown_creates_small_steps_under_the_big_task_and_can_be_taken_back_as_a_whole()
    {
        var api = new FakeBackendApi();
        var parent = api.AddTask("לעדכן את האתר");
        var parentId = Guid.Parse((string)parent["id"]!);

        var outcome = await Run(new TaskTools(api).BreakDownTask(parentId, ["לבחור חמישה מוצרים", "לצלם אותם", "להעלות לאתר"]));

        Assert.True(outcome.Ok);
        var steps = api.Tasks.Where(t => (string?)t["parentTaskId"] == parentId.ToString()).ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal("/tasks/batch-delete", outcome.Undo!.Path);

        await api.SendAsync(HttpMethod.Post, outcome.Undo.Path, outcome.Undo.Body);
        Assert.Single(api.Tasks);

        Assert.False((await Run(new TaskTools(api).BreakDownTask(parentId, ["צעד אחד"]))).Ok);
    }

    [Fact]
    public async Task Setting_a_date_can_be_undone_both_when_there_was_one_and_when_there_was_not()
    {
        var api = new FakeBackendApi();
        api.AddTask("בלי תאריך");
        api.AddTask("עם תאריך", dueDate: "2026-12-01");

        var first = await Run(new TaskTools(api).SetTaskDue("בלי תאריך", "2026-12-10"));
        Assert.Equal("PATCH", first.Undo!.Method);
        Assert.Contains("/coach", first.Undo.Path);

        var second = await Run(new TaskTools(api).SetTaskDue("עם תאריך", "2026-12-20"));
        Assert.Equal("PUT", second.Undo!.Method);
        Assert.Equal("2026-12-01", ((JsonElement)second.Undo.Body!).GetProperty("dueDate").GetString());

        Assert.False((await Run(new TaskTools(api).SetTaskDue("בלי תאריך", "מחר"))).Ok);
    }

    [Fact]
    public async Task Setting_a_task_aside_and_bringing_it_back_are_each_the_undo_of_the_other()
    {
        var api = new FakeBackendApi();
        var task = api.AddTask("להזמין כסף");

        var parked = await Run(new TaskTools(api).ParkTask("להזמין כסף"));
        Assert.NotNull(task["parkedAt"]);
        Assert.Contains("unpark", JsonSerializer.Serialize(parked.Undo!.Body));

        var back = await Run(new TaskTools(api).UnparkTask("להזמין כסף"));
        Assert.Null(task["parkedAt"]);
        Assert.Contains("\"park\"", JsonSerializer.Serialize(back.Undo!.Body));
    }

    [Theory]
    [InlineData("GET", "/tasks")]
    [InlineData("POST", "/tasks")]
    [InlineData("POST", "/tasks/batch")]
    [InlineData("POST", "/tasks/batch-delete")]
    [InlineData("PUT", "/tasks/11111111-1111-1111-1111-111111111111")]
    [InlineData("PATCH", "/tasks/11111111-1111-1111-1111-111111111111/status")]
    [InlineData("PATCH", "/tasks/11111111-1111-1111-1111-111111111111/coach")]
    [InlineData("GET", "/analytics/sales")]
    [InlineData("GET", "/analytics/growth")]
    [InlineData("GET", "/analytics/sellers")]
    [InlineData("GET", "/analytics/discount-ceiling")]
    [InlineData("GET", "/analytics/materials")]
    [InlineData("POST", "/analytics/what-if")]
    public void The_agent_may_use_the_task_and_analysis_calls(string method, string path) =>
        Assert.True(AgentRouteGuard.IsAllowed(method, path));

    [Theory]
    [InlineData("DELETE", "/tasks/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/tasks")]
    [InlineData("POST", "/analytics/sales")]
    [InlineData("PUT", "/analytics/what-if")]
    [InlineData("GET", "/agent/users")]
    [InlineData("POST", "/agent/users")]
    public void The_agent_may_not_delete_tasks_or_manage_who_may_talk_to_it(string method, string path) =>
        Assert.False(AgentRouteGuard.IsAllowed(method, path));

    [Fact]
    public void Every_new_tool_is_classified_and_the_ones_that_wait_for_approval_ask_for_a_summary()
    {
        var names = McpToolCatalog.Create(new FakeBackendApi()).Select(t => t.ProtocolTool.Name).ToList();

        foreach (var expected in new[] { "list_tasks", "add_task", "complete_task", "break_down_task", "add_tasks", "sales_by_period", "growth_snapshot", "discount_ceiling", "what_if_metal" })
            Assert.Contains(expected, names);

        Assert.All(names, n => Assert.True(ToolPolicy.IsKnown(n), $"{n} has no entry in ToolPolicy"));
        Assert.Equal(ToolTier.Confirm, ToolPolicy.TierOf("break_down_task"));
        Assert.Equal(ToolTier.Immediate, ToolPolicy.TierOf("complete_task"));
        Assert.Equal(ToolTier.Read, ToolPolicy.TierOf("what_if_metal"));
        Assert.DoesNotContain(names, n => n.Contains("delete", StringComparison.OrdinalIgnoreCase));
    }
}
