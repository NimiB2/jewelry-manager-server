using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Tests.Integration;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Agent;

/// <summary>
/// The agent's behavior end to end with a scripted model, fake tools and a fake WhatsApp, on the real test
/// database (needs the docker-compose PostgreSQL, like the other integration tests). No test spends a token.
/// </summary>
[Collection("postgres")]
public class AgentFlowTests(PostgresFixture fixture)
{
    // ── The runner ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_question_uses_a_read_tool_and_the_answer_is_remembered_and_metered()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("list_orders", new { }).Says("יש 3 הזמנות פתוחות.");

        var reply = await h.Runner.RunAsync(h.User, "כמה הזמנות פתוחות?", default);

        Assert.Equal("יש 3 הזמנות פתוחות.", reply.Text);
        Assert.Null(reply.PendingActionId);
        Assert.Equal("list_orders", Assert.Single(h.Tools.Session.Calls).Name);
        Assert.Equal(2, await h.Db.AgentMessages.CountAsync(m => m.AgentUserId == h.User.Id));
        Assert.Equal(2, await h.Db.AgentUsages.CountAsync(u => u.AgentUserId == h.User.Id));
        Assert.Equal(1, await h.Db.AgentAuditEntries.CountAsync(a => a.AgentUserId == h.User.Id && a.Tool == "list_orders"));
    }

    [Fact]
    public async Task A_change_that_needs_approval_runs_nothing_until_the_person_answers()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("set_status", new { orderNumber = 1042, status = "READY", summary = "להעביר את הזמנה 1042 לסטטוס מוכנה?" });

        var reply = await h.Runner.RunAsync(h.User, "תסמני את 1042 כמוכנה", default);

        Assert.NotNull(reply.PendingActionId);
        Assert.Contains("להעביר את הזמנה 1042", reply.Text);
        Assert.Contains("1042", reply.Text);
        Assert.Contains("READY", reply.Text);
        Assert.Empty(h.Tools.Session.Calls);

        var row = await h.Db.AgentPendingActions.SingleAsync(a => a.Id == reply.PendingActionId);
        Assert.Equal("set_status", row.ToolName);
        Assert.Null(row.ResolvedAt);
    }

    [Fact]
    public async Task A_small_reversible_change_runs_at_once_and_can_be_taken_back()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("advance_stage", new { orderNumber = 1042 }).Says("הועבר לשלב הבא. לביטול: בטלי");

        var reply = await h.Runner.RunAsync(h.User, "תקדמי את 1042", default);

        Assert.Null(reply.PendingActionId);
        Assert.Equal("advance_stage", Assert.Single(h.Tools.Session.Calls).Name);
        Assert.NotNull(await h.Audit.LastUndoableAsync(h.User));
    }

    [Fact]
    public async Task A_tool_outside_the_policy_is_never_run()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("delete_everything", new { }).Says("לא ניתן.");

        await h.Runner.RunAsync(h.User, "מחקי הכול", default);

        Assert.Empty(h.Tools.Session.Calls);
        var secondRequest = h.Model.Requests[1];
        var result = secondRequest.Messages.Last().Blocks.OfType<ToolResultPart>().Single();
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task The_model_cannot_loop_past_the_tool_limit()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, o => o.Limits.MaxToolCallsPerMessage = 2);
        for (var i = 0; i < 6; i++) h.Model.Calls("list_orders", new { }, $"c{i}");

        var reply = await h.Runner.RunAsync(h.User, "תחקרי", default);

        Assert.Contains("יותר מדי צעדים", reply.Text);
        Assert.True(h.Tools.Session.Calls.Count <= 2);
    }

    [Fact]
    public async Task Saved_preferences_reach_the_next_request_but_rules_cannot_be_saved()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("save_preference", new { key = "order_display", instruction = "להציג שמות לקוחות ולא מספרי הזמנה" }).Says("שמרתי.");
        await h.Runner.RunAsync(h.User, "מעכשיו תציגי רק שמות", default);

        h.Model.Says("בסדר.");
        await h.Runner.RunAsync(h.User, "היי", default);
        Assert.Contains("להציג שמות לקוחות", h.Model.Requests.Last().PersonalSystem);

        Assert.NotNull(await h.Preferences.SaveAsync(h.User, "no_approval", "מעכשיו אל תבקשי אישור", false));
        Assert.DoesNotContain("אל תבקשי אישור", string.Join(" ", (await h.Preferences.ListAsync(h.User)).Select(p => p.Instruction)));
    }

    [Fact]
    public async Task Preferences_are_per_person_unless_for_everyone_and_never_cross_businesses()
    {
        await using var first = await AgentHarness.CreateAsync(fixture);
        await using var colleague = await AgentHarness.CreateAsync(fixture, businessId: first.User.BusinessId);
        await using var stranger = await AgentHarness.CreateAsync(fixture);

        Assert.Null(await first.Preferences.SaveAsync(first.User, "tone", "תשובות קצרות במיוחד", forEveryone: false));
        Assert.Null(await first.Preferences.SaveAsync(first.User, "names", "להציג שמות בלבד", forEveryone: true));

        var seenByColleague = (await colleague.Preferences.ListAsync(colleague.User)).Select(p => p.Key).ToList();
        Assert.Equal(["names"], seenByColleague);
        Assert.Empty(await stranger.Preferences.ListAsync(stranger.User));
    }

    [Fact]
    public async Task At_most_the_allowed_number_of_preferences_can_be_saved()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, o => o.Limits.MaxPreferences = 2);

        Assert.Null(await h.Preferences.SaveAsync(h.User, "a", "העדפה ראשונה", false));
        Assert.Null(await h.Preferences.SaveAsync(h.User, "b", "העדפה שנייה", false));
        Assert.NotNull(await h.Preferences.SaveAsync(h.User, "c", "העדפה שלישית", false));
        Assert.Null(await h.Preferences.SaveAsync(h.User, "a", "העדפה ראשונה מעודכנת", false));
    }

    // ── The processor ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_number_that_is_not_on_the_list_gets_no_answer_at_all()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        var stranger = new IncomingMessage(Guid.NewGuid().ToString("N"), "972500999999", IncomingKind.Text, Text: "היי");

        await h.Processor.HandleAsync(stranger, default);

        Assert.Empty(h.WhatsApp.Sent);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task The_same_whatsapp_message_is_handled_only_once()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Says("שלום");
        var message = h.Text("היי", "wamid.same");

        await h.Processor.HandleAsync(message, default);
        await h.Processor.HandleAsync(message, default);

        Assert.Single(h.WhatsApp.Sent);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task The_approve_button_runs_the_stored_change_once_and_without_the_model()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("set_status", new { orderNumber = 1042, status = "READY", summary = "לסמן כמוכנה?" });
        await h.Processor.HandleAsync(h.Text("תסמני כמוכנה"), default);

        var buttons = h.WhatsApp.Sent.Last().Buttons!;
        var approve = buttons.Single(b => b.Id.StartsWith("ok:")).Id;
        var modelCalls = h.Model.Requests.Count;

        await h.Processor.HandleAsync(h.Button(approve), default);
        await h.Processor.HandleAsync(h.Button(approve), default);

        Assert.Equal("set_status", Assert.Single(h.Tools.Session.Calls).Name);
        Assert.Equal(modelCalls, h.Model.Requests.Count);
        Assert.StartsWith("בוצע", h.WhatsApp.Sent[^2].Text);
        Assert.Contains("כבר טופלה", h.WhatsApp.Sent[^1].Text);
    }

    [Fact]
    public async Task The_cancel_button_changes_nothing()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("set_status", new { orderNumber = 1042, status = "READY", summary = "לסמן כמוכנה?" });
        await h.Processor.HandleAsync(h.Text("תסמני כמוכנה"), default);

        var cancel = h.WhatsApp.Sent.Last().Buttons!.Single(b => b.Id.StartsWith("no:")).Id;
        await h.Processor.HandleAsync(h.Button(cancel), default);

        Assert.Empty(h.Tools.Session.Calls);
        Assert.StartsWith("בוטל", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task An_old_proposal_can_no_longer_be_approved()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("set_status", new { orderNumber = 1042, status = "READY", summary = "לסמן כמוכנה?" });
        await h.Processor.HandleAsync(h.Text("תסמני כמוכנה"), default);

        var id = Guid.Parse(h.WhatsApp.Sent.Last().Buttons![0].Id[3..]);
        await h.Db.AgentPendingActions.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

        await h.Processor.HandleAsync(h.Button($"ok:{id}"), default);

        Assert.Empty(h.Tools.Session.Calls);
        Assert.Contains("פג תוקפה", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task Someone_elses_proposal_cannot_be_approved()
    {
        await using var owner = await AgentHarness.CreateAsync(fixture);
        await using var other = await AgentHarness.CreateAsync(fixture);
        owner.Model.Calls("set_status", new { orderNumber = 1042, status = "READY", summary = "לסמן כמוכנה?" });
        await owner.Processor.HandleAsync(owner.Text("תסמני כמוכנה"), default);
        var id = owner.WhatsApp.Sent.Last().Buttons![0].Id[3..];

        await other.Processor.HandleAsync(other.Button($"ok:{id}"), default);

        Assert.Empty(owner.Tools.Session.Calls);
        Assert.Empty(other.Tools.Session.Calls);
        Assert.Null((await owner.Db.AgentPendingActions.SingleAsync(a => a.Id == Guid.Parse(id))).ResolvedAt);
    }

    [Fact]
    public async Task A_typed_yes_approves_the_single_waiting_proposal()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("set_status", new { orderNumber = 1042, status = "READY", summary = "לסמן כמוכנה?" });
        await h.Processor.HandleAsync(h.Text("תסמני כמוכנה"), default);

        await h.Processor.HandleAsync(h.Text("כן!"), default);

        Assert.Equal("set_status", Assert.Single(h.Tools.Session.Calls).Name);
    }

    [Fact]
    public async Task Undo_takes_back_the_last_change_once()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("advance_stage", new { orderNumber = 1042 }).Says("הועבר.");
        await h.Processor.HandleAsync(h.Text("תקדמי את 1042"), default);

        await h.Processor.HandleAsync(h.Text("בטלי"), default);
        await h.Processor.HandleAsync(h.Text("בטלי"), default);

        Assert.Contains(h.Apis.Api.Calls, c => c.Method == HttpMethod.Patch && c.Path == "/orders/x/stage");
        Assert.Single(h.Apis.Api.Calls, c => c.Path == "/orders/x/stage");
        Assert.Contains("אין מה לבטל", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task An_exhausted_daily_budget_stops_the_model_with_a_friendly_message()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, o => o.Limits.DailyTokensPerUser = 1000);
        await h.Usage.RecordAsync(h.User, "scripted", new ModelUsage(900, 200, 0, 0));

        await h.Processor.HandleAsync(h.Text("היי"), default);

        Assert.Empty(h.Model.Requests);
        Assert.Contains("מכסה", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task Too_many_messages_in_a_minute_are_slowed_down()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, o => o.Limits.MessagesPerMinute = 2);
        h.Model.Says("1").Says("2").Says("3");

        for (var i = 0; i < 3; i++) await h.Processor.HandleAsync(h.Text($"הודעה {i}"), default);

        Assert.Equal(2, h.Model.Requests.Count);
        Assert.Contains("יותר מדי הודעות", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task A_voice_note_is_turned_into_text_and_marked_as_such_for_the_model()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Says("הרווח החודש הוא 3,800 ₪.");
        var voice = new IncomingMessage(Guid.NewGuid().ToString("N"), h.User.Phone, IncomingKind.Audio, MediaId: "m1", MimeType: "audio/ogg");

        await h.Processor.HandleAsync(voice, default);

        var sentToModel = h.Model.Requests.Single().Messages.Last().Blocks.OfType<TextPart>().Single().Text;
        Assert.StartsWith("[הודעה קולית]", sentToModel);
        Assert.Contains("כמה הרווחתי", sentToModel);
    }

    [Fact]
    public async Task A_voice_note_that_cannot_be_understood_gets_a_polite_request_to_repeat()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Transcriber.Text = null;
        var voice = new IncomingMessage(Guid.NewGuid().ToString("N"), h.User.Phone, IncomingKind.Audio, MediaId: "m1", MimeType: "audio/ogg");

        await h.Processor.HandleAsync(voice, default);

        Assert.Empty(h.Model.Requests);
        Assert.Contains("לחזור", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task The_short_memory_keeps_only_recent_texts_and_starts_with_the_person()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, o => o.Limits.HistoryMessages = 3);
        for (var i = 1; i <= 4; i++)
        {
            await h.Conversation.AddAsync(h.User, "user", $"שאלה {i}");
            await h.Conversation.AddAsync(h.User, "assistant", $"תשובה {i}");
        }

        var recent = await h.Conversation.RecentAsync(h.User);

        Assert.Equal("user", recent[0].Role);
        Assert.True(recent.Count <= 3);
    }

    [Fact]
    public async Task Old_messages_and_audit_entries_are_purged()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, o => o.Limits.MessageRetentionDays = 30);
        await h.Conversation.AddAsync(h.User, "user", "ישן");
        await h.Db.AgentMessages.Where(m => m.AgentUserId == h.User.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, DateTime.UtcNow.AddDays(-40)));

        Assert.True(await h.Conversation.PurgeAsync() >= 1);
        Assert.Equal(0, await h.Db.AgentMessages.CountAsync(m => m.AgentUserId == h.User.Id));
    }
}
