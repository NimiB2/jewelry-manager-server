using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace JewelryManager.Mcp;

/// <summary>One line of a new order: a catalog product by name. The price always comes from the system.</summary>
public record OrderItemArg(string ProductName, int Quantity = 1, string? Note = null);

/// <summary>
/// Order tools. Orders are found by their number, never by an internal id. Pay, labor rate, work hours and the
/// customer's phone are not on the allow-lists, so they cannot reach the model.
/// </summary>
[McpServerToolType]
public class OrderTools(IBackendApi api)
{
    private const string SummaryHint = "One short Hebrew sentence saying exactly what will be done, with the exact values.";

    private static readonly IReadOnlySet<string> ListKeys = ResultShaper.Keys(
        "orders", "summary", "count", "totalFinalAmount", "number", "date", "customer", "externalName", "finalAmount",
        "status", "preparationStage", "receiptSent", "isPendingApproval", "storeAlert", "customerNotifiedAt");

    private static readonly IReadOnlySet<string> DetailKeys = ResultShaper.Keys(
        "number", "date", "customer", "externalName", "amount", "finalAmount", "status", "preparationStage", "receiptSent",
        "notes", "hasDiscount", "discountAmount", "discountPercent", "discountReason", "isPendingApproval", "storeAlert",
        "customerNotifiedAt", "isCompleted", "items", "name", "quantity", "unitPrice", "lineTotal", "note", "needsProduct");

    private static readonly IReadOnlySet<string> AttentionKeys = ResultShaper.Keys(
        "counts", "pending", "alerts", "messagesToSend", "total", "number", "customer", "externalName", "finalAmount",
        "date", "storeAlert", "needsProduct", "items", "name", "quantity");

