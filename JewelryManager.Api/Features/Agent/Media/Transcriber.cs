using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Media;

/// <summary>Turns a voice note into text. Which service does it is a setting; the agent only sees this interface.</summary>
public interface ITranscriber
{
    bool IsAvailable { get; }

    /// <returns>The text, or null if nothing could be understood.</returns>
    Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct);
}

/// <summary>
/// Works with any service that offers the OpenAI-style /audio/transcriptions endpoint (OpenAI, Groq, a self-hosted
/// Whisper...). The recording is sent once and never stored by the agent.
/// </summary>
public class OpenAiCompatibleTranscriber(HttpClient http, IOptions<AgentOptions> options) : ITranscriber
{
    private TranscriptionOptions Config => options.Value.Transcription;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Config.ApiKey);

    public async Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct)
    {
        if (!IsAvailable) return null;

        var baseUrl = (string.IsNullOrWhiteSpace(Config.BaseUrl) ? "https://api.openai.com/v1" : Config.BaseUrl).TrimEnd('/');

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType.Split(';')[0].Trim());
        form.Add(file, "file", "voice" + Extension(mimeType));
        form.Add(new StringContent(Config.Model), "model");
        form.Add(new StringContent(Config.Language), "language");
        form.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Extension(string mime) => mime.ToLowerInvariant() switch
    {
        var m when m.Contains("ogg") => ".ogg",
        var m when m.Contains("mpeg") => ".mp3",
        var m when m.Contains("mp4") || m.Contains("m4a") || m.Contains("aac") => ".m4a",
        var m when m.Contains("amr") => ".amr",
        var m when m.Contains("wav") => ".wav",
        _ => ".ogg",
    };
}
