using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.WhatsApp;

public record ReplyButton(string Id, string Title);

public record DownloadedMedia(byte[] Content, string MimeType);

/// <summary>Sends and receives through the WhatsApp Cloud API. Everything the agent says goes out through here.</summary>
public interface IWhatsAppClient
{
    Task SendTextAsync(string to, string text, CancellationToken ct);

    /// <summary>A message with up to three quick-reply buttons (titles up to 20 characters).</summary>
    Task SendButtonsAsync(string to, string text, IReadOnlyList<ReplyButton> buttons, CancellationToken ct);

    /// <summary>A pre-approved template: the only kind of message a business may start outside the 24-hour window.</summary>
    Task SendTemplateAsync(string to, string template, string language, IReadOnlyList<string> bodyParameters, CancellationToken ct);

    Task MarkReadAsync(string messageId, CancellationToken ct);

    /// <summary>Downloads a photo, document or voice note. Returns null if it is larger than the allowed size.</summary>
    Task<DownloadedMedia?> DownloadMediaAsync(string mediaId, int maxBytes, CancellationToken ct);
}

public class WhatsAppClient(HttpClient http, IOptions<AgentOptions> options, ILogger<WhatsAppClient> log) : IWhatsAppClient
{
    private const int MaxText = 4000;
    private WhatsAppOptions Config => options.Value.WhatsApp;

    public Task SendTextAsync(string to, string text, CancellationToken ct) =>
        PostMessageAsync(new
        {
            messaging_product = "whatsapp",
            to,
            type = "text",
            text = new { body = Trim(text, MaxText) },
        }, ct);

    public Task SendButtonsAsync(string to, string text, IReadOnlyList<ReplyButton> buttons, CancellationToken ct) =>
        PostMessageAsync(new
        {
            messaging_product = "whatsapp",
            to,
            type = "interactive",
            interactive = new
            {
                type = "button",
                body = new { text = Trim(text, 1000) },
                action = new
                {
                    buttons = buttons.Take(3).Select(b => new { type = "reply", reply = new { id = b.Id, title = Trim(b.Title, 20) } }),
                },
            },
        }, ct);

    public Task SendTemplateAsync(string to, string template, string language, IReadOnlyList<string> bodyParameters, CancellationToken ct) =>
        PostMessageAsync(new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = template,
                language = new { code = language },
                components = bodyParameters.Count == 0
                    ? Array.Empty<object>()
                    : [new { type = "body", parameters = bodyParameters.Select(p => new { type = "text", text = Trim(p, 500) }).ToArray() }],
            },
        }, ct);

    public Task MarkReadAsync(string messageId, CancellationToken ct) =>
        PostMessageAsync(new { messaging_product = "whatsapp", status = "read", message_id = messageId }, ct);

    public async Task<DownloadedMedia?> DownloadMediaAsync(string mediaId, int maxBytes, CancellationToken ct)
    {
        using var info = new HttpRequestMessage(HttpMethod.Get, $"{Config.GraphVersion}/{Uri.EscapeDataString(mediaId)}");
        Authorize(info);
        using var infoResponse = await http.SendAsync(info, ct);
        infoResponse.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync(ct));
        var url = doc.RootElement.GetProperty("url").GetString()!;
        var mime = doc.RootElement.TryGetProperty("mime_type", out var m) ? m.GetString() ?? "application/octet-stream" : "application/octet-stream";
        if (doc.RootElement.TryGetProperty("file_size", out var size) && size.TryGetInt64(out var bytes) && bytes > maxBytes) return null;

        using var download = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(download);
        using var response = await http.SendAsync(download, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsByteArrayAsync(ct);
        return content.Length > maxBytes ? null : new DownloadedMedia(content, mime);
    }

    private async Task PostMessageAsync(object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Config.GraphVersion}/{Config.PhoneNumberId}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        Authorize(request);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Meta's error body can echo the number; only the status goes to the log.
            log.LogWarning("WhatsApp send failed with {Status}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.AccessToken);

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
