namespace JewelryManager.Api.Features.Invoices;

/// <summary>One AI model to call: which vendor, which model, and the key for it.</summary>
public class InvoiceReaderEndpoint
{
    // Values pasted into a hosting dashboard often carry a stray space or newline; a key with one is rejected.
    private string _provider = "";
    private string _model = "";
    private string _apiKey = "";

    /// <summary>"gemini", "openai" or "claude". Empty means not used.</summary>
    public string Provider { get => _provider; set => _provider = value?.Trim() ?? ""; }

    public string Model { get => _model; set => _model = value?.Trim() ?? ""; }

    public string ApiKey { get => _apiKey; set => _apiKey = value?.Trim() ?? ""; }

    /// <summary>Optional override of the provider's default address (useful for tests and proxies).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>OpenAI reasoning models only: "none" or "low" makes them much faster for simple reading.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>An endpoint is usable only when it is fully configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Provider)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Which AI reads the invoices. Bound from the "InvoiceReader" configuration section; keys come from
/// env vars or appsettings.Development.json, never from the repository. The top-level fields are the
/// main model; <see cref="Fallback"/> is tried only when the main one fails or returns nothing.
/// </summary>
public class InvoiceReaderOptions : InvoiceReaderEndpoint
{
    public const string SectionName = "InvoiceReader";

    public InvoiceReaderEndpoint Fallback { get; set; } = new();
}
