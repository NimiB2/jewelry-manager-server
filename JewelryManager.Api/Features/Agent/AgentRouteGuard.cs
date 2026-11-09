using System.Text.RegularExpressions;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Which API calls the agent's identity may make. A second wall behind the tool list: even if the service
/// key leaked or a tool misbehaved, the agent could not delete in bulk, change settings or manage users.
/// The only deletes are the two that take back something the agent itself just created.
/// </summary>
public static partial class AgentRouteGuard
{
    private static readonly (string Method, Regex Path)[] Allowed =
    [
        ("GET", OrdersRead()),
        ("POST", OrdersApprove()),
        ("POST", OrdersWrite()),
        ("PUT", OrdersWrite()),
        ("PATCH", OrdersWrite()),
        ("PUT", OrderLineProduct()),
        ("DELETE", SingleOrder()),
        ("GET", Finances()),
        ("GET", ExpensesRead()),
        ("GET", SingleExpense()),
        ("POST", ExpensesRoot()),
        ("PUT", SingleExpense()),
        ("DELETE", SingleExpense()),
        ("GET", Products()),
        ("GET", Search()),
        ("POST", PricingCalculate()),
        ("GET", StatementMonth()),
    ];

    public static bool IsAllowed(string method, string path)
    {
        var clean = path.TrimEnd('/');
        if (clean.Length == 0) clean = "/";
        return Allowed.Any(rule => string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase) && rule.Path.IsMatch(clean));
    }

    [GeneratedRegex("^/orders(/(years|pending|attention|alerts|[0-9a-fA-F-]{36}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdersRead();

    [GeneratedRegex("^/orders/[0-9a-fA-F-]{36}/approve$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdersApprove();

    [GeneratedRegex("^/orders(/[0-9a-fA-F-]{36}(/(status|stage|advance-stage|receipt-sent|customer-notified|dismiss-alert))?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdersWrite();

    [GeneratedRegex("^/orders/[0-9a-fA-F-]{36}/items/[0-9a-fA-F-]{36}/product$", RegexOptions.IgnoreCase)]
    private static partial Regex OrderLineProduct();

    [GeneratedRegex("^/orders/[0-9a-fA-F-]{36}$", RegexOptions.IgnoreCase)]
    private static partial Regex SingleOrder();

    [GeneratedRegex("^/finances(/analysis)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Finances();

    [GeneratedRegex("^/expenses/(suppliers|supplier-types|without-receipt)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExpensesRead();

    [GeneratedRegex("^/expenses/[0-9a-fA-F-]{36}$", RegexOptions.IgnoreCase)]
    private static partial Regex SingleExpense();

    [GeneratedRegex("^/expenses$", RegexOptions.IgnoreCase)]
    private static partial Regex ExpensesRoot();

    [GeneratedRegex("^/products(/[0-9a-fA-F-]{36})?$", RegexOptions.IgnoreCase)]
    private static partial Regex Products();

    [GeneratedRegex("^/search$", RegexOptions.IgnoreCase)]
    private static partial Regex Search();

    [GeneratedRegex("^/pricing/calculate$", RegexOptions.IgnoreCase)]
    private static partial Regex PricingCalculate();

    [GeneratedRegex(@"^/statements/months/\d{4}-\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex StatementMonth();
}
