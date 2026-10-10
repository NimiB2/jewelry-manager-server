using System.Net;
using System.Text;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Features.Shopify;
using Microsoft.Extensions.Options;

namespace JewelryManager.Tests;

public class ShopifyCatalogTests
{
    private const string PageOne = """
        {"data":{"products":{
          "pageInfo":{"hasNextPage":true,"endCursor":"CURSOR-1"},
          "nodes":[
            {"id":"gid://shopify/Product/111","title":" Silver Ring ","variants":{"nodes":[
              {"title":"Size 6","sku":"R6","price":"150.00"},
              {"title":"Size 7","sku":"R7","price":"160.00"}]}},
            {"id":"gid://shopify/Product/222","title":"Pearl Necklace","variants":{"nodes":[
              {"title":"Default Title","sku":"","price":"320.50"}]}}
          ]}}}
        """;

    private const string PageTwo = """
        {"data":{"products":{
          "pageInfo":{"hasNextPage":false,"endCursor":null},
          "nodes":[
            {"id":"gid://shopify/Product/333","title":"Gold Pendant","variants":{"nodes":[
              {"title":"Default Title","sku":null,"price":"899"}]}},
            {"id":"gid://shopify/Product/444","title":"Broken price","variants":{"nodes":[
              {"title":"Default Title","sku":null,"price":"abc"}]}}
          ]}}}
        """;

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePage_ReadsProductsWithEveryVariantPrice()
    {
        var (products, hasNext, cursor) = ShopifyCatalogParser.ParsePage(PageOne);

        Assert.True(hasNext);
        Assert.Equal("CURSOR-1", cursor);
        Assert.Equal(2, products.Count);

        var ring = products[0];
        Assert.Equal("111", ring.ExternalId);
        Assert.Equal("Silver Ring", ring.Title);
        Assert.Equal([150m, 160m], ring.Variants.Select(v => v.Price));
        Assert.Equal(150m, ring.LowestPrice);
        Assert.Equal("Size 7", ring.Variants[1].Title);
        Assert.Equal("R7", ring.Variants[1].Sku);

        // A product without options says "Default Title"; that is dropped, and an empty SKU is no SKU.
        Assert.Equal("", products[1].Variants[0].Title);
        Assert.Null(products[1].Variants[0].Sku);
    }

    [Fact]
    public void ParsePage_SkipsAProductWhoseOnlyVariantHasNoUsablePrice()
    {
        var (products, hasNext, _) = ShopifyCatalogParser.ParsePage(PageTwo);

        Assert.False(hasNext);
        Assert.Equal(["Gold Pendant"], products.Select(p => p.Title));
    }

    [Fact]
    public void ParsePage_RefusesAnErrorsAnswer() =>
        Assert.Throws<BadRequestException>(() =>
            ShopifyCatalogParser.ParsePage("""{"errors":[{"message":"Access denied"}]}"""));

    // ── Client: paging, token in the header only ─────────────────────────────

    private sealed class PagingHandler(params string[] pages) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var body = pages[Math.Min(Requests.Count, pages.Length) - 1];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static ShopifyCatalogClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new ShopifyOptions
        {
            ShopDomain = " https://my-shop.myshopify.com/ ", AccessToken = " shpat_secret\n", ApiVersion = "2026-07",
        }));

    [Fact]
    public async Task Client_FollowsThePagesAndKeepsTheTokenInTheHeader()
    {
        var handler = new PagingHandler(PageOne, PageTwo);

        var products = await Client(handler).GetActiveProductsAsync();

        Assert.Equal(["111", "222", "333"], products.Select(p => p.ExternalId));
        Assert.Equal(2, handler.Requests.Count);

        var first = handler.Requests[0];
        Assert.Equal("https://my-shop.myshopify.com/admin/api/2026-07/graphql.json", first.RequestUri!.ToString());
        Assert.Equal("shpat_secret", first.Headers.GetValues("X-Shopify-Access-Token").Single());
        Assert.DoesNotContain("shpat_secret", first.RequestUri.ToString());
        Assert.DoesNotContain("shpat_secret", handler.Bodies[0]);

        // The second request continues from the cursor of the first page.
        Assert.Contains("CURSOR-1", handler.Bodies[1]);
    }

    [Fact]
    public async Task Client_ExplainsAFailingStore()
    {
        var handler = new StatusHandler(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<BadRequestException>(() => Client(handler).GetActiveProductsAsync());
    }

    [Fact]
    public async Task Client_WithoutConnectionSettings_RefusesBeforeAnyRequest()
    {
        var handler = new PagingHandler(PageOne);
        var client = new ShopifyCatalogClient(new HttpClient(handler), Options.Create(new ShopifyOptions()));

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<BadRequestException>(() => client.GetActiveProductsAsync());
        Assert.Empty(handler.Requests);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    // ── Options ──────────────────────────────────────────────────────────────

    [Fact]
    public void Options_TrimPastedValues_AndKnowWhatTheyCanDo()
    {
        var options = new ShopifyOptions { ShopDomain = "https://shop.myshopify.com/\n", AccessToken = "  t ", WebhookSecret = " s\r\n" };

        Assert.Equal("shop.myshopify.com", options.ShopDomain);
        Assert.Equal("t", options.AccessToken);
        Assert.Equal("s", options.WebhookSecret);
        Assert.True(options.CanReadCatalog);
        Assert.True(options.CanReceiveOrders);
        Assert.False(new ShopifyOptions { ShopDomain = "x" }.CanReadCatalog);
        Assert.False(new ShopifyOptions { ShopDomain = "x" }.CanReceiveOrders);
    }
}
