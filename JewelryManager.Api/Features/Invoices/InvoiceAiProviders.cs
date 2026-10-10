using System.Net.Http.Json;
using System.Text.Json;

namespace JewelryManager.Api.Features.Invoices;

/// <summary>
/// One AI vendor's wire format. It only builds the request and pulls the text out of the answer;
/// sending, error handling and reading the JSON live in <see cref="AiInvoiceReader"/>.
/// </summary>
public interface IInvoiceAiProvider
{
    string Name { get; }

    bool Supports(string contentType);

    HttpRequestMessage BuildRequest(InvoiceReaderEndpoint options, string prompt, string base64, string contentType, string fileName);

    string? ExtractText(JsonElement response);
}

internal static class ProviderHelpers
{
    public static string? Path(JsonElement element, params object[] steps)
    {
        foreach (var step in steps)
        {
            if (step is int index && element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > index)
                element = element[index];
            else if (step is string name && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var next))
                element = next;
            else
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}

public class GeminiInvoiceProvider : IInvoiceAiProvider
{
    public string Name => "gemini";

    public bool Supports(string contentType) => true;

    public HttpRequestMessage BuildRequest(InvoiceReaderEndpoint options, string prompt, string base64, string contentType, string fileName)
    {
        var baseUrl = (options.BaseUrl ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1beta/models/{Uri.EscapeDataString(options.Model)}:generateContent")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { parts = new object[] { new { text = prompt }, new { inline_data = new { mime_type = contentType, data = base64 } } } } },
                generationConfig = new { responseMimeType = "application/json", temperature = 0 },
            }),
        };
        request.Headers.Add("x-goog-api-key", options.ApiKey);
        return request;
    }

    public string? ExtractText(JsonElement response) =>
        ProviderHelpers.Path(response, "candidates", 0, "content", "parts", 0, "text");
}

public class OpenAiInvoiceProvider : IInvoiceAiProvider
{
    public string Name => "openai";

    // HEIC photos are not accepted by the API.
    public bool Supports(string contentType) => contentType != "image/heic";

    public HttpRequestMessage BuildRequest(InvoiceReaderEndpoint options, string prompt, string base64, string contentType, string fileName)
    {
        var dataUrl = $"data:{contentType};base64,{base64}";
        object file = contentType == "application/pdf"
            ? new { type = "file", file = new { filename = "invoice.pdf", file_data = dataUrl } }
            : new { type = "image_url", image_url = new { url = dataUrl } };

        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model,
            ["messages"] = new[] { new { role = "user", content = new object[] { new { type = "text", text = prompt }, file } } },
            ["response_format"] = new { type = "json_object" },
        };
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort)) body["reasoning_effort"] = options.ReasoningEffort;

        var baseUrl = (options.BaseUrl ?? "https://api.openai.com").TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Authorization", $"Bearer {options.ApiKey}");
        return request;
    }

    public string? ExtractText(JsonElement response) =>
        ProviderHelpers.Path(response, "choices", 0, "message", "content");
}

public class ClaudeInvoiceProvider : IInvoiceAiProvider
{
    public string Name => "claude";

    public bool Supports(string contentType) => contentType != "image/heic";

    public HttpRequestMessage BuildRequest(InvoiceReaderEndpoint options, string prompt, string base64, string contentType, string fileName)
    {
        var source = new { type = "base64", media_type = contentType, data = base64 };
        object file = contentType == "application/pdf"
            ? new { type = "document", source }
            : new { type = "image", source };

        var baseUrl = (options.BaseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = options.Model,
                max_tokens = 500,
                messages = new[] { new { role = "user", content = new object[] { file, new { type = "text", text = prompt } } } },
            }),
        };
        request.Headers.Add("x-api-key", options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    public string? ExtractText(JsonElement response) =>
        ProviderHelpers.Path(response, "content", 0, "text");
}
