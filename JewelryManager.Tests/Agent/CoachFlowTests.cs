using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Api.Features.Agent.Coach;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Mcp;
using JewelryManager.Tests.Integration;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Agent;

/// <summary>
/// The coach end to end on the test database, with a scripted model, fake tools and a fake WhatsApp:
/// reminders, the task ladder, breakdowns, the rationing, and above all that nothing finished comes back.
/// </summary>
[Collection("postgres")]
public class CoachFlowTests(PostgresFixture fixture)
{
    private static async Task WithClockAsync(DateTime localTime, Func<Task> body)
    {
        var israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), israel);
        var before = AgentClock.UtcNow;
        AgentClock.UtcNow = () => utc;
        try { await body(); }
        finally { AgentClock.UtcNow = before; }
    }

    private static async Task SwitchOnAsync(AgentHarness h, bool morning = false, bool review = false)
    {
        await h.Db.AgentUsers.Where(u => u.Id == h.User.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.CoachEnabled, true).SetProperty(u => u.MorningSummary, morning).SetProperty(u => u.WeeklyReview, review)
            .SetProperty(u => u.IntroStep, 99));
        h.User.CoachEnabled = true;
        h.User.MorningSummary = morning;
        h.User.WeeklyReview = review;
    }

    private static Guid IdOf(Dictionary<string, object?> task) => Guid.Parse((string)task["id"]!);

    // ── One timeline ──────────────────────────────────────────────────────────

    [Fact]
    public async Task What_the_agent_sends_on_its_own_is_part_of_the_conversation_her_answer_makes_sense_in()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () =>
        {
            Assert.True(await h.Outbox.SendAsync(h.User, "נראה שהמשימה נתקעה. לפרק אותה?", MessageClass.Coaching, CoachKind.Task));
        });

        h.Model.Says("בסדר, אפרק.");
        await h.Processor.HandleAsync(h.Text("כן"), default);

        var seen = h.Model.Requests.Single().Messages;
        Assert.Contains(seen, m => m.Blocks.OfType<TextPart>().Any(t => t.Text.Contains("נראה שהמשימה נתקעה")));
        Assert.Equal("coach", (await h.Db.AgentMessages.FirstAsync(m => m.AgentUserId == h.User.Id && m.Role == "assistant")).Source);
    }

    [Fact]
    public async Task Coaching_that_goes_unanswered_pauses_until_she_writes_and_her_message_ends_the_pause()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);

        await h.Db.AgentUsers.Where(u => u.Id == h.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CoachUnanswered, 2));
        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () =>
        {
            Assert.False((await h.Outbox.DecideAsync(h.User, MessageClass.Coaching, CoachKind.Task)).Allowed);
            Assert.True((await h.Outbox.DecideAsync(h.User, MessageClass.Scheduled, CoachKind.Morning)).Allowed);
        });

        h.Model.Says("היי!");
        await h.Processor.HandleAsync(h.Text("שלום"), default);
        Assert.Equal(0, (await h.Db.AgentUsers.AsNoTracking().FirstAsync(u => u.Id == h.User.Id)).CoachUnanswered);
    }

    [Fact]
    public async Task A_muted_topic_is_never_raised_and_can_be_brought_back()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await h.Ledger.MuteAsync(h.User, CoachKind.Task);

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () =>
        {
            Assert.False(await h.Outbox.SendAsync(h.User, "תזכורת", MessageClass.Scheduled, CoachKind.Task));
            Assert.True(await h.Ledger.IsMutedAsync(h.User, CoachKind.Task));
            await h.Ledger.UnmuteAsync(h.User, CoachKind.Task);
            Assert.True(await h.Outbox.SendAsync(h.User, "תזכורת", MessageClass.Scheduled, CoachKind.Task));
        });
    }

    // ── The task ladder, end to end ───────────────────────────────────────────

    [Fact]
    public async Task An_old_task_gets_one_gentle_reminder_and_not_a_second_one_the_same_day()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        h.Apis.Api.AddTask("להזמין כסף", updatedAt: DateTime.UtcNow.AddDays(-9));

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () =>
        {
            await h.Tasks.RunAsync(h.User, default);
            await h.Tasks.RunAsync(h.User, default);
        });

        var sent = Assert.Single(h.WhatsApp.Sent);
        Assert.Contains("להזמין כסף", sent.Text);
        Assert.Equal(["עשיתי", "דחי בשבוע", "לא עכשיו"], sent.Buttons!.Select(b => b.Title));
    }

    [Fact]
    public async Task Pressing_done_completes_the_task_closes_the_ledger_and_it_never_comes_back()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        var task = h.Apis.Api.AddTask("להזמין כסף", updatedAt: DateTime.UtcNow.AddDays(-9));

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        var done = h.WhatsApp.Sent.Single().Buttons!.Single(b => b.Title == "עשיתי").Id;

        await h.Processor.HandleAsync(h.Button(done), default);

        Assert.Equal("COMPLETED", task["status"]);
        Assert.Equal(CoachState.Done, (await h.Ledger.FindAsync(h.User, CoachLedger.TaskKey(IdOf(task))))!.State);

        // Weeks later, with the task still on the list: silence.
        h.WhatsApp.Sent.Clear();
        task["status"] = "NEW";
        task["updatedAt"] = DateTime.UtcNow.AddDays(-40).ToString("O");
        await WithClockAsync(new DateTime(2026, 12, 20, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        Assert.Empty(h.WhatsApp.Sent);
    }

    [Fact]
    public async Task Undoing_a_done_task_opens_it_again_for_reminders()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        var task = h.Apis.Api.AddTask("להזמין כסף", updatedAt: DateTime.UtcNow.AddDays(-9));

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        await h.Processor.HandleAsync(h.Button(h.WhatsApp.Sent.Single().Buttons![0].Id), default);
        await h.Processor.HandleAsync(h.Text("בטלי"), default);

        Assert.Equal("NEW", task["status"]);
        Assert.Equal(CoachState.Suggested, (await h.Ledger.FindAsync(h.User, CoachLedger.TaskKey(IdOf(task))))!.State);
    }

    [Fact]
    public async Task Not_now_snoozes_the_task_and_the_ladder_then_moves_on_instead_of_dropping_it()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        var task = h.Apis.Api.AddTask("להזמין כסף", updatedAt: DateTime.UtcNow.AddDays(-9));

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        await h.Processor.HandleAsync(h.Button(h.WhatsApp.Sent.Single().Buttons!.Single(b => b.Title == "לא עכשיו").Id), default);
        Assert.NotNull(task["snoozedUntil"]);

        // A week and a bit later the snooze is over and nothing was done: the next rung is the offer to break it down.
        var item = (await h.Ledger.FindAsync(h.User, CoachLedger.TaskKey(IdOf(task))))!;
        await h.Db.CoachItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastMentionedAt, DateTime.UtcNow.AddDays(-8)));
        h.Db.ChangeTracker.Clear();
        task["snoozedUntil"] = null;
        task["updatedAt"] = DateTime.UtcNow.AddDays(-17).ToString("O");
        await h.Db.AgentUsers.Where(u => u.Id == h.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CoachUnanswered, 0).SetProperty(u => u.LastOutboundAt, (DateTime?)null).SetProperty(u => u.LastCoachAt, (DateTime?)null).SetProperty(u => u.LastUserMessageAt, DateTime.UtcNow.AddDays(-3)));
        h.WhatsApp.Sent.Clear();

        await WithClockAsync(new DateTime(2026, 11, 19, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        var offer = Assert.Single(h.WhatsApp.Sent);
        Assert.Contains("לפרק", offer.Text);
        Assert.Contains(offer.Buttons!, b => b.Title == "כן, לפרק");
    }

    [Fact]
    public async Task Agreeing_to_a_breakdown_proposes_small_steps_and_creates_them_only_after_approval()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        var task = h.Apis.Api.AddTask("לעדכן את האתר", updatedAt: DateTime.UtcNow.AddDays(-3));

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        var yes = h.WhatsApp.Sent.Single().Buttons!.Single(b => b.Title == "כן, לפרק").Id;

        h.Model.Says("[\"לבחור חמישה מוצרים לעדכון\", \"לצלם אותם באור יום\", \"להעלות לאתר\"]");
        await h.Processor.HandleAsync(h.Button(yes), default);

        var proposal = h.WhatsApp.Sent[^1];
        Assert.Contains("1. לבחור חמישה מוצרים לעדכון", proposal.Text);
        Assert.Contains(proposal.Buttons!, b => b.Title == "אשר");
        Assert.Empty(h.Tools.Session.Calls);

        await h.Processor.HandleAsync(h.Button(proposal.Buttons!.Single(b => b.Title == "אשר").Id), default);
        var call = Assert.Single(h.Tools.Session.Calls);
        Assert.Equal("break_down_task", call.Name);
        Assert.Contains(IdOf(task).ToString(), call.Args);
    }

    [Fact]
    public async Task A_breakdown_the_model_cannot_produce_is_reported_without_creating_anything()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        h.Apis.Api.AddTask("לעדכן את האתר", updatedAt: DateTime.UtcNow.AddDays(-3));

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));
        h.Model.Says("אני לא בטוחה");
        await h.Processor.HandleAsync(h.Button(h.WhatsApp.Sent.Single().Buttons![0].Id), default);

        Assert.Contains("לא הצלחתי לפרק", h.WhatsApp.Sent[^1].Text);
        Assert.Empty(await h.Db.AgentPendingActions.Where(a => a.AgentUserId == h.User.Id).ToListAsync());
    }

    [Fact]
    public async Task A_task_nobody_answers_about_rests_quietly_and_is_never_nagged_or_lost()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        var task = h.Apis.Api.AddTask("להזמין כסף", updatedAt: DateTime.UtcNow.AddDays(-30));
        var item = await h.Ledger.EnsureAsync(h.User, CoachLedger.TaskKey(IdOf(task)), CoachKind.Task, "להזמין כסף", IdOf(task));
        await h.Db.CoachItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Stage, 3).SetProperty(i => i.LastMentionedAt, DateTime.UtcNow.AddDays(-5)));
        h.Db.ChangeTracker.Clear();

        await WithClockAsync(new DateTime(2026, 11, 11, 10, 0, 0), async () => await h.Tasks.RunAsync(h.User, default));

        Assert.Empty(h.WhatsApp.Sent);
        Assert.NotNull(task["parkedAt"]);
        Assert.Equal(CoachState.Parked, (await h.Ledger.FindAsync(h.User, CoachLedger.TaskKey(IdOf(task))))!.State);
        Assert.Equal("NEW", task["status"]);
    }

    [Fact]
    public async Task Setting_a_task_aside_by_button_keeps_it_listed_but_silent()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        var task = h.Apis.Api.AddTask("לעדכן את האתר", updatedAt: DateTime.UtcNow.AddDays(-3));
        var item = await h.Ledger.EnsureAsync(h.User, CoachLedger.TaskKey(IdOf(task)), CoachKind.Task, "לעדכן את האתר", IdOf(task));

        await h.Processor.HandleAsync(h.Button($"c:dpk:{item.Id}"), default);

        Assert.NotNull(task["parkedAt"]);
        Assert.Contains("בצד", h.WhatsApp.Sent.Last().Text);
        Assert.Equal(CoachState.Parked, (await h.Ledger.GetAsync(h.User, item.Id))!.State);
    }

    // ── Closing what she finished ─────────────────────────────────────────────

    [Fact]
    public async Task What_she_says_she_finished_is_closed_by_the_daily_check_and_never_suggested_again()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        var item = await h.Ledger.OfferAsync(h.User, "growth:advertising:2026-W45", CoachKind.Growth, "לפרסם פוסט עם המוצר הנמכר ביותר");
        Assert.NotNull(item);
        await h.Conversation.AddAsync(h.User, "user", "פרסמתי היום פוסט עם הטבעת הכי נמכרת");

        h.Model.Says("[1]");
        await h.Reconciler.RunAsync(h.User, default);

        Assert.Equal(CoachState.Done, (await h.Ledger.GetAsync(h.User, item!.Id))!.State);
        Assert.Null(await h.Ledger.OfferAsync(h.User, "growth:advertising:2026-W45", CoachKind.Growth, "לפרסם פוסט עם המוצר הנמכר ביותר"));
        Assert.Contains("לפרסם פוסט", string.Join(" ", await h.Ledger.ClosedTitlesAsync(h.User, 30)));
    }

    [Fact]
    public async Task The_daily_check_makes_no_call_when_there_is_nothing_to_compare()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await h.Conversation.AddAsync(h.User, "user", "שלום");
        await h.Reconciler.RunAsync(h.User, default);
        Assert.Empty(h.Model.Requests);

        await h.Ledger.OfferAsync(h.User, "growth:x:1", CoachKind.Growth, "משהו פתוח");
        await h.Db.AgentMessages.Where(m => m.AgentUserId == h.User.Id).ExecuteDeleteAsync();
        await h.Reconciler.RunAsync(h.User, default);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task A_refused_or_expired_offer_does_not_come_back_and_a_mentioned_one_is_offered_at_most_twice()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);

        var item = (await h.Ledger.OfferAsync(h.User, "season:hanukkah:2026", CoachKind.Season, "חנוכה"))!;
        await h.Ledger.MentionedAsync(item);
        Assert.NotNull(await h.Ledger.OfferAsync(h.User, "season:hanukkah:2026", CoachKind.Season, "חנוכה"));
        await h.Ledger.MentionedAsync(item);
        Assert.Null(await h.Ledger.OfferAsync(h.User, "season:hanukkah:2026", CoachKind.Season, "חנוכה"));

        var refused = (await h.Ledger.OfferAsync(h.User, "growth:customers:1", CoachKind.Growth, "ללקוחות"))!;
        await h.Ledger.SetStateAsync(refused, CoachState.Declined);
        Assert.Null(await h.Ledger.OfferAsync(h.User, "growth:customers:1", CoachKind.Growth, "ללקוחות"));
    }

    [Fact]
    public async Task Three_refusals_of_one_kind_put_it_to_sleep_for_the_focus_choices()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        for (var week = 1; week <= 3; week++)
        {
            var item = (await h.Ledger.EnsureAsync(h.User, $"growth:advertising:2026-W{40 + week}", CoachKind.Growth, "פרסום"));
            await h.Ledger.SetStateAsync(item, week == 3 ? CoachState.Expired : CoachState.Declined);
        }

        Assert.Equal(3, await h.Ledger.RecentRefusalsAsync(h.User, "growth:advertising:", 60));
        Assert.Equal(0, await h.Ledger.RecentRefusalsAsync(h.User, "growth:website:", 60));
    }

    // ── Morning, Friday, month end ────────────────────────────────────────────

    [Fact]
    public async Task The_morning_message_names_what_matters_today_once_a_day()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h, morning: true);
        h.Apis.Api.AddTask("להזמין כסף", dueDate: "2026-11-11");

        await WithClockAsync(new DateTime(2026, 11, 11, 9, 0, 0), async () =>
        {
            await h.Rhythm.MorningAsync(h.User, default);
            await h.Rhythm.MorningAsync(h.User, default);
        });

        var message = Assert.Single(h.WhatsApp.Sent);
        Assert.Contains("בוקר טוב", message.Text);
        Assert.Contains("משימה להיום: להזמין כסף", message.Text);
    }

    [Fact]
    public async Task The_morning_message_waits_for_morning_and_for_her_switch()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h, morning: false);
        await WithClockAsync(new DateTime(2026, 11, 11, 9, 0, 0), async () => await h.Rhythm.MorningAsync(h.User, default));
        Assert.Empty(h.WhatsApp.Sent);

        await SwitchOnAsync(h, morning: true);
        await WithClockAsync(new DateTime(2026, 11, 11, 6, 0, 0), async () => await h.Rhythm.MorningAsync(h.User, default));
        Assert.Empty(h.WhatsApp.Sent);
    }

    [Fact]
    public async Task On_Sunday_the_morning_message_names_the_focus_and_its_first_small_step()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h, morning: true);
        h.Db.CoachFocuses.Add(new CoachFocus { Id = Guid.NewGuid(), BusinessId = h.User.BusinessId, Area = FocusArea.NewJewelry, Reason = "x", FirstStep = "לשרטט רעיון אחד", StartedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();

        await WithClockAsync(new DateTime(2026, 11, 15, 9, 0, 0), async () => await h.Rhythm.MorningAsync(h.User, default));

        Assert.Contains("המיקוד השבוע: תכשיטים חדשים", h.WhatsApp.Sent.Single().Text);
        Assert.Contains("לשרטט רעיון אחד", h.WhatsApp.Sent.Single().Text);
    }

    [Fact]
    public async Task The_friday_review_offers_a_focus_for_next_week_and_the_choice_is_remembered()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h, review: true);

        await WithClockAsync(new DateTime(2026, 11, 13, 10, 0, 0), async () =>
        {
            await h.Rhythm.FridayAsync(h.User, default);
            await h.Rhythm.FridayAsync(h.User, default);
        });

        var review = Assert.Single(h.WhatsApp.Sent);
        Assert.Contains("סיכום השבוע", review.Text);
        Assert.Contains("מה המיקוד לשבוע הבא?", review.Text);
        Assert.InRange(review.Buttons!.Count, 2, 3);

        await WithClockAsync(new DateTime(2026, 11, 13, 10, 5, 0), async () => await h.Processor.HandleAsync(h.Button(review.Buttons![0].Id), default));
        var focus = await h.Db.CoachFocuses.AsNoTracking().SingleAsync(f => f.BusinessId == h.User.BusinessId);
        Assert.Equal(review.Buttons![0].Id["f:".Length..], focus.Area);
        Assert.Contains("המיקוד לשבוע הבא", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task The_friday_review_is_only_for_friday_and_only_if_she_asked_for_it()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h, review: true);
        await WithClockAsync(new DateTime(2026, 11, 12, 10, 0, 0), async () => await h.Rhythm.FridayAsync(h.User, default));
        Assert.Empty(h.WhatsApp.Sent);

        await SwitchOnAsync(h, review: false);
        await WithClockAsync(new DateTime(2026, 11, 13, 10, 0, 0), async () => await h.Rhythm.FridayAsync(h.User, default));
        Assert.Empty(h.WhatsApp.Sent);
    }

    [Fact]
    public async Task Month_end_lists_what_is_left_to_tidy_and_stays_silent_when_nothing_is()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);

        await WithClockAsync(new DateTime(2026, 11, 29, 10, 0, 0), async () => await h.Rhythm.MonthEndAsync(h.User, default));
        Assert.Contains("2 הוצאות בלי קבלה", h.WhatsApp.Sent.Single().Text);

        // Not in the middle of the month.
        await using var other = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(other);
        await WithClockAsync(new DateTime(2026, 11, 10, 10, 0, 0), async () => await other.Rhythm.MonthEndAsync(other.User, default));
        Assert.Empty(other.WhatsApp.Sent);
    }

    [Fact]
    public async Task A_monthly_duty_becomes_one_task_a_month()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        Assert.Null(await h.Rhythm.AddObligationAsync(h.User, "לשלוח חשבוניות לרואה החשבון", 5, default));

        await WithClockAsync(new DateTime(2026, 11, 3, 10, 0, 0), async () => await h.Rhythm.ObligationsAsync(h.User, default));
        Assert.Empty(h.Apis.Api.Tasks);

        await WithClockAsync(new DateTime(2026, 11, 6, 10, 0, 0), async () =>
        {
            await h.Rhythm.ObligationsAsync(h.User, default);
            await h.Rhythm.ObligationsAsync(h.User, default);
        });
        var created = Assert.Single(h.Apis.Api.Tasks);
        Assert.Equal("לשלוח חשבוניות לרואה החשבון", created["title"]);
        Assert.Equal("2026-11-05", created["dueDate"]);

        Assert.NotNull(await h.Rhythm.AddObligationAsync(h.User, "x", 5, default));
        Assert.NotNull(await h.Rhythm.AddObligationAsync(h.User, "משהו כלשהו", 40, default));
        Assert.True(await h.Rhythm.RemoveObligationAsync(h.User, "רואה החשבון", default));
    }

    // ── Occasions ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_occasion_is_asked_about_once_and_a_yes_brings_an_offer_above_the_profit_floor_with_an_ad_draft()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        await h.Occasions.SetAsync(h.User, "hanukkah", true, default);
        foreach (var other in new[] { "valentine", "tu_bav", "rosh_hashana", "passover", "family_day", "black_friday" })
            await h.Occasions.SetAsync(h.User, other, false, default);

        await WithClockAsync(new DateTime(2026, 11, 3, 10, 0, 0), async () =>
        {
            await h.Occasions.RunAsync(h.User, default);
            await h.Occasions.RunAsync(h.User, default);
        });

        var ask = Assert.Single(h.WhatsApp.Sent);
        Assert.Contains("חנוכה", ask.Text);
        var yes = ask.Buttons!.Single(b => b.Title == "כן, בואי נתכנן").Id;

        h.Model.Says("חנוכה מגיע, ואיתו מתנות שנשארות.");
        await h.Processor.HandleAsync(h.Button(yes), default);
        Assert.Contains("הצעה לחנוכה", h.WhatsApp.Sent.Last().Text);
        Assert.Contains("טיוטה לפוסט", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task Saying_no_to_an_occasion_means_not_this_year_and_it_is_not_raised_again()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        await h.Occasions.SetAsync(h.User, "hanukkah", true, default);
        foreach (var other in new[] { "valentine", "tu_bav", "rosh_hashana", "passover", "family_day", "black_friday" })
            await h.Occasions.SetAsync(h.User, other, false, default);

        await WithClockAsync(new DateTime(2026, 11, 3, 10, 0, 0), async () => await h.Occasions.RunAsync(h.User, default));
        await h.Processor.HandleAsync(h.Button(h.WhatsApp.Sent.Single().Buttons!.Single(b => b.Title == "לא השנה").Id), default);

        h.WhatsApp.Sent.Clear();
        await h.Db.AgentUsers.Where(u => u.Id == h.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastOutboundAt, (DateTime?)null).SetProperty(u => u.LastCoachAt, (DateTime?)null).SetProperty(u => u.CoachUnanswered, 0).SetProperty(u => u.LastUserMessageAt, DateTime.UtcNow.AddDays(-3)));
        await WithClockAsync(new DateTime(2026, 11, 20, 10, 0, 0), async () => await h.Occasions.RunAsync(h.User, default));
        Assert.Empty(h.WhatsApp.Sent);
    }

    // ── The first week ────────────────────────────────────────────────────────

    [Fact]
    public async Task After_the_first_message_is_answered_the_agent_asks_its_first_question_once()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, introStep: 0);
        h.Model.Says("שלום!").Says("שלום שוב");

        await h.Processor.HandleAsync(h.Text("היי"), default);
        await h.Processor.HandleAsync(h.Text("עוד משהו"), default);

        Assert.Equal(3, h.WhatsApp.Sent.Count);
        Assert.Equal("שלום!", h.WhatsApp.Sent[0].Text);
        Assert.Contains("איך תעדיפי שאכתוב", h.WhatsApp.Sent[1].Text);
        Assert.Equal("שלום שוב", h.WhatsApp.Sent[2].Text);
    }

    [Fact]
    public async Task The_answers_become_preferences_and_switches_and_the_questions_end()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, introStep: 0);
        h.Model.Says("שלום!");
        await h.Processor.HandleAsync(h.Text("היי"), default);

        await h.Processor.HandleAsync(h.Button("i:0:short"), default);
        await h.Processor.HandleAsync(h.Button("i:1:no"), default);
        await h.Processor.HandleAsync(h.Button("i:2:friday_saturday"), default);
        await h.Processor.HandleAsync(h.Button("i:3:all"), default);

        var user = await h.Db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == h.User.Id);
        Assert.Equal("friday_saturday", user.RestDays);
        Assert.True(user.MorningSummary && user.WeeklyReview && user.CoachEnabled && user.StoreOrderAlerts);
        Assert.True(user.IntroStep > IntroService.Steps);

        var saved = (await h.Preferences.ListAsync(h.User)).Select(p => p.Key).OrderBy(k => k).ToList();
        Assert.Equal(["emoji", "tone"], saved);
        Assert.Contains("זה הכול", h.WhatsApp.Sent.Last().Text);
    }

    [Fact]
    public async Task The_questions_can_be_skipped_and_are_not_asked_again()
    {
        await using var h = await AgentHarness.CreateAsync(fixture, introStep: 0);
        h.Model.Says("שלום!").Says("עוד");
        await h.Processor.HandleAsync(h.Text("היי"), default);
        await h.Processor.HandleAsync(h.Button("i:skip:x"), default);

        h.WhatsApp.Sent.Clear();
        await h.Processor.HandleAsync(h.Text("שוב"), default);
        Assert.Single(h.WhatsApp.Sent);
        Assert.Equal(IntroService.Skipped, (await h.Db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == h.User.Id)).IntroStep);
    }

    // ── Local tools of the coach ──────────────────────────────────────────────

    [Fact]
    public async Task The_model_can_mute_a_topic_set_rest_days_and_read_back_what_is_on_record()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await SwitchOnAsync(h);
        h.Model.Calls("mute_topic", new { topic = "season" }).Calls("set_rest_days", new { days = "saturday" }, "c2").Calls("list_coach_state", new { }, "c3").Says("בסדר.");

        await h.Runner.RunAsync(h.User, "תפסיקי לשאול על מועדים ואל תכתבי בשבת", default);

        Assert.True(await h.Ledger.IsMutedAsync(h.User, "season"));
        Assert.Equal("saturday", (await h.Db.AgentUsers.AsNoTracking().SingleAsync(u => u.Id == h.User.Id)).RestDays);
        var state = h.Model.Requests.Last().Messages.Last().Blocks.OfType<ToolResultPart>().Last().Content;
        Assert.Contains("muted: season", state);
        Assert.Contains("rest days: saturday", state);
    }

    [Fact]
    public async Task A_bad_topic_or_area_is_refused_without_changing_anything()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        h.Model.Calls("mute_topic", new { topic = "everything" }).Calls("set_focus", new { area = "nothing" }, "c2").Says("לא יכולה.");

        await h.Runner.RunAsync(h.User, "תשתיקי הכול", default);

        var results = h.Model.Requests.Last().Messages.Last().Blocks.OfType<ToolResultPart>().ToList();
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Equal(0, await h.Db.CoachItems.CountAsync(i => i.BusinessId == h.User.BusinessId));
        Assert.Equal(0, await h.Db.CoachFocuses.CountAsync(f => f.BusinessId == h.User.BusinessId));
    }

    [Fact]
    public async Task The_personal_context_lists_open_suggestions_and_what_is_closed_so_the_model_does_not_repeat_it()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await h.Ledger.OfferAsync(h.User, "growth:website:1", CoachKind.Growth, "להשלים פרטים לשלושה מוצרים");
        var done = (await h.Ledger.OfferAsync(h.User, "growth:ads:1", CoachKind.Growth, "לפרסם פוסט אחד"))!;
        await h.Ledger.SetStateAsync(done, CoachState.Done);
        h.Model.Says("בסדר.");

        await h.Runner.RunAsync(h.User, "מה עושים?", default);

        var personal = h.Model.Requests.Single().PersonalSystem;
        Assert.Contains("להשלים פרטים לשלושה מוצרים", personal);
        Assert.Contains("לפרסם פוסט אחד", personal);
        Assert.Contains("never suggest again", personal);
    }

    // ── Metals, with the market answering from a table ────────────────────────

    private static void ConfigureMetals(AgentHarness h)
    {
        h.Options.Metals.Enabled = true;
        h.Options.Metals.Sources =
        [
            new PriceSourceOptions { Name = "a", UrlTemplate = "https://a.test/{metal}", PricePath = "price" },
            new PriceSourceOptions { Name = "b", UrlTemplate = "https://b.test/{metal}", PricePath = "data.price" },
        ];
        h.Options.Metals.Fx = new FxSourceOptions { UrlTemplate = "https://fx.test/usd", RatePath = "rate" };
    }

    [Fact]
    public async Task Two_agreeing_sources_give_an_accepted_reading_and_everything_is_kept()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        ConfigureMetals(h);
        var http = new FakeHttpFactory();
        http.Answers["a.test/XAU"] = """{"price": 2000}""";
        http.Answers["b.test/XAU"] = """{"data":{"price": 2010}}""";
        http.Answers["a.test/XAG"] = """{"price": 25}""";
        http.Answers["b.test/XAG"] = """{"data":{"price": 31}}""";
        http.Answers["fx.test"] = """{"rate": 3.5}""";
        var service = MetalService(h, http);
        await h.Db.MetalPriceReadings.ExecuteDeleteAsync();

        await service.FetchAsync(default);

        var gold = await h.Db.MetalPriceReadings.AsNoTracking().Where(r => r.Metal == "gold" && r.Accepted).SingleAsync();
        Assert.Equal(2005m, gold.UsdPerOunce);
        Assert.Equal(MetalMath.IlsPerGram(2005, 3.5m), gold.IlsPerGram);

        // Silver: the two sources disagree, so nothing is accepted - but both raw answers are on record.
        Assert.False(await h.Db.MetalPriceReadings.AnyAsync(r => r.Metal == "silver" && r.Accepted));
        Assert.True(await h.Db.MetalPriceReadings.CountAsync(r => r.Metal == "silver") >= 3);
    }

    [Fact]
    public async Task One_source_alone_or_a_missing_exchange_rate_is_never_trusted()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        ConfigureMetals(h);
        var http = new FakeHttpFactory();
        http.Answers["a.test/XAU"] = """{"price": 2000}""";
        http.Answers["fx.test"] = """{"rate": 3.5}""";
        await h.Db.MetalPriceReadings.ExecuteDeleteAsync();

        await MetalService(h, http).FetchAsync(default);
        Assert.False(await h.Db.MetalPriceReadings.AnyAsync(r => r.Accepted));

        http.Answers["b.test/XAU"] = """{"data":{"price": 2001}}""";
        http.Answers["fx.test"] = """{"rate": 99}""";
        await MetalService(h, http).FetchAsync(default);
        Assert.False(await h.Db.MetalPriceReadings.AnyAsync(r => r.Accepted));
    }

    [Fact]
    public async Task The_index_starts_in_shadow_then_alerts_only_after_two_days_above_the_threshold_and_resets_when_she_updates()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        ConfigureMetals(h);
        h.Options.Metals.ShadowDays = 0;
        await h.Db.AgentUsers.Where(u => u.Id == h.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IntroStep, 99));
        await h.Db.MetalPriceReadings.ExecuteDeleteAsync();
        h.Apis.Api.MaterialsJson = """[{"name":"זהב 14K","pricePerGram":250},{"name":"תכשיט מיוחד","pricePerGram":10}]""";

        await AcceptReadingAsync(h, "gold", 2000);
        await WithClockAsync(new DateTime(2026, 11, 11, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));
        var map = await h.Db.MetalMappings.AsNoTracking().SingleAsync(m => m.BusinessId == h.User.BusinessId);
        Assert.Equal("gold", map.Metal);
        Assert.Equal(0.585m, map.Purity);
        Assert.Equal(250m, map.LastSettingsPrice);
        Assert.Empty(h.WhatsApp.Sent);

        // The market rises 6%: the first day only marks the crossing.
        await AcceptReadingAsync(h, "gold", 2120);
        await WithClockAsync(new DateTime(2026, 11, 12, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));
        Assert.Empty(h.WhatsApp.Sent);

        // The second day sends one message, with the sources and an expected price.
        await WithClockAsync(new DateTime(2026, 11, 13, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));
        var alert = Assert.Single(h.WhatsApp.Sent);
        Assert.Contains("מדד הזהב עלה 6%", alert.Text);
        Assert.Contains("לפי שני מקורות", alert.Text);
        Assert.Contains("265", alert.Text);

        // A third day does not repeat it.
        await WithClockAsync(new DateTime(2026, 11, 14, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));
        Assert.Single(h.WhatsApp.Sent);

        // She updates the price in the settings: the baseline is today's market and the crossing is forgotten.
        h.Apis.Api.MaterialsJson = """[{"name":"זהב 14K","pricePerGram":265}]""";
        await WithClockAsync(new DateTime(2026, 11, 15, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));
        var reset = await h.Db.MetalMappings.AsNoTracking().SingleAsync(m => m.BusinessId == h.User.BusinessId);
        Assert.Equal(265m, reset.LastSettingsPrice);
        Assert.Null(reset.FirstCrossedOn);
        Assert.Null(reset.AlertedOn);
    }

    [Fact]
    public async Task During_the_shadow_weeks_the_index_records_but_sends_nothing()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        ConfigureMetals(h);
        h.Options.Metals.ShadowDays = 14;
        await h.Db.MetalPriceReadings.ExecuteDeleteAsync();
        h.Apis.Api.MaterialsJson = """[{"name":"זהב 14K","pricePerGram":250}]""";

        await AcceptReadingAsync(h, "gold", 2000);
        await WithClockAsync(new DateTime(2026, 11, 11, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));
        await AcceptReadingAsync(h, "gold", 2300);
        foreach (var day in new[] { 12, 13, 14 })
            await WithClockAsync(new DateTime(2026, 11, day, 15, 0, 0), async () => await h.Metals.RunDailyAsync([h.User], default));

        Assert.Empty(h.WhatsApp.Sent);
    }

    [Fact]
    public async Task Accepting_the_move_makes_todays_market_the_new_baseline()
    {
        await using var h = await AgentHarness.CreateAsync(fixture);
        await h.Db.MetalPriceReadings.ExecuteDeleteAsync();
        await AcceptReadingAsync(h, "gold", 2000);
        h.Db.MetalMappings.Add(new MetalMapping
        {
            Id = Guid.NewGuid(), BusinessId = h.User.BusinessId, MaterialName = "זהב 14K", Metal = "gold", Purity = 0.585m,
            LastSettingsPrice = 250, BaselineSpotIlsPerGram = 100, BaselineAt = DateTime.UtcNow.AddDays(-30), FirstCrossedOn = new DateOnly(2026, 11, 1),
        });
        await h.Db.SaveChangesAsync();

        await h.Processor.HandleAsync(h.Button("m:x:gold"), default);
        var reply = h.WhatsApp.Sent.Last().Text;

        Assert.Contains("אתעלם", reply);
        var map = await h.Db.MetalMappings.AsNoTracking().SingleAsync(m => m.BusinessId == h.User.BusinessId);
        Assert.Equal(MetalMath.IlsPerGram(2000, 3.5m), map.BaselineSpotIlsPerGram);
        Assert.Null(map.FirstCrossedOn);
    }

    private static MetalIndexService MetalService(AgentHarness h, FakeHttpFactory http)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(h.Options);
        return new MetalIndexService(h.Db, new MetalSources(http, opts, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetalSources>.Instance),
            h.Apis, h.Outbox, new ProactiveLog(h.Db), h.WhatsApp, opts, Microsoft.Extensions.Logging.Abstractions.NullLogger<MetalIndexService>.Instance);
    }

    private static async Task AcceptReadingAsync(AgentHarness h, string metal, decimal usdPerOunce)
    {
        h.Db.MetalPriceReadings.Add(new MetalPriceReading
        {
            Id = Guid.NewGuid(), Metal = metal, Source = "agreed:test", UsdPerOunce = usdPerOunce, UsdIls = 3.5m,
            IlsPerGram = MetalMath.IlsPerGram(usdPerOunce, 3.5m), Accepted = true, At = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();
    }
}