    // ── Read ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_orders", ReadOnly = true)]
    [Description("List orders. status: active (default), completed or all. search: customer name or order number. month: yyyy-MM. Shows at most 20.")]
    public Task<string> ListOrders(string? status = null, string? search = null, string? month = null, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var (from, to) = ToolSupport.MonthRange(month);
            var data = await api.GetAsync("/orders" + ToolSupport.Query(
                ("status", status), ("search", search),
                ("from", from is null ? null : ToolSupport.Iso(from.Value)),
                ("to", to is null ? null : ToolSupport.Iso(to.Value))), ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, ListKeys));
        });

    [McpServerTool(Name = "get_order", ReadOnly = true)]
    [Description("Full details of one order by its number.")]
    public Task<string> GetOrder(int orderNumber, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(order, DetailKeys));
        });

    [McpServerTool(Name = "orders_needing_attention", ReadOnly = true)]
    [Description("What waits for the owner: store orders to approve, orders the store changed, summaries still to send.")]
    public Task<string> OrdersNeedingAttention(CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var counts = await api.GetAsync("/orders/attention", ct);
            var pending = await api.GetAsync("/orders/pending", ct);
            var alerts = await api.GetAsync("/orders/alerts", ct);
            var combined = JsonSerializer.SerializeToElement(new { counts, pending, alerts });
            return ToolEnvelope.Ok(ResultShaper.Shape(combined, AttentionKeys, 5));
        });

    [McpServerTool(Name = "list_pending_orders", ReadOnly = true)]
    [Description("Orders that came from the online store and wait for approval.")]
    public Task<string> ListPendingOrders(CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var data = await api.GetAsync("/orders/pending", ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(data, DetailKeys, 5));
        });

    // ── Write, small and reversible: run at once, can be undone ───────────────

    [McpServerTool(Name = "advance_stage", Idempotent = false)]
    [Description("Move an order to its next preparation stage.")]
    public Task<string> AdvanceStage(int orderNumber, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var id = ToolSupport.IdOf(order);
            var previous = ToolSupport.Str(order, "preparationStage");

            var updated = await api.SendAsync(HttpMethod.Post, $"/orders/{id}/advance-stage", null, ct);
            var undo = previous is null ? null : new UndoCall("PATCH", $"/orders/{id}/stage", new { stage = previous });
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys), undo);
        });

    [McpServerTool(Name = "mark_receipt_sent")]
    [Description("Mark that the receipt was sent for an order (sent=false takes the mark back).")]
    public Task<string> MarkReceiptSent(int orderNumber, bool sent = true, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var id = ToolSupport.IdOf(order);
            var previous = order.TryGetProperty("receiptSent", out var p) && p.ValueKind == JsonValueKind.True;

            var updated = await api.SendAsync(HttpMethod.Patch, $"/orders/{id}/receipt-sent", new { receiptSent = sent }, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys),
                new UndoCall("PATCH", $"/orders/{id}/receipt-sent", new { receiptSent = previous }));
        });

    [McpServerTool(Name = "mark_customer_notified")]
    [Description("Mark that the customer was updated about an order (notified=false takes the mark back).")]
    public Task<string> MarkCustomerNotified(int orderNumber, bool notified = true, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var id = ToolSupport.IdOf(order);
            var previous = order.TryGetProperty("customerNotifiedAt", out var p) && p.ValueKind == JsonValueKind.String;

            var updated = await api.SendAsync(HttpMethod.Patch, $"/orders/{id}/customer-notified", new { notified }, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys),
                new UndoCall("PATCH", $"/orders/{id}/customer-notified", new { notified = previous }));
        });

    // ── Write, needs the owner's confirmation first ───────────────────────────

    [McpServerTool(Name = "set_stage")]
    [Description("Set an order's preparation stage to a specific stage name from the settings.")]
    public Task<string> SetStage(int orderNumber, string stage, [Description(SummaryHint)] string summary, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var id = ToolSupport.IdOf(order);
            var previous = ToolSupport.Str(order, "preparationStage");

            var updated = await api.SendAsync(HttpMethod.Patch, $"/orders/{id}/stage", new { stage }, ct);
            var undo = previous is null ? null : new UndoCall("PATCH", $"/orders/{id}/stage", new { stage = previous });
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys), undo);
        });

    [McpServerTool(Name = "set_status")]
    [Description("Set an order's status: NEW, IN_PROGRESS, READY or COMPLETED.")]
    public Task<string> SetStatus(int orderNumber, string status, [Description(SummaryHint)] string summary, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var wanted = status.Trim().ToUpperInvariant();
            if (wanted is not ("NEW" or "IN_PROGRESS" or "READY" or "COMPLETED"))
                throw new ArgumentException("status must be NEW, IN_PROGRESS, READY or COMPLETED");

            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var id = ToolSupport.IdOf(order);
            var previous = ToolSupport.Str(order, "status");

            var updated = await api.SendAsync(HttpMethod.Patch, $"/orders/{id}/status", new { status = wanted }, ct);
            var undo = previous is null ? null : new UndoCall("PATCH", $"/orders/{id}/status", new { status = previous });
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys), undo);
        });

    [McpServerTool(Name = "approve_order")]
    [Description("Approve an order that came from the online store (every line must already be tied to a product).")]
    public Task<string> ApproveOrder(int orderNumber, [Description(SummaryHint)] string summary, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var updated = await api.SendAsync(HttpMethod.Post, $"/orders/{ToolSupport.IdOf(order)}/approve", null, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys));
        });

    [McpServerTool(Name = "dismiss_alert")]
    [Description("Clear the warning the online store raised on an order (cancelled, changed or deleted in the store).")]
    public Task<string> DismissAlert(int orderNumber, [Description(SummaryHint)] string summary, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var updated = await api.SendAsync(HttpMethod.Post, $"/orders/{ToolSupport.IdOf(order)}/dismiss-alert", null, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, ListKeys));
        });

    [McpServerTool(Name = "set_line_product")]
    [Description("Tie one line of a store order to a catalog product. itemNumber counts from 1.")]
    public Task<string> SetLineProduct(int orderNumber, int itemNumber, string productName, [Description(SummaryHint)] string summary, CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var items = order.TryGetProperty("items", out var arr) ? arr.EnumerateArray().ToList() : [];
            if (itemNumber < 1 || itemNumber > items.Count)
                throw new ArgumentException($"The order has {items.Count} items");

            var lineId = ToolSupport.IdOf(items[itemNumber - 1]);
            var productId = await ToolSupport.FindProductAsync(api, productName, ct);
            var updated = await api.SendAsync(HttpMethod.Put,
                $"/orders/{ToolSupport.IdOf(order)}/items/{lineId}/product", new { productId, replace = false }, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, DetailKeys));
        });

    [McpServerTool(Name = "create_order")]
    [Description("Create a new order from catalog products by name. Prices come from the system; do not invent a price.")]
    public Task<string> CreateOrder(List<OrderItemArg> items, string? customer = null, string? notes = null,
        [Description(SummaryHint)] string summary = "", CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (items is null || items.Count == 0) throw new ArgumentException("An order needs at least one item");

            var lines = new List<object>();
            foreach (var item in items)
            {
                var productId = await ToolSupport.FindProductAsync(api, item.ProductName, ct);
                lines.Add(new { productId, quantity = Math.Clamp(item.Quantity, 1, 1000), unitPrice = (decimal?)null, note = item.Note });
            }

            var body = new { customer, date = ToolSupport.Iso(ToolSupport.Today()), notes, items = lines, discount = (object?)null };
            var created = await api.SendAsync(HttpMethod.Post, "/orders", body, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(created, DetailKeys),
                new UndoCall("DELETE", $"/orders/{ToolSupport.IdOf(created)}", null));
        });

    [McpServerTool(Name = "update_order")]
    [Description("Change an order's notes and/or discount. Give discountPercent or discountFinalAmount (what the customer pays), not both.")]
    public Task<string> UpdateOrder(int orderNumber, string? notes = null, decimal? discountPercent = null,
        decimal? discountFinalAmount = null, string? discountReason = null,
        [Description(SummaryHint)] string summary = "", CancellationToken ct = default) =>
        ToolGuard.RunAsync(async () =>
        {
            if (discountPercent is not null && discountFinalAmount is not null)
                throw new ArgumentException("Give a discount percent or a final amount, not both");

            var order = await ToolSupport.FindOrderAsync(api, orderNumber, ct);
            var id = ToolSupport.IdOf(order);

            // The API saves a whole order at once, so the untouched parts are sent back as they are.
            var lines = order.GetProperty("items").EnumerateArray().Select(i => (object)new
            {
                lineId = ToolSupport.IdOf(i),
                quantity = i.GetProperty("quantity").GetInt32(),
                unitPrice = ToolSupport.Dec(i, "unitPrice"),
                note = ToolSupport.Str(i, "note"),
            }).ToList();

            object? discount = null;
            if (discountPercent is not null)
                discount = new { mode = "PERCENT", value = discountPercent, reason = discountReason };
            else if (discountFinalAmount is not null)
                discount = new { mode = "FINAL_AMOUNT", value = discountFinalAmount, reason = discountReason };
            else if (order.TryGetProperty("hasDiscount", out var has) && has.ValueKind == JsonValueKind.True)
                discount = new { mode = "FINAL_AMOUNT", value = ToolSupport.Dec(order, "finalAmount"), reason = ToolSupport.Str(order, "discountReason") };

            var body = new
            {
                customer = ToolSupport.Str(order, "customer"),
                date = ToolSupport.Str(order, "date"),
                notes = notes ?? ToolSupport.Str(order, "notes"),
                items = lines,
                discount,
            };

            var updated = await api.SendAsync(HttpMethod.Put, $"/orders/{id}", body, ct);
            return ToolEnvelope.Ok(ResultShaper.Shape(updated, DetailKeys));
        });
}
