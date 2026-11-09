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

        var json = Answer(method, path);
        if (json is null) throw new BackendApiException(404, "not found");
        return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
    }

    private string? Answer(HttpMethod method, string path)
    {
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
