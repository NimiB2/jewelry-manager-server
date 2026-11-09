using System.Text.Json;
using JewelryManager.Api.Features.Agent.Model;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Tools that belong to the agent itself - its memory of how the person likes things, undo, and its own
/// notifications. They touch only the agent's tables, never the business data, so they are not MCP tools.
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
                kind = Str("morning_summary, store_orders or stuck_orders."),
                enabled = Bool("True to turn on."),
            }, ["kind", "enabled"]),
    ];

    private static object Str(string description) => new { type = "string", description };

    private static object Bool(string description) => new { type = "boolean", description };

    private static AgentToolSpec Spec(string name, string description, object properties, string[] required) =>
        new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties, required }));
}
