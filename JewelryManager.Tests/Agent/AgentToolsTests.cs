using System.Text.Json;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Mcp;

namespace JewelryManager.Tests.Agent;

public class AgentToolsTests
{
    [Fact]
    public async Task GetOrder_hides_phone_salary_and_work_hours()
    {
        var tools = new OrderTools(new FakeBackendApi());

        var outcome = ToolOutcome.Parse(await tools.GetOrder(1042));

        Assert.True(outcome.Ok);
        Assert.Contains("1042", outcome.ModelText);
        Assert.Contains("1250", outcome.ModelText);
        foreach (var secret in new[] { "972500000000", "secret text", "salary", "laborHourRate", "workHours", "customerPhone" })
            Assert.DoesNotContain(secret, outcome.ModelText);
    }

    [Fact]
    public async Task AdvanceStage_runs_and_offers_the_way_back()
    {
        var api = new FakeBackendApi();
        var outcome = ToolOutcome.Parse(await new OrderTools(api).AdvanceStage(1042));

        Assert.True(outcome.Ok);
        Assert.Equal("PATCH", outcome.Undo!.Method);
        Assert.Equal($"/orders/{FakeBackendApi.OrderId}/stage", outcome.Undo.Path);
        Assert.Equal("ליטוש", ((JsonElement)outcome.Undo.Body!).GetProperty("stage").GetString());
    }

    [Fact]
    public async Task SetStatus_refuses_an_unknown_status_before_calling_the_api()
    {
        var api = new FakeBackendApi();
        var outcome = ToolOutcome.Parse(await new OrderTools(api).SetStatus(1042, "DELETED", "x"));

        Assert.False(outcome.Ok);
        Assert.DoesNotContain(api.Calls, c => c.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task CreateOrder_takes_the_price_from_the_system_and_can_be_undone()
    {
        var api = new FakeBackendApi();
        var outcome = ToolOutcome.Parse(await new OrderTools(api).CreateOrder([new OrderItemArg("טבעת בדיקה", 2)], "דנה בדיקה"));

        Assert.True(outcome.Ok);
        var post = Assert.Single(api.Calls, c => c.Method == HttpMethod.Post && c.Path == "/orders");
        Assert.Contains("\"unitPrice\":null", post.Body);
        Assert.Equal("DELETE", outcome.Undo!.Method);
    }

    [Fact]
    public async Task AddExpense_rejects_a_zero_amount()
    {
        var outcome = ToolOutcome.Parse(await new FinanceTools(new FakeBackendApi()).AddExpense("בדיקה", 0));
        Assert.False(outcome.Ok);
    }

    [Fact]
    public async Task AddExpense_creates_and_offers_to_delete_only_that_expense()
    {
        var api = new FakeBackendApi();
        var outcome = ToolOutcome.Parse(await new FinanceTools(api).AddExpense("בדיקה", 340, "ספק בדיקה"));

        Assert.True(outcome.Ok);
        Assert.Equal("/expenses/55555555-5555-5555-5555-555555555555", outcome.Undo!.Path);
    }

    [Fact]
    public async Task FinanceSummary_returns_totals_without_the_list_of_rows()
    {
        var outcome = ToolOutcome.Parse(await new FinanceTools(new FakeBackendApi()).FinanceSummary("2026-11"));

        Assert.Contains("netProfit", outcome.ModelText);
        Assert.DoesNotContain("חומרי גלם", outcome.ModelText);
    }

    [Fact]
    public async Task A_missing_order_becomes_a_readable_error_not_an_exception()
    {
        var api = new FakeBackendApi();
        api.OrderJson = api.OrderJson.Replace("\"number\":1042", "\"number\":7");

        var outcome = ToolOutcome.Parse(await new OrderTools(api).GetOrder(1042));
        Assert.False(outcome.Ok);
        Assert.Contains("1042", outcome.ModelText);
    }

    [Fact]
    public void Every_tool_the_server_offers_is_classified_and_none_can_delete()
    {
        var names = McpToolCatalog.Create(new FakeBackendApi()).Select(t => t.ProtocolTool.Name).ToList();

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True(ToolPolicy.IsKnown(n), $"{n} has no entry in ToolPolicy"));
        Assert.DoesNotContain(names, n => n.Contains("delete", StringComparison.OrdinalIgnoreCase) || n.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_mcp_server_and_client_talk_in_memory_and_run_a_tool()
    {
        await using var session = await McpToolSession.StartAsync(new FakeBackendApi(), CancellationToken.None);

        Assert.Contains(session.Tools, t => t.Name == "get_order");
        Assert.All(session.Tools, t => Assert.True(ToolPolicy.IsKnown(t.Name)));

        var outcome = ToolOutcome.Parse(await session.CallAsync("get_order", """{"orderNumber":1042}""", CancellationToken.None));
        Assert.True(outcome.Ok);
        Assert.Contains("דנה בדיקה", outcome.ModelText);
    }

    [Fact]
    public async Task Confirm_tools_ask_the_model_for_a_summary()
    {
        await using var session = await McpToolSession.StartAsync(new FakeBackendApi(), CancellationToken.None);

        foreach (var spec in session.Tools.Where(t => ToolPolicy.TierOf(t.Name) == ToolTier.Confirm))
            Assert.True(spec.InputSchema.GetProperty("properties").TryGetProperty("summary", out _), $"{spec.Name} has no summary field");
    }
}
