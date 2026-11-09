using System.Text;

namespace JewelryManager.Api.Features.Agent.Proactive;

/// <summary>The texts of the agent's own messages. Plain code, no model: they cost no tokens and always read the same.</summary>
public static class ProactiveMessages
{
    public static string Morning(string gender, int storeOrdersToApprove, int storeChanges, int summariesToSend, int openOrders,
        decimal? yesterdayIncome, decimal? yesterdayExpenses)
    {
        var text = new StringBuilder("בוקר טוב.");

        var waiting = new List<string>();
        if (storeOrdersToApprove > 0) waiting.Add($"{storeOrdersToApprove} הזמנות מהחנות לאישור");
        if (storeChanges > 0) waiting.Add($"{storeChanges} הזמנות ששונו בחנות");
        if (summariesToSend > 0) waiting.Add($"{summariesToSend} סיכומים לשליחה ללקוחות");

        text.Append(waiting.Count > 0 ? "\nמחכה לך: " + string.Join(", ", waiting) + "." : "\nאין משהו שמחכה לאישור.");
        text.Append($"\nהזמנות פתוחות: {openOrders}.");

        if (yesterdayIncome is not null && yesterdayExpenses is not null)
            text.Append($"\nאתמול: הכנסות {Money.Format(yesterdayIncome.Value)}, הוצאות {Money.Format(yesterdayExpenses.Value)}.");

        return text.ToString();
    }

    public static string StoreOrder(int number, string? customer) =>
        customer is null
            ? $"הגיעה הזמנה חדשה מהחנות ({number}) והיא ממתינה לאישור."
            : $"הגיעה הזמנה חדשה מהחנות ({number}, {customer}) והיא ממתינה לאישור.";

    public static string StuckOrder(int number, string? customer, int days) =>
        customer is null
            ? $"הזמנה {number} פתוחה כבר {days} ימים."
            : $"הזמנה {number} ({customer}) פתוחה כבר {days} ימים.";
}
