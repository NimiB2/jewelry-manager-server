using System.Net;
using System.Text;
using System.Text.Json;

namespace JewelryManager.Mcp;

/// <summary>A call to the business API failed. The message is safe to show the agent (it comes from the API's own error shape).</summary>
public class BackendApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The only way MCP tools reach the business: the existing REST API. Tools never see the database, so
/// validation and tenant isolation stay where they already live.
/// </summary>
public interface IBackendApi
{
    Task<JsonElement> GetAsync(string path, CancellationToken ct = default);

    Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default);
}

/// <summary>Talks to the API over HTTP. The HttpClient already carries the service key and the acting user.</summary>
public class HttpBackendApi(HttpClient http) : IBackendApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<JsonElement> GetAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, null, ct);

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new BackendApiException((int)response.StatusCode, ReadError(response.StatusCode, text));

        if (string.IsNullOrWhiteSpace(text)) return default;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // The API answers errors as { "error": "..." }; anything else is reduced to a generic line.
    private static string ReadError(HttpStatusCode status, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                return error.GetString()!;
        }
        catch (JsonException)
        {
        }

        return $"The system answered {(int)status}";
    }
}
