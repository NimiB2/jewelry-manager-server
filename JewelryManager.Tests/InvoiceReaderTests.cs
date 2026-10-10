using System.Net;
using System.Text;
using JewelryManager.Api.Features.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JewelryManager.Tests;

public class InvoiceReaderTests
{
    private static readonly string[] Types = ["אריזה", "חומרי גלם"];
    private const string Answer = """{"amount": 118.5, "date": "2026-03-04", "supplier": "כסף ושות׳", "description": "חוט כסף 925", "type": "חומרי גלם"}""";

    // Answers with a canned body and remembers what was sent, so no network and no key are involved.
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static AiInvoiceReader Reader(FakeHandler handler, string provider, string model = "m", string key = "secret-key") =>
        new(new HttpClient(handler),
            Options.Create(new InvoiceReaderOptions { Provider = provider, Model = model, ApiKey = key }),
            [new GeminiInvoiceProvider(), new OpenAiInvoiceProvider(), new ClaudeInvoiceProvider()],
            NullLogger<AiInvoiceReader>.Instance);

    private static Task<InvoiceSuggestion?> Read(AiInvoiceReader reader, string contentType = "image/jpeg") =>
        reader.ReadAsync(new MemoryStream([1, 2, 3]), "a.jpg", contentType, Types);

    private static string Escape(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    [Theory]
    [InlineData("gemini", """{"candidates":[{"content":{"parts":[{"text":__}]}}]}""")]
    [InlineData("openai", """{"choices":[{"message":{"content":__}}]}""")]
    [InlineData("claude", """{"content":[{"type":"text","text":__}]}""")]
    public async Task EachProvider_ReadsItsOwnAnswerShape(string provider, string template)
    {
        var handler = new FakeHandler(HttpStatusCode.OK, template.Replace("__", Escape(Answer)));

        var result = await Read(Reader(handler, provider));

        Assert.Equal(new InvoiceSuggestion(118.5m, new DateOnly(2026, 3, 4), "כסף ושות׳", "חוט כסף 925", "חומרי גלם"), result);
    }

    [Theory]
    [InlineData("gemini", "x-goog-api-key")]
    [InlineData("openai", "Authorization")]
    [InlineData("claude", "x-api-key")]
    public async Task Key_TravelsOnlyInTheHeader_NeverInTheBodyOrUrl(string provider, string header)
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        await Read(Reader(handler, provider));

        Assert.True(handler.Request!.Headers.Contains(header));
        Assert.DoesNotContain("secret-key", handler.Request.RequestUri!.ToString());
        Assert.DoesNotContain("secret-key", handler.SentBody);
    }

    [Fact]
    public async Task WithoutProviderModelOrKey_ReaderIsUnavailable_AndSendsNothing()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        foreach (var reader in new[] { Reader(handler, ""), Reader(handler, "gemini", model: ""), Reader(handler, "gemini", key: ""), Reader(handler, "unknown") })
        {
            Assert.False(reader.IsAvailable);
            Assert.Null(await Read(reader));
        }
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ProviderThatCannotTakeTheFileType_ReturnsNothingWithoutCalling()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        Assert.Null(await Read(Reader(handler, "claude"), "image/heic"));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ProviderError_OrBrokenAnswer_GivesNoSuggestionInsteadOfFailing()
    {
        Assert.Null(await Read(Reader(new FakeHandler(HttpStatusCode.TooManyRequests, "{}"), "gemini")));
        Assert.Null(await Read(Reader(new FakeHandler(HttpStatusCode.OK, "not json at all"), "gemini")));
        Assert.Null(await Read(Reader(new FakeHandler(HttpStatusCode.OK, """{"candidates":[]}"""), "gemini")));
    }

    // OpenAI first, Claude as the safety net; the handler routes by host and counts the calls.
    private sealed class RoutingHandler(HttpStatusCode openAiStatus) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            var answer = Escape(Answer);
            var (status, body) = host.Contains("openai")
                ? (openAiStatus, "{\"choices\":[{\"message\":{\"content\":" + answer + "}}]}")
                : (HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":" + answer + "}]}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static AiInvoiceReader ChainReader(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new InvoiceReaderOptions
            {
                Provider = "openai", Model = "gpt-6-luna", ApiKey = "k1", ReasoningEffort = "low",
                Fallback = new InvoiceReaderEndpoint { Provider = "claude", Model = "claude-haiku-5-5", ApiKey = "k2" },
            }),
            [new GeminiInvoiceProvider(), new OpenAiInvoiceProvider(), new ClaudeInvoiceProvider()],
            NullLogger<AiInvoiceReader>.Instance);

    [Fact]
    public async Task MainModelWorks_FallbackIsNeverCalled()
    {
        var handler = new RoutingHandler(HttpStatusCode.OK);

        Assert.NotNull(await Read(ChainReader(handler)));
        Assert.Equal(["api.openai.com"], handler.Hosts);
    }

    [Fact]
    public async Task MainModelFails_FallbackAnswersInstead()
    {
        var handler = new RoutingHandler(HttpStatusCode.ServiceUnavailable);

        var result = await Read(ChainReader(handler), "application/pdf");

        Assert.Equal(118.5m, result!.Amount);
        Assert.Equal(["api.openai.com", "api.anthropic.com"], handler.Hosts);
    }

    [Fact]
    public async Task OpenAiRequest_CarriesReasoningEffort_OnlyWhenConfigured()
    {
        var with = new FakeHandler(HttpStatusCode.OK, "{}");
        var reader = new AiInvoiceReader(new HttpClient(with),
            Options.Create(new InvoiceReaderOptions { Provider = "openai", Model = "gpt-6-luna", ApiKey = "k", ReasoningEffort = "low" }),
            [new OpenAiInvoiceProvider()], NullLogger<AiInvoiceReader>.Instance);
        await Read(reader);
        Assert.Contains("\"reasoning_effort\":\"low\"", with.SentBody);

        var without = new FakeHandler(HttpStatusCode.OK, "{}");
        await Read(Reader(without, "openai"));
        Assert.DoesNotContain("reasoning_effort", without.SentBody);
    }

    [Fact]
    public async Task PdfIsSentAsDocument_ForClaude()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        await Read(Reader(handler, "claude"), "application/pdf");

        Assert.Contains("\"document\"", handler.SentBody);
    }

    [Fact]
    public void Parser_ToleratesCodeFencesAndStringNumbers()
    {
        var result = InvoiceAnswerParser.Parse("```json\n{\"amount\": \"1,234.50 ₪\", \"date\": \"2026-01-02\", \"supplier\": null}\n```");

        Assert.Equal(1234.50m, result!.Amount);
        Assert.Equal(new DateOnly(2026, 1, 2), result.Date);
        Assert.Null(result.Supplier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I could not read this invoice.")]
    [InlineData("{}")]
    [InlineData("""{"amount": null, "date": "03/04/2026"}""")]
    [InlineData("{broken")]
    public void Parser_ReturnsNull_WhenNothingUsableIsThere(string text) =>
        Assert.Null(InvoiceAnswerParser.Parse(text));

    [Fact]
    public void Prompt_ListsTheOwnersCategories_AndAsksForWhatWasBought()
    {
        var prompt = InvoiceAnswerParser.BuildPrompt(Types);

        Assert.Contains("אריזה | חומרי גלם", prompt);
        Assert.Contains("WHAT was bought", prompt);
    }
}
