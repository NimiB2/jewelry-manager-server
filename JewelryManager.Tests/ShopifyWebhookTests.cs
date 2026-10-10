using System.Security.Cryptography;
using System.Text;
using JewelryManager.Api.Features.Shopify;

namespace JewelryManager.Tests;

public class ShopifyWebhookTests
{
    private const string Secret = "shpss_test_secret";

    private const string Sample = """
        {
          "id": 820982911946154508,
          "name": "#1001",
          "created_at": "2026-10-09T23:30:00+03:00",
          "currency": "ILS",
          "total_price": "395.73",
          "total_discounts": "20.00",
          "note": "לארוז למתנה",
          "customer": { "first_name": "דנה", "last_name": "כהן" },
          "line_items": [
            { "id": 111, "title": "טבעת כסף", "variant_title": "מידה 7", "sku": "RING-7", "quantity": 2, "price": "150.00" },
            { "id": 222, "title": "שרשרת", "variant_title": null, "sku": "", "quantity": 1, "price": "95.73" }
          ]
        }
        """;

    private static string Sign(string body, string secret = Secret) =>
        Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(body)));

    // ── Signature ────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_Valid_WhenSignedWithTheSecret() =>
        Assert.True(ShopifyWebhookVerifier.IsValid(Encoding.UTF8.GetBytes(Sample), Sign(Sample), Secret));

    [Fact]
    public void Signature_Invalid_WhenBodyWasChanged() =>
        Assert.False(ShopifyWebhookVerifier.IsValid(Encoding.UTF8.GetBytes(Sample + " "), Sign(Sample), Secret));

    [Fact]
    public void Signature_Invalid_WhenSignedWithAnotherSecret() =>
        Assert.False(ShopifyWebhookVerifier.IsValid(Encoding.UTF8.GetBytes(Sample), Sign(Sample, "other"), Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 !!")]
    public void Signature_Invalid_WhenHeaderMissingOrBroken(string? header) =>
        Assert.False(ShopifyWebhookVerifier.IsValid(Encoding.UTF8.GetBytes(Sample), header, Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Signature_Invalid_WhenNoSecretIsConfigured(string? secret) =>
        Assert.False(ShopifyWebhookVerifier.IsValid(Encoding.UTF8.GetBytes(Sample), Sign(Sample, ""), secret));

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsAnOrder()
    {
        var order = ShopifyOrderParser.Parse(Sample)!;

        Assert.Equal("820982911946154508", order.ExternalId);
        Assert.Equal("#1001", order.DisplayName);
        Assert.Equal("דנה כהן", order.Customer);
        Assert.Equal(395.73m, order.Total);
        Assert.Equal(20m, order.TotalDiscounts);
        Assert.Equal("ILS", order.Currency);
        Assert.Equal("לארוז למתנה", order.Note);
        // The shop's own calendar day, not converted to UTC.
        Assert.Equal(new DateOnly(2026, 10, 9), order.Date);

        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(new IncomingLine("111", "טבעת כסף", "מידה 7", "RING-7", 2, 150m), order.Lines[0]);
        Assert.Null(order.Lines[1].VariantTitle);
        Assert.Null(order.Lines[1].Sku);
    }

    [Fact]
    public void Parse_FallsBackToTheShippingNameWhenThereIsNoCustomer()
    {
        var json = """{"id":1,"total_price":"10","shipping_address":{"name":"רונית לוי"},"line_items":[{"id":2,"title":"x","quantity":1,"price":"10"}]}""";

        Assert.Equal("רונית לוי", ShopifyOrderParser.Parse(json)!.Customer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"id":1,"total_price":"10","line_items":[]}""")]
    [InlineData("""{"id":1,"total_price":"10","line_items":[{"id":2,"title":"x","quantity":0,"price":"10"}]}""")]
    [InlineData("""{"id":1,"total_price":"10","line_items":[{"id":2,"title":"x","quantity":1,"price":"-5"}]}""")]
    [InlineData("""{"id":1,"total_price":"abc","line_items":[{"id":2,"title":"x","quantity":1,"price":"10"}]}""")]
    [InlineData("""{"total_price":"10","line_items":[{"id":2,"title":"x","quantity":1,"price":"10"}]}""")]
    public void Parse_RejectsAnythingThatIsNotAUsableOrder(string json) =>
        Assert.Null(ShopifyOrderParser.Parse(json));
}
