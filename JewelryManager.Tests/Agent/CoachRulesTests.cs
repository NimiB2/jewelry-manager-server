using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Api.Features.Agent.Coach;
using JewelryManager.Api.Features.Analytics;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Mcp;

namespace JewelryManager.Tests.Agent;

/// <summary>The coach's rules in plain code: when it may speak, when a task moves on, what the numbers say. No database.</summary>
public class CoachRulesTests
{
    // 2026-11-11 is a Wednesday.
    private static readonly DateTime Wednesday10 = new(2026, 11, 11, 10, 0, 0);

    private static GateInput Gate(MessageClass cls = MessageClass.Coaching, DateTime? now = null, string rest = "none", bool muted = false,
        DateTime? lastUser = null, DateTime? lastOut = null, DateTime? lastCoach = null, int unanswered = 0, int today = 0, int week = 0, bool fewer = false) =>
        new(cls, now ?? Wednesday10, rest, muted, lastUser, lastOut, lastCoach, unanswered, today, week, fewer);

    // ── The attention gate ────────────────────────────────────────────────────

    [Theory]
    [InlineData(7, 59, false)]
    [InlineData(8, 0, true)]
    [InlineData(20, 59, true)]
    [InlineData(21, 0, false)]
    public void Nothing_is_sent_at_night_whatever_kind_it_is(int hour, int minute, bool allowed)
    {
        foreach (var cls in Enum.GetValues<MessageClass>())
            Assert.Equal(allowed, AttentionGate.Decide(Gate(cls, now: new DateTime(2026, 11, 11, hour, minute, 0))).Allowed);
    }

