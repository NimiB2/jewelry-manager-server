using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Invoices;

/// <summary>
/// The real invoice reader: sends the file to the main AI model and parses its answer. If the main model
/// fails or returns nothing, the fallback model gets one try. With nothing configured it reports itself
/// unavailable and the form is filled by hand. A failing provider never breaks the upload.
/// </summary>
public class AiInvoiceReader(
    HttpClient http,
    IOptions<InvoiceReaderOptions> options,
    IEnumerable<IInvoiceAiProvider> providers,
    ILogger<AiInvoiceReader> logger) : IInvoiceReader
{
    private IEnumerable<(InvoiceReaderEndpoint Endpoint, IInvoiceAiProvider Provider)> Chain()
    {
        foreach (var endpoint in new InvoiceReaderEndpoint[] { options.Value, options.Value.Fallback })
        {
            if (!endpoint.IsConfigured) continue;
            var provider = providers.FirstOrDefault(p => string.Equals(p.Name, endpoint.Provider.Trim(), StringComparison.OrdinalIgnoreCase));
            if (provider is not null) yield return (endpoint, provider);
        }
    }

    public bool IsAvailable => Chain().Any();

    public async Task<InvoiceSuggestion?> ReadAsync(Stream content, string fileName, string contentType, IReadOnlyList<string> expenseTypes)
    {
        var attempts = Chain().Where(c => c.Provider.Supports(contentType)).ToList();
        if (attempts.Count == 0) return null;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var base64 = Convert.ToBase64String(buffer.ToArray());
        var prompt = InvoiceAnswerParser.BuildPrompt(expenseTypes);

        foreach (var (endpoint, provider) in attempts)
        {
            var suggestion = await TryAsync(endpoint, provider, prompt, base64, contentType, fileName);
            if (suggestion is not null) return suggestion;
        }
        return null;
    }

    private async Task<InvoiceSuggestion?> TryAsync(
        InvoiceReaderEndpoint endpoint, IInvoiceAiProvider provider, string prompt, string base64, string contentType, string fileName)
    {
        try
        {
            using var request = provider.BuildRequest(endpoint, prompt, base64, contentType, fileName);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // Status only: the body may echo the request, which contains the invoice.
                logger.LogWarning("Invoice reader {Provider}/{Model} answered {Status}", provider.Name, endpoint.Model, (int)response.StatusCode);
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var suggestion = InvoiceAnswerParser.Parse(provider.ExtractText(json.RootElement));
            logger.LogInformation("Invoice read by {Provider}/{Model}: {Result}", provider.Name, endpoint.Model, suggestion is null ? "nothing readable" : "ok");
            return suggestion;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Invoice reader {Provider}/{Model} failed: {Error}", provider.Name, endpoint.Model, ex.GetType().Name);
            return null;
        }
    }
}
