using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JewelryManager.Api.Features.Agent.WhatsApp;

public enum IncomingKind
{
    Text,
    Image,
    Audio,
    Document,
    ButtonReply,
    Unsupported,
}

/// <summary>One message from a person, reduced to what the agent needs. The phone number is digits with the country code.</summary>
public record IncomingMessage(
    string MessageId,
    string From,
    IncomingKind Kind,
    string? Text = null,
    string? MediaId = null,
    string? MimeType = null,
    string? FileName = null,
    string? ButtonId = null);

/// <summary>Reads Meta's webhook JSON. Delivery receipts and anything that is not a message are ignored.</summary>
public static class WhatsAppPayloadParser
{
    public static List<IncomingMessage> Parse(string json, string? expectedPhoneNumberId)
    {
        var result = new List<IncomingMessage>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return result;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)) continue;
                    if (!BelongsToUs(value, expectedPhoneNumberId)) continue;
                    if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;

                    foreach (var message in messages.EnumerateArray())
                    {
                        var parsed = ParseMessage(message);
                        if (parsed is not null) result.Add(parsed);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static bool BelongsToUs(JsonElement value, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        return value.TryGetProperty("metadata", out var meta) &&
               meta.TryGetProperty("phone_number_id", out var id) &&
               id.ValueKind == JsonValueKind.String && id.GetString() == expected;
    }

    private static IncomingMessage? ParseMessage(JsonElement m)
    {
        var id = Str(m, "id");
        var from = Str(m, "from");
        var type = Str(m, "type");
        if (id is null || from is null || type is null) return null;

        switch (type)
        {
            case "text":
                return new IncomingMessage(id, from, IncomingKind.Text, Text: m.TryGetProperty("text", out var t) ? Str(t, "body") : null);

            case "image":
                var image = m.GetProperty("image");
                return new IncomingMessage(id, from, IncomingKind.Image, Text: Str(image, "caption"), MediaId: Str(image, "id"), MimeType: Str(image, "mime_type"));

            case "audio":
                var audio = m.GetProperty("audio");
                return new IncomingMessage(id, from, IncomingKind.Audio, MediaId: Str(audio, "id"), MimeType: Str(audio, "mime_type"));

            case "document":
                var doc = m.GetProperty("document");
                return new IncomingMessage(id, from, IncomingKind.Document, Text: Str(doc, "caption"), MediaId: Str(doc, "id"),
                    MimeType: Str(doc, "mime_type"), FileName: Str(doc, "filename"));

            case "interactive":
                if (m.TryGetProperty("interactive", out var interactive) && interactive.TryGetProperty("button_reply", out var reply))
                    return new IncomingMessage(id, from, IncomingKind.ButtonReply, ButtonId: Str(reply, "id"));
                return new IncomingMessage(id, from, IncomingKind.Unsupported);

            default:
                return new IncomingMessage(id, from, IncomingKind.Unsupported);
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Checks that a webhook really came from Meta: HMAC-SHA256 of the raw body with the app secret.</summary>
public static class WhatsAppSignature
{
    public static bool IsValid(byte[] body, string? header, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(header)) return false;
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        byte[] sent;
        try
        {
            sent = Convert.FromHexString(header[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(sent, expected);
    }

    public static string Sign(byte[] body, string appSecret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();
}
