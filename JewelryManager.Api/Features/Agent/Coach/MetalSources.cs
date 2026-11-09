using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Coach;

/// <summary>Reads one number out of a JSON answer by a dotted path such as "data.rates.0.price".</summary>
public static class JsonPath
{
    public static decimal? ReadNumber(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index < current.GetArrayLength())
                    current = current[index];
                else if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next))
                    current = next;
                else
                    return null;
            }

            return current.ValueKind switch
            {
                JsonValueKind.Number => current.GetDecimal(),
                JsonValueKind.String when decimal.TryParse(current.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public record SourceReading(string Source, decimal? UsdPerOunce, string? Problem);

/// <summary>The configured price services, called one by one. A failure of one is a note, never an exception.</summary>
public class MetalSources(IHttpClientFactory httpFactory, IOptions<AgentOptions> options, ILogger<MetalSources> log)
{
    public const string ClientName = "metal-prices";

    public async Task<SourceReading> ReadAsync(PriceSourceOptions source, string metal, CancellationToken ct)
    {
        if (!source.MetalCodes.TryGetValue(metal, out var code) || string.IsNullOrWhiteSpace(source.UrlTemplate))
            return new SourceReading(source.Name, null, "not configured for this metal");

        try
        {
            var json = await GetAsync(source.UrlTemplate.Replace("{metal}", code), source.ApiKeyHeader, source.ApiKey, ct);
            var price = JsonPath.ReadNumber(json, source.PricePath);
            if (price is null or <= 0) return new SourceReading(source.Name, null, "no price in the answer");

            return new SourceReading(source.Name, source.Unit == "usd_per_gram" ? price * MetalMath.GramsPerTroyOunce : price, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Metal price source {Source} failed: {Message}", source.Name, ex.Message);
            return new SourceReading(source.Name, null, "request failed");
        }
    }

    public async Task<decimal?> UsdIlsAsync(CancellationToken ct)
    {
        var fx = options.Value.Metals.Fx;
        if (string.IsNullOrWhiteSpace(fx.UrlTemplate)) return null;

        try
        {
            var rate = JsonPath.ReadNumber(await GetAsync(fx.UrlTemplate, fx.ApiKeyHeader, fx.ApiKey, ct), fx.RatePath);
            // A shekel is worth roughly a quarter to a third of a dollar: anything far from 2-6 shekels per dollar is a bad reading.
            return rate is >= 2 and <= 6 ? rate : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Exchange rate source failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<string> GetAsync(string url, string? header, string? key, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(header) && !string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation(header, key);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