    [Fact]
    public void Rest_days_are_kept_for_every_kind_of_message()
    {
        var saturday = new DateTime(2026, 11, 14, 11, 0, 0);
        var fridayNoon = new DateTime(2026, 11, 13, 12, 0, 0);
        var fridayEvening = new DateTime(2026, 11, 13, 15, 0, 0);

        Assert.False(AttentionGate.Decide(Gate(MessageClass.Scheduled, saturday, rest: "saturday")).Allowed);
        Assert.False(AttentionGate.Decide(Gate(MessageClass.Alert, saturday, rest: "friday_saturday")).Allowed);
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Scheduled, fridayNoon, rest: "friday_saturday")).Allowed);
        Assert.False(AttentionGate.Decide(Gate(MessageClass.Scheduled, fridayEvening, rest: "friday_saturday")).Allowed);
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Scheduled, fridayEvening, rest: "saturday")).Allowed);
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Scheduled, saturday, rest: "none")).Allowed);
    }

    [Fact]
    public void A_muted_topic_stays_silent_even_for_alerts_and_scheduled_messages()
    {
        foreach (var cls in Enum.GetValues<MessageClass>())
            Assert.False(AttentionGate.Decide(Gate(cls, muted: true)).Allowed);
    }

    [Fact]
    public void Coaching_waits_after_she_has_written_today_or_is_writing_now()
    {
        Assert.False(AttentionGate.Decide(Gate(lastUser: Wednesday10.AddHours(-3))).Allowed);
        Assert.False(AttentionGate.Decide(Gate(now: new DateTime(2026, 11, 11, 14, 0, 0), lastUser: new DateTime(2026, 11, 11, 9, 0, 0))).Allowed);
        Assert.True(AttentionGate.Decide(Gate(lastUser: Wednesday10.AddDays(-1))).Allowed);
        Assert.False(AttentionGate.Decide(Gate(lastUser: Wednesday10.AddMinutes(-10))).Allowed);

        // A reminder she asked for (scheduled) is not held back by a chat earlier today.
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Scheduled, lastUser: Wednesday10.AddHours(-1))).Allowed);
    }

    [Fact]
    public void Coaching_keeps_clear_of_a_message_that_just_went_out()
    {
        Assert.False(AttentionGate.Decide(Gate(lastOut: Wednesday10.AddHours(-2))).Allowed);
        Assert.True(AttentionGate.Decide(Gate(lastOut: Wednesday10.AddHours(-4))).Allowed);
    }

    [Fact]
    public void Coaching_is_rationed_by_day_and_by_week()
    {
        Assert.False(AttentionGate.Decide(Gate(today: 1)).Allowed);
        Assert.False(AttentionGate.Decide(Gate(week: 3)).Allowed);
        Assert.True(AttentionGate.Decide(Gate(week: 2)).Allowed);

        // "פחות הודעות" halves the week.
        Assert.False(AttentionGate.Decide(Gate(week: 1, fewer: true)).Allowed);

        // Alerts and scheduled messages are not counted against the ideas budget.
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Alert, today: 5, week: 20)).Allowed);
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Scheduled, today: 5, week: 20)).Allowed);
    }

    [Fact]
    public void Silence_is_never_turned_into_pressure()
    {
        // Two ideas with no answer: the coach pauses until she writes. Alerts and her own schedule go on.
        Assert.False(AttentionGate.Decide(Gate(unanswered: 2)).Allowed);
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Scheduled, unanswered: 5)).Allowed);
        Assert.True(AttentionGate.Decide(Gate(MessageClass.Alert, unanswered: 5)).Allowed);

        // One unanswered idea doubles the wait before the next.
        Assert.False(AttentionGate.Decide(Gate(lastCoach: Wednesday10.AddHours(-30), unanswered: 1)).Allowed);
        Assert.True(AttentionGate.Decide(Gate(lastCoach: Wednesday10.AddHours(-49), unanswered: 1)).Allowed);
        Assert.False(AttentionGate.Decide(Gate(lastCoach: Wednesday10.AddHours(-20))).Allowed);
        Assert.True(AttentionGate.Decide(Gate(lastCoach: Wednesday10.AddHours(-25))).Allowed);
    }

    // ── The task ladder ───────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static TaskView Task(string title = "להזמין כסף", int ageDays = 0, DateOnly? due = null, int defer = 0, Guid? parent = null,
        bool done = false, bool auto = false, DateTime? snoozed = null, DateTime? parked = null) =>
        new(Guid.NewGuid(), title, done, auto, Now.AddDays(-ageDays - 1), Now.AddDays(-ageDays), due, parent, snoozed, defer, parked);

    private static CoachItem Item(int stage, int mentionedDaysAgo, string state = CoachState.Suggested) =>
        new() { Stage = stage, State = state, LastMentionedAt = Now.AddDays(-mentionedDaysAgo), Key = "k" };

    [Fact]
    public void A_fresh_task_is_left_alone_and_an_old_one_gets_a_gentle_reminder()
    {
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(ageDays: 3), null, Now, Today));
        Assert.Equal(LadderStep.RemindStale, TaskLadder.Decide(Task(ageDays: 8), null, Now, Today));
    }

    [Fact]
    public void A_task_with_a_due_date_is_reminded_when_the_date_comes()
    {
        Assert.Equal(LadderStep.RemindDue, TaskLadder.Decide(Task(due: Today), null, Now, Today));
        Assert.Equal(LadderStep.RemindDue, TaskLadder.Decide(Task(due: Today.AddDays(-2)), null, Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(due: Today.AddDays(2)), null, Now, Today));
    }

    [Fact]
    public void A_big_sounding_or_twice_postponed_task_is_offered_a_breakdown_early()
    {
        Assert.Equal(LadderStep.OfferBreakdown, TaskLadder.Decide(Task("לעדכן את האתר", ageDays: 2), null, Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task("לעדכן את האתר", ageDays: 0), null, Now, Today));
        Assert.Equal(LadderStep.OfferBreakdown, TaskLadder.Decide(Task(ageDays: 1, defer: 2), null, Now, Today));
    }

    [Fact]
    public void An_ignored_task_climbs_the_ladder_and_then_rests_instead_of_vanishing()
    {
        var task = Task(ageDays: 12);

        Assert.Equal(LadderStep.None, TaskLadder.Decide(task, Item(1, 2), Now, Today));
        Assert.Equal(LadderStep.OfferBreakdown, TaskLadder.Decide(task, Item(1, 3), Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(task, Item(2, 1), Now, Today));
        Assert.Equal(LadderStep.AskDecision, TaskLadder.Decide(task, Item(2, 3), Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(task, Item(3, 2), Now, Today));
        Assert.Equal(LadderStep.Park, TaskLadder.Decide(task, Item(3, 3), Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(task, Item(4, 30, CoachState.Parked), Now, Today));
    }

    [Fact]
    public void Anything_she_does_to_a_task_starts_the_ladder_again()
    {
        var touched = new TaskView(Guid.NewGuid(), "להזמין כסף", false, false, Now.AddDays(-20), Now.AddDays(-1), null, null, null, 0, null);
        Assert.Equal(LadderStep.None, TaskLadder.Decide(touched, Item(3, 5), Now, Today));
    }

    [Theory]
    [InlineData(CoachState.Done)]
    [InlineData(CoachState.Declined)]
    [InlineData(CoachState.Parked)]
    [InlineData(CoachState.Expired)]
    public void A_task_the_ledger_has_closed_is_never_raised_again(string state) =>
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(ageDays: 30, due: Today.AddDays(-5)), Item(0, 40, state), Now, Today));

    [Fact]
    public void Finished_automatic_snoozed_and_parked_tasks_are_left_alone()
    {
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(ageDays: 30, done: true), null, Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(ageDays: 30, auto: true), null, Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(ageDays: 30, snoozed: Now.AddDays(2)), null, Now, Today));
        Assert.Equal(LadderStep.None, TaskLadder.Decide(Task(ageDays: 30, parked: Now.AddDays(-3)), null, Now, Today));
        Assert.Equal(LadderStep.RemindStale, TaskLadder.Decide(Task(ageDays: 30, snoozed: Now.AddDays(-1)), null, Now, Today));
    }

    [Fact]
    public void A_small_step_waits_until_its_big_task_has_had_a_quiet_week()
    {
        var step = Task("לבחור 5 מוצרים", ageDays: 9, parent: Guid.NewGuid());
        Assert.Equal(LadderStep.None, TaskLadder.Decide(step, null, Now, Today, parentLastMentionedUtc: Now.AddDays(-2)));
        Assert.Equal(LadderStep.RemindStale, TaskLadder.Decide(step, null, Now, Today, parentLastMentionedUtc: Now.AddDays(-10)));

        // A step that was reminded and ignored is asked about plainly, not broken down again.
        Assert.Equal(LadderStep.AskDecision, TaskLadder.Decide(step, Item(1, 4), Now, Today));
    }

    // ── The ledger's second line of defence ───────────────────────────────────

    [Fact]
    public void A_new_idea_that_repeats_something_closed_is_recognized()
    {
        string[] closed = ["לצלם את הטבעות החדשות באור יום", "לשלוח הודעת תודה ללקוחות"];

        Assert.True(CoachLedger.LooksLikeAny("לצלם טבעות חדשות באור יום", closed));
        Assert.True(CoachLedger.LooksLikeAny("הודעת תודה ללקוחות לשלוח", closed));
        Assert.False(CoachLedger.LooksLikeAny("לעדכן את מחירי הזהב בהגדרות", closed));
        Assert.False(CoachLedger.LooksLikeAny("", closed));
    }

    [Fact]
    public void Task_wording_tells_a_big_task_from_a_small_one()
    {
        Assert.True(TaskHeuristics.LooksBig("לבנות קמפיין לחנוכה"));
        Assert.True(TaskHeuristics.LooksBig("לסדר את האתר"));
        Assert.True(TaskHeuristics.LooksBig(new string('א', 60)));
        Assert.False(TaskHeuristics.LooksBig("להזמין כסף"));
    }

    // ── Occasions ─────────────────────────────────────────────────────────────

    [Fact]
    public void Occasion_dates_follow_the_hebrew_and_gregorian_calendars()
    {
        Assert.Equal(new DateOnly(2026, 9, 12), OccasionCalendar.NextOnOrAfter("rosh_hashana", new DateOnly(2026, 8, 1)));
        Assert.Equal(new DateOnly(2026, 12, 5), OccasionCalendar.NextOnOrAfter("hanukkah", new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2027, 4, 22), OccasionCalendar.NextOnOrAfter("passover", new DateOnly(2027, 1, 1)));
        Assert.Equal(new DateOnly(2026, 7, 29), OccasionCalendar.NextOnOrAfter("tu_bav", new DateOnly(2026, 7, 1)));
        Assert.Equal(new DateOnly(2026, 11, 27), OccasionCalendar.NextOnOrAfter("black_friday", new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2027, 2, 14), OccasionCalendar.NextOnOrAfter("valentine", new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void Every_occasion_has_a_date_in_every_year_and_a_name()
    {
        foreach (var occasion in OccasionCalendar.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(occasion.Name));
            for (var year = 2024; year <= 2032; year++)
                Assert.NotEmpty(OccasionCalendar.Occurrences(occasion.Code, year, year));
        }
    }

    [Fact]
    public void An_occasion_that_was_busier_for_her_ranks_higher_and_too_little_history_says_nothing()
    {
        var today = new DateOnly(2026, 11, 11);
        var days = new List<(DateOnly, int)>();
        for (var d = today.AddYears(-4); d < today; d = d.AddDays(7)) days.Add((d, 1));

        // Three extra orders in each of the three weeks before every Hanukkah.
        foreach (var hanukkah in OccasionCalendar.Occurrences("hanukkah", 2022, 2025))
            for (var i = 1; i <= 15; i++) days.Add((hanukkah.AddDays(-i), 2));

        var hanukkahLift = OccasionCalendar.Lift("hanukkah", days, today);
        var valentineLift = OccasionCalendar.Lift("valentine", days, today);
        Assert.True(hanukkahLift > valentineLift);
        Assert.Equal("hanukkah", OccasionCalendar.DefaultChoice(days, today)[0]);

        Assert.Null(OccasionCalendar.Lift("hanukkah", [(today.AddDays(-30), 2)], today));
        Assert.Equal(["hanukkah", "valentine", "tu_bav"], OccasionCalendar.DefaultChoice([], today));
    }

    [Theory]
    [InlineData(40, 0, CoachState.Suggested, 0, 0, OccasionStep.Ask)]
    [InlineData(60, 0, CoachState.Suggested, 0, 0, OccasionStep.None)]
    [InlineData(5, 0, CoachState.Suggested, 0, 0, OccasionStep.None)]
    [InlineData(30, 1, CoachState.Suggested, 1, 8, OccasionStep.Remind)]
    [InlineData(30, 1, CoachState.Suggested, 1, 2, OccasionStep.None)]
    [InlineData(30, 1, CoachState.Suggested, 2, 20, OccasionStep.None)]
    [InlineData(15, 2, CoachState.Accepted, 2, 5, OccasionStep.Checklist)]
    [InlineData(40, 2, CoachState.Accepted, 2, 5, OccasionStep.None)]
    [InlineData(-4, 3, CoachState.Accepted, 3, 20, OccasionStep.Results)]
    [InlineData(15, 0, CoachState.Declined, 1, 20, OccasionStep.None)]
    public void An_occasion_gets_only_its_few_touches_at_the_right_time(int daysUntil, int stage, string state, int mentions, int mentionedDaysAgo, OccasionStep expected) =>
        Assert.Equal(expected, OccasionTimeline.Decide(daysUntil, stage, state, mentions, Now.AddDays(-mentionedDaysAgo), Now));

    // ── Focus ideas ───────────────────────────────────────────────────────────

    private static GrowthSnapshot Snapshot(int last30 = 10, decimal prior = 10, int? sinceNew = 20, int needing = 0, decimal repeat = 0.3m) =>
        new(last30, prior, 800, repeat, 40, needing, sinceNew, 0.4m, "זהב", 0.5m);

    [Fact]
    public void Focus_ideas_come_from_signals_and_always_number_three()
    {
        var dropping = GrowthAdvisor.Options(Snapshot(last30: 4, prior: 10), 0, null);
        Assert.Equal(FocusArea.Advertising, dropping[0].Area);
        Assert.Equal(3, dropping.Count);
        Assert.All(dropping, o => Assert.False(string.IsNullOrWhiteSpace(o.FirstStep)));

        var occasion = GrowthAdvisor.Options(Snapshot(), 0, ("חנוכה", 30));
        Assert.Equal(FocusArea.Occasion, occasion[0].Area);

        var quiet = GrowthAdvisor.Options(Snapshot(), 0, null);
        Assert.Equal(3, quiet.Count);
        Assert.Equal(quiet.Count, quiet.Select(o => o.Area).Distinct().Count());
    }

    [Fact]
    public void A_kind_she_keeps_refusing_is_left_out_of_the_choices()
    {
        var options = GrowthAdvisor.Options(Snapshot(last30: 4, prior: 10), 0, null, new HashSet<string> { FocusArea.Advertising });
        Assert.DoesNotContain(options, o => o.Area == FocusArea.Advertising);
        Assert.Equal(3, options.Count);
    }

    // ── Metals ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_market_price_is_converted_to_shekels_per_gram()
    {
        Assert.Equal(225.0552m, MetalMath.IlsPerGram(2000, 3.5m), 3);
        Assert.Equal(1.8993m, MetalMath.Premium(250, 225.0552m, 0.585m), 3);
        Assert.Equal(0, MetalMath.Premium(250, 0, 0.585m));
    }

    [Fact]
    public void Two_sources_must_agree_or_the_reading_is_thrown_away()
    {
        Assert.Equal(2010m, MetalMath.CrossCheck(2000, 2020, 1.5m));
        Assert.Null(MetalMath.CrossCheck(2000, 2100, 1.5m));
        Assert.Null(MetalMath.CrossCheck(0, 2000, 1.5m));

        Assert.Equal(2005m, MetalMath.Consensus([2000, 2010, 2500], 1.5m));
        Assert.Null(MetalMath.Consensus([2000, 2500], 1.5m));
        Assert.Null(MetalMath.Consensus([2000], 1.5m));
    }

    [Fact]
    public void An_alert_needs_two_days_and_is_sent_once_per_crossing()
    {
        var today = new DateOnly(2026, 11, 11);
        var yesterday = today.AddDays(-1);

        Assert.Equal(IndexAction.None, MetalMath.Evaluate(1.5m, 4, null, null, today));
        Assert.Equal(IndexAction.MarkCrossing, MetalMath.Evaluate(4.5m, 4, null, null, today));
        Assert.Equal(IndexAction.None, MetalMath.Evaluate(4.5m, 4, today, null, today));
        Assert.Equal(IndexAction.Alert, MetalMath.Evaluate(4.5m, 4, yesterday, null, today));
        Assert.Equal(IndexAction.Alert, MetalMath.Evaluate(-5m, 4, yesterday, null, today));
        Assert.Equal(IndexAction.None, MetalMath.Evaluate(4.5m, 4, yesterday, yesterday, today));

        // Between 80% and 100% of the threshold nothing changes; well below it the crossing is forgotten.
        Assert.Equal(IndexAction.None, MetalMath.Evaluate(3.5m, 4, yesterday, null, today));
        Assert.Equal(IndexAction.ClearCrossing, MetalMath.Evaluate(1m, 4, yesterday, yesterday, today));
    }

    [Theory]
    [InlineData("זהב 14K", "gold", 0.585)]
    [InlineData("זהב 18", "gold", 0.75)]
    [InlineData("14K gold", "gold", 0.585)]
    [InlineData("כסף 925", "silver", 0.925)]
    [InlineData("כסף", "silver", 0.925)]
    public void A_material_name_tells_its_metal_and_purity(string name, string metal, double purity)
    {
        var guess = MetalMath.Guess(name);
        Assert.NotNull(guess);
        Assert.Equal(metal, guess!.Value.Metal);
        Assert.Equal((decimal)purity, guess.Value.Purity);
    }

    [Theory]
    [InlineData("פלטינה")]
    [InlineData("זהב")]
    [InlineData("זהב 13K")]
    public void An_unclear_material_is_left_for_her_to_explain(string name) => Assert.Null(MetalMath.Guess(name));

    [Fact]
    public void A_price_is_found_in_the_answer_by_its_path()
    {
        Assert.Equal(123.5m, JsonPath.ReadNumber("""{"data":{"rates":[{"price":"123.5"}]}}""", "data.rates.0.price"));
        Assert.Equal(7m, JsonPath.ReadNumber("""{"price":7}""", "price"));
        Assert.Null(JsonPath.ReadNumber("""{"price":7}""", "cost"));
        Assert.Null(JsonPath.ReadNumber("not json", "price"));
    }

    [Fact]
    public void The_what_if_answer_is_told_in_plain_words()
    {
        var json = JsonDocument.Parse("""
            {"products":12,"averageChangePercent":3.2,"byType":[{"type":"טבעת","products":6,"averageChangePercent":4.1}],
             "newlyBelowProfitFloor":2,"newlyBelowExamples":["טבעת בדיקה","צמיד בדיקה"]}
            """).RootElement;

        var text = MetalIndexService.DescribeWhatIf(json, ["זהב 14K"]);
        Assert.Contains("+3.2%", text);
        Assert.Contains("12 מוצרים", text);
        Assert.Contains("טבעת +4.1%", text);
        Assert.Contains("2 מוצרים יירדו מתחת לרצפת הרווח", text);
        Assert.Contains("אני לא משנה אותם", text);
    }

    // ── Numbers behind the advice ─────────────────────────────────────────────

    [Fact]
    public void The_largest_safe_discount_lands_exactly_on_the_profit_floor()
    {
        const decimal cost = 100, vat = 0.18m, card = 0.02m, floor = 30;
        var minimum = ProfitMath.MinPriceForFloor(cost, vat, card, floor)!.Value;

        Assert.Equal(0.30m, ProfitMath.RateAt(minimum, cost, vat, card), 3);
        Assert.InRange(ProfitMath.MaxDiscountPercent(200, cost, vat, card, floor), 12.7m, 12.9m);
        Assert.Equal(0, ProfitMath.MaxDiscountPercent(150, cost, vat, card, floor));
        Assert.Null(ProfitMath.MinPriceForFloor(cost, vat, 0.9m, floor));
    }

    [Fact]
    public void Sales_are_grouped_by_the_start_of_the_week_or_month()
    {
        var wednesday = new DateOnly(2026, 11, 11);
        Assert.Equal(new DateOnly(2026, 11, 9), AnalyticsService.PeriodStart(wednesday, "week"));
        Assert.Equal(new DateOnly(2026, 11, 9), AnalyticsService.PeriodStart(new DateOnly(2026, 11, 15), "week"));
        Assert.Equal(new DateOnly(2026, 11, 1), AnalyticsService.PeriodStart(wednesday, "month"));
        Assert.Equal(wednesday, AnalyticsService.PeriodStart(wednesday, "day"));
    }

    [Fact]
    public void This_week_and_last_week_are_read_from_the_weekly_rows()
    {
        var rows = JsonDocument.Parse("""
            [{"period":"2026-11-02","orders":4,"revenue":3000},{"period":"2026-11-09","orders":3,"revenue":2400.5}]
            """).RootElement;

        var (orders, revenue, last) = RhythmService.WeekFigures(rows, new DateOnly(2026, 11, 9));
        Assert.Equal((3, 2400.5m, 3000m), (orders, revenue, last));
        Assert.Equal((0, 0m, 0m), RhythmService.WeekFigures(default, new DateOnly(2026, 11, 9)));
    }

    // ── Reading the model's answers ───────────────────────────────────────────

    [Fact]
    public void Breakdown_steps_are_read_from_the_answer_and_checked()
    {
        Assert.Equal(["לפתוח את הדף", "לרשום שלושה דברים", "לבחור אחד"],
            BreakdownService.ParseSteps("הנה: [\"1. לפתוח את הדף\", \"- לרשום שלושה דברים\", \"לבחור אחד\"]"));
        Assert.Null(BreakdownService.ParseSteps("[\"צעד אחד בלבד\"]"));
        Assert.Null(BreakdownService.ParseSteps("[\"א\",\"ב\",\"ג\",\"ד\",\"ה\",\"ו\",\"ז\"]"));
        Assert.Null(BreakdownService.ParseSteps("אין כאן רשימה"));
        Assert.Null(BreakdownService.ParseSteps("[not json]"));
    }

    [Fact]
    public void Item_numbers_from_the_reconciler_are_checked_against_the_list()
    {
        Assert.Equal([1, 3], DoneReconciler.ParseNumbers("[1, 3, 9, 3, 0]", 4));
        Assert.Empty(DoneReconciler.ParseNumbers("[]", 4));
        Assert.Empty(DoneReconciler.ParseNumbers("nothing", 4));
    }

    // ── The first week's questions ────────────────────────────────────────────

    [Fact]
    public void The_first_week_asks_four_short_questions_with_buttons_that_fit_whatsapp()
    {
        var user = new AgentUser { Gender = "female" };
        for (var step = 0; step < IntroService.Steps; step++)
        {
            var question = IntroService.Question(user, step);
            Assert.InRange(question.Buttons!.Count, 2, 3);
            Assert.All(question.Buttons!, b => Assert.True(b.Title.Length <= 20, b.Title));
            Assert.All(question.Buttons!, b => Assert.StartsWith(IntroService.Prefix, b.Id));
        }
    }
}
