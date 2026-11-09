using System.Text.Json;
using JewelryManager.Mcp;

namespace JewelryManager.Tests.Agent;

/// <summary>An in-memory stand-in for the business API: routes answer with canned JSON and every call is recorded.</summary>
public class FakeBackendApi : IBackendApi
{
    public const string OrderId = "11111111-1111-1111-1111-111111111111";
    public const string LineId = "22222222-2222-2222-2222-222222222222";
    public const string ProductId = "33333333-3333-3333-3333-333333333333";

    public List<(HttpMethod Method, string Path, string? Body)> Calls { get; } = [];

    public string MaterialsJson { get; set; } = "[]";

    // Invented data only: no real names, phones or prices.
    public string OrderJson { get; set; } = $$"""
        {"id":"{{OrderId}}","number":1042,"date":"2026-11-02","customer":"דנה בדיקה","amount":1300,"finalAmount":1250,
         "status":"IN_PROGRESS","preparationStage":"ליטוש","receiptSent":false,"hasDiscount":false,
         "customerPhone":"972500000000","customerWhatsApp":"972500000000","customerMessage":"secret text",
         "salary":300,"laborHourRate":100,
         "items":[{"id":"{{LineId}}","name":"טבעת בדיקה","quantity":1,"unitPrice":1250,"lineTotal":1250,"workHours":3}]}
        """;

    public Task<JsonElement> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var bodyText = body is null ? null : JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Calls.Add((method, path, bodyText));

