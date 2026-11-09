namespace JewelryManager.Api.Features.Agent;

/// <summary>How much trust a tool needs before it runs.</summary>
public enum ToolTier
{
    // Only reads: runs at once.
    Read,

    // Small and reversible: runs at once, and "בטלי" takes it back.
    Immediate,

    // Changes the books: the person must approve first (by button), and the server runs it without the model.
    Confirm,

    // Handled inside the agent itself (preferences, undo...), never sent to the business API.
    Local,
}

/// <summary>
/// The single list of tools the model may use and how each one is gated. A tool that is not listed here
/// is not offered to the model, so adding a tool to the MCP server never opens it up by accident.
/// Deleting records, changing pricing settings and managing users are deliberately absent.
/// </summary>
public static class ToolPolicy
{
    private static readonly Dictionary<string, ToolTier> Tiers = new()
    {
        ["list_orders"] = ToolTier.Read,
        ["get_order"] = ToolTier.Read,
        ["orders_needing_attention"] = ToolTier.Read,
        ["list_pending_orders"] = ToolTier.Read,
        ["finance_summary"] = ToolTier.Read,
        ["finance_analysis"] = ToolTier.Read,
        ["list_expenses"] = ToolTier.Read,
        ["list_suppliers"] = ToolTier.Read,
        ["expenses_without_receipt"] = ToolTier.Read,
        ["get_expense"] = ToolTier.Read,
        ["calculate_price"] = ToolTier.Read,
        ["get_product"] = ToolTier.Read,
        ["search"] = ToolTier.Read,
        ["statement_status"] = ToolTier.Read,
        ["list_tasks"] = ToolTier.Read,
        ["sales_by_period"] = ToolTier.Read,
        ["growth_snapshot"] = ToolTier.Read,
        ["best_and_slow_sellers"] = ToolTier.Read,
        ["discount_ceiling"] = ToolTier.Read,
        ["what_if_metal"] = ToolTier.Read,

        ["advance_stage"] = ToolTier.Immediate,
        ["mark_receipt_sent"] = ToolTier.Immediate,
        ["mark_customer_notified"] = ToolTier.Immediate,
        ["add_task"] = ToolTier.Immediate,
        ["complete_task"] = ToolTier.Immediate,
        ["set_task_due"] = ToolTier.Immediate,
        ["park_task"] = ToolTier.Immediate,
        ["unpark_task"] = ToolTier.Immediate,

        ["set_stage"] = ToolTier.Confirm,
        ["set_status"] = ToolTier.Confirm,
        ["approve_order"] = ToolTier.Confirm,
        ["dismiss_alert"] = ToolTier.Confirm,
        ["set_line_product"] = ToolTier.Confirm,
        ["create_order"] = ToolTier.Confirm,
        ["update_order"] = ToolTier.Confirm,
        ["add_expense"] = ToolTier.Confirm,
        ["update_expense"] = ToolTier.Confirm,
        ["break_down_task"] = ToolTier.Confirm,
        ["add_tasks"] = ToolTier.Confirm,

        [LocalTools.SavePreference] = ToolTier.Local,
        [LocalTools.ListPreferences] = ToolTier.Local,
        [LocalTools.DeletePreference] = ToolTier.Local,
        [LocalTools.SaveAlias] = ToolTier.Local,
        [LocalTools.DeleteAlias] = ToolTier.Local,
        [LocalTools.UndoLast] = ToolTier.Local,
        [LocalTools.SetNotifications] = ToolTier.Local,
        [LocalTools.MarkItemDone] = ToolTier.Local,
        [LocalTools.MuteTopic] = ToolTier.Local,
        [LocalTools.UnmuteTopic] = ToolTier.Local,
        [LocalTools.SetOccasion] = ToolTier.Local,
        [LocalTools.SetRestDays] = ToolTier.Local,
        [LocalTools.AddObligation] = ToolTier.Local,
        [LocalTools.ListObligations] = ToolTier.Local,
        [LocalTools.RemoveObligation] = ToolTier.Local,
        [LocalTools.SetFocus] = ToolTier.Local,
        [LocalTools.MapMaterial] = ToolTier.Local,
        [LocalTools.ListCoachState] = ToolTier.Local,
    };

    public static bool IsKnown(string name) => Tiers.ContainsKey(name);

    public static ToolTier TierOf(string name) =>
        Tiers.TryGetValue(name, out var tier) ? tier : throw new InvalidOperationException($"Tool '{name}' is not allowed");
}
