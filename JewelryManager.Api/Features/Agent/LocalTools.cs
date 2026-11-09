using System.Text.Json;
using JewelryManager.Api.Features.Agent.Model;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Tools that belong to the agent itself - its memory of how the person likes things, undo, its own notifications,
/// and the coach's switches. They touch only the agent's tables, never the business data, so they are not MCP tools.
/// </summary>
public static class LocalTools
{
    public const string SavePreference = "save_preference";
    public const string ListPreferences = "list_preferences";
    public const string DeletePreference = "delete_preference";
    public const string SaveAlias = "save_alias";
    public const string DeleteAlias = "delete_alias";
    public const string UndoLast = "undo_last";
    public const string SetNotifications = "set_notifications";

    public const string MarkItemDone = "mark_item_done";
    public const string MuteTopic = "mute_topic";
    public const string UnmuteTopic = "unmute_topic";
    public const string SetOccasion = "set_occasion";
    public const string SetRestDays = "set_rest_days";
    public const string AddObligation = "add_obligation";
    public const string ListObligations = "list_obligations";
    public const string RemoveObligation = "remove_obligation";
    public const string SetFocus = "set_focus";
    public const string MapMaterial = "map_material";
    public const string ListCoachState = "list_coach_state";

    public static IReadOnlyList<AgentToolSpec> Specs { get; } =
    [
        Spec(SavePreference,
            "Remember a standing instruction about HOW to present things (e.g. show customer names instead of order numbers). Never for permissions, limits or confirmations.",
            new
            {
                key = Str("Short English slug of the topic, e.g. order_display. The same key replaces the old value."),
                instruction = Str("One short sentence in Hebrew, max 200 characters."),
                forEveryone = Bool("True only if the person said it applies to everyone in the business."),
            }, ["key", "instruction"]),
        Spec(ListPreferences, "List what is remembered about how the person likes things.", new { }, []),
        Spec(DeletePreference, "Forget one remembered preference.", new { key = Str("The key of the preference.") }, ["key"]),
        Spec(SaveAlias, "Remember a nickname for a customer or supplier name.",
            new { nickname = Str("The short name people use."), fullName = Str("The full name in the system.") }, ["nickname", "fullName"]),
        Spec(DeleteAlias, "Forget a nickname.", new { nickname = Str("The nickname.") }, ["nickname"]),
        Spec(UndoLast, "Take back the last change the agent made in the last few minutes.", new { }, []),
        Spec(SetNotifications, "Turn the agent's own messages to this person on or off.",
            new
            {
                kind = Str("morning_summary, weekly_review, ideas (the agent's own suggestions and reminders), store_orders or stuck_orders."),
                enabled = Bool("True to turn on."),
            }, ["kind", "enabled"]),

        Spec(MarkItemDone, "She says she already did or handled something the agent had suggested or reminded about. Match it to the open items listed in the context, or give its words.",
            new { title = Str("The item's title or the words she used.") }, ["title"]),
        Spec(MuteTopic, "She asked the agent to stop raising a kind of thing, for good.",
            new { topic = Str("task, season (occasions), growth (focus ideas), review (weekly review), morning, monthend, anniversary or metal.") }, ["topic"]),
        Spec(UnmuteTopic, "She wants a muted kind of thing back.", new { topic = Str("The same topics as mute_topic.") }, ["topic"]),
        Spec(SetOccasion, "Add or remove an occasion she wants help preparing for.",
            new { code = Str("valentine, family_day, passover, tu_bav, rosh_hashana, hanukkah or black_friday."), enabled = Bool("True to prepare for it.") }, ["code", "enabled"]),
        Spec(SetRestDays, "Her rest days, when the agent must not write on its own.",
            new { days = Str("none, saturday or friday_saturday.") }, ["days"]),
        Spec(AddObligation, "A duty she wants as a task every month.",
            new { title = Str("What to do, in Hebrew."), dayOfMonth = new { type = "integer", description = "Day of the month, 1 to 31." } }, ["title", "dayOfMonth"]),
        Spec(ListObligations, "List her monthly duties.", new { }, []),
        Spec(RemoveObligation, "Stop a monthly duty.", new { title = Str("A part of its title.") }, ["title"]),
        Spec(SetFocus, "What she decided the business concentrates on now.",
            new { area = Str("advertising, new_jewelry, website, customers, admin or occasion."), firstStep = Str("One small first step, optional.") }, ["area"]),
        Spec(MapMaterial, "Tell the agent which metal and purity a material in the settings is, for the gold and silver index.",
            new { material = Str("The material's name as in the settings."), metal = Str("gold or silver."), purity = new { type = "number", description = "A fraction such as 0.585 or 0.925." } },
            ["material", "metal", "purity"]),
        Spec(ListCoachState, "What the agent has on record about her: focus, occasions, monthly duties, muted topics, open suggestions.", new { }, []),
    ];

    private static object Str(string description) => new { type = "string", description };

    private static object Bool(string description) => new { type = "boolean", description };

    private static AgentToolSpec Spec(string name, string description, object properties, string[] required) =>
        new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties, required }));
}