        var json = Answer(method, path, bodyText);
        if (json is null) throw new BackendApiException(404, "not found");
        return Task.FromResult(json.Length == 0 ? default : JsonDocument.Parse(json).RootElement.Clone());
    }

    /// <summary>The to-do list: tasks as the API returns them (camelCase, enums in UPPER_SNAKE).</summary>
    public List<Dictionary<string, object?>> Tasks { get; } = [];

    public Dictionary<string, object?> AddTask(string title, string status = "NEW", DateTime? updatedAt = null, string? dueDate = null,
        Guid? parent = null, int defer = 0, DateTime? parked = null)
    {
        var now = updatedAt ?? DateTime.UtcNow;
        var task = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(), ["title"] = title, ["status"] = status, ["isAutomatic"] = false,
            ["createdAt"] = now.ToString("O"), ["updatedAt"] = now.ToString("O"), ["dueDate"] = dueDate,
            ["parentTaskId"] = parent?.ToString(), ["snoozedUntil"] = null, ["deferCount"] = defer, ["parkedAt"] = parked?.ToString("O"),
        };
        Tasks.Add(task);
        return task;
    }

    private string? TaskAnswer(HttpMethod method, string path, string? body)
    {
        var parts = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != "tasks") return null;
        var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, opts);
        using var doc = body is null ? null : JsonDocument.Parse(body);
        string? Field(string name) => doc is not null && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (parts.Length == 1 && method == HttpMethod.Get) return Json(Tasks);
        if (parts.Length == 1 && method == HttpMethod.Post)
        {
            var created = AddTask(Field("title") ?? "?", Field("status") ?? "NEW", dueDate: Field("dueDate"));
            return Json(created);
        }

        if (parts.Length == 2 && parts[1] == "batch" && method == HttpMethod.Post)
        {
            var made = new List<Dictionary<string, object?>>();
            foreach (var item in doc!.RootElement.GetProperty("tasks").EnumerateArray())
            {
                Guid? parent = item.TryGetProperty("parentTaskId", out var p) && p.ValueKind == JsonValueKind.String ? Guid.Parse(p.GetString()!) : null;
                made.Add(AddTask(item.GetProperty("title").GetString()!, parent: parent));
            }

            return Json(made);
        }

        if (parts.Length == 2 && parts[1] == "batch-delete")
        {
            var ids = doc!.RootElement.GetProperty("ids").EnumerateArray().Select(i => i.GetString()).ToHashSet();
            Tasks.RemoveAll(t => ids.Contains((string?)t["id"]));
            return "";
        }

        var task = Tasks.FirstOrDefault(t => (string?)t["id"] == parts[1]);
        if (task is null) return null;
        if (parts.Length == 2 && method == HttpMethod.Get) return Json(task);
        if (parts.Length == 2 && method == HttpMethod.Put)
        {
            task["dueDate"] = Field("dueDate");
            task["updatedAt"] = DateTime.UtcNow.ToString("O");
            task["deferCount"] = (int)task["deferCount"]! + (Field("dueDate") is not null && task["dueDate"] is not null ? 0 : 0);
            return Json(task);
        }

        if (parts.Length == 3 && parts[2] == "status")
        {
            task["status"] = Field("status");
            task["updatedAt"] = DateTime.UtcNow.ToString("O");
            return Json(task);
        }

        if (parts.Length == 3 && parts[2] == "coach")
        {
            switch (Field("action"))
            {
                case "park": task["parkedAt"] = DateTime.UtcNow.ToString("O"); break;
                case "unpark": task["parkedAt"] = null; task["snoozedUntil"] = null; task["updatedAt"] = DateTime.UtcNow.ToString("O"); break;
                case "snooze": task["snoozedUntil"] = Field("until"); break;
                case "clear-due": task["dueDate"] = null; break;
            }

            return Json(task);
        }

        return null;
    }

    private string? Answer(HttpMethod method, string path, string? body = null)
    {
        if (path.StartsWith("/tasks")) return TaskAnswer(method, path, body);

        if (path.StartsWith("/orders/attention")) return """{"pending":0,"alerts":0,"messagesToSend":0,"total":0}""";
        if (path.StartsWith("/orders/pending")) return "[]";
        if (path.StartsWith("/analytics/sales")) return "[]";
        if (path.StartsWith("/analytics/sellers")) return """{"months":1,"best":[],"slow":[]}""";
        if (path.StartsWith("/analytics/anniversaries")) return "[]";
        if (path.StartsWith("/analytics/materials")) return MaterialsJson;
        if (path.StartsWith("/analytics/growth"))
            return """{"ordersLast30Days":10,"ordersPrior30DayAverage":10,"averageOrderValue90Days":800,"repeatCustomerRate":0.3,"products":40,"productsNeedingDetails":0,"daysSinceLastProductAdded":20,"storeShare90Days":0.4,"topMaterial":"זהב","topMaterialShare":0.5}""";
        if (path.StartsWith("/analytics/discount-ceiling"))
            return """{"profitFloorPercent":30,"byType":[{"type":"טבעת","products":3,"safeForAllPercent":12.5,"medianPercent":15}],"alreadyBelowFloor":[]}""";
        if (path.StartsWith("/analytics/what-if"))
            return """{"products":4,"averageChangePercent":3.1,"byType":[{"type":"טבעת","products":4,"averageChangePercent":3.1}],"newlyBelowProfitFloor":0,"newlyBelowExamples":[]}""";
        if (path.StartsWith("/statements/months"))
            return """{"month":"2026-11","isClosed":false,"pendingCount":0,"canClose":false,"settlementsWithoutCardStatement":[],"expensesNotInStatements":[]}""";

        if (path.StartsWith("/orders?") && method == HttpMethod.Get)
            return "{\"orders\":[" + OrderJson + "],\"summary\":{\"count\":1,\"totalFinalAmount\":1250}}";

        if (path.EndsWith("/advance-stage")) return OrderJson.Replace("ליטוש", "אריזה");
        if (path.EndsWith("/receipt-sent")) return OrderJson.Replace("\"receiptSent\":false", "\"receiptSent\":true");
        if (path.EndsWith("/status") || path.EndsWith("/stage")) return OrderJson;
        if (path == "/orders" && method == HttpMethod.Post) return OrderJson;

        if (path.StartsWith("/search"))
            return $$"""{"query":"x","orders":[],"products":[{"id":"{{ProductId}}","name":"טבעת בדיקה","type":"טבעת","material":"כסף","sitePrice":900}],"expenses":[]}""";

        if (path.StartsWith("/finances"))
            return """{"items":[{"id":"44444444-4444-4444-4444-444444444444","kind":"expense","date":"2026-11-03","description":"חומרי גלם","amount":340,"supplier":"ספק בדיקה","hasReceipt":true}],"summary":{"totalIncome":5000,"totalExpenses":1200,"netProfit":3800,"expensesWithoutReceipt":2}}""";

        if (path == "/expenses" && method == HttpMethod.Post)
            return """{"id":"55555555-5555-5555-5555-555555555555","date":"2026-11-09","description":"בדיקה","amount":340,"supplier":"ספק בדיקה","hasReceipt":false}""";

        return null;
    }
}
