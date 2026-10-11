using JewelryManager.Api.Features.Shopify;

namespace JewelryManager.Tests;

public class ShopifyFingerprintTests
{
    private static string Order(string quantity = "2", string price = "150.00", string total = "395.73",
        string extra = "", string note = "לארוז") => $$"""
        {
          "id": 1, "name": "#1001", "created_at": "2026-10-09T23:30:00+03:00",
          "total_price": "{{total}}", "total_discounts": "0.00", "note": "{{note}}",
          "customer": { "first_name": "דנה", "last_name": "כהן" },
          {{extra}}
          "line_items": [ { "id": 111, "title": "טבעת", "quantity": {{quantity}}, "price": "{{price}}" } ]
        }
        """;

    private static string Print(string json) => ShopifyOrderParser.Parse(json)!.Fingerprint();

    [Fact]
    public void SameOrder_GivesTheSameFingerprint() => Assert.Equal(Print(Order()), Print(Order()));

    [Fact]
    public void StoreNoise_DoesNotChangeTheFingerprint() =>
        // Tags, fulfilment and payment status are none of the owner's concern here.
        Assert.Equal(Print(Order()), Print(Order(extra: "\"tags\": \"vip\", \"financial_status\": \"paid\", \"fulfillment_status\": \"fulfilled\",")));

    [Theory]
    [InlineData("3", "150.00", "395.73", "לארוז")]
    [InlineData("2", "160.00", "395.73", "לארוז")]
    [InlineData("2", "150.00", "400.00", "לארוז")]
    [InlineData("2", "150.00", "395.73", "בלי אריזה")]
    public void RealChange_ChangesTheFingerprint(string quantity, string price, string total, string note) =>
        Assert.NotEqual(Print(Order()), Print(Order(quantity, price, total, note: note)));
}
