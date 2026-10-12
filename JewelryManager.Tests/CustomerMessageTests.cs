using JewelryManager.Api.Features.Orders;
using JewelryManager.Api.Features.Shopify;

namespace JewelryManager.Tests;

public class CustomerMessageTests
{
    [Fact]
    public void TheMessage_CarriesTheRealDetails_AndKeepsTheOwnersWording()
    {
        var text = CustomerMessageTemplate.Build("דנה כהן", "טבעת AHAVA (48)", new DateOnly(2026, 10, 12), 2200m);

        Assert.Contains("• שם המזמין/ה: דנה כהן", text);
        Assert.Contains("• התכשיט: טבעת AHAVA (48)", text);
        Assert.Contains("• תאריך ההזמנה: 12.10.2026", text);
        Assert.Contains("• מקדמה ששולמה: 2,200 ₪", text); // deposit wording unless it is known to be the whole price

        var full = CustomerMessageTemplate.Build("דנה כהן", "טבעת", new DateOnly(2026, 10, 12), 2200m, paidInFull: true);
        Assert.Contains("• סכום ששולם: 2,200 ₪", full);
        Assert.DoesNotContain("מקדמה", full.Split('\n').First(l => l.Contains("₪")));
        Assert.StartsWith("היי🤍", text);
        Assert.Contains("תוך עד 21 ימי עסקים", text);
        Assert.Contains("תכשיט שיוצר בהזמנה אישית אינו ניתן להחזרה או להחלפה.", text);
        Assert.DoesNotContain("[", text); // no placeholder is left behind
    }

    [Theory]
    [InlineData("052-1234567", "972521234567")]
    [InlineData("0521234567", "972521234567")]
    [InlineData("+972 52-123-4567", "972521234567")]
    [InlineData("00972521234567", "972521234567")]
    [InlineData("+1 (415) 555-0100", "14155550100")]
    [InlineData("12", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WhatsAppNumber_GivesDigitsWithTheCountryCode(string? phone, string? expected) =>
        Assert.Equal(expected, CustomerMessageTemplate.WhatsAppNumber(phone));

    [Fact]
    public void Parse_ReadsThePhone_FromTheCustomerFirst_ThenTheAddresses()
    {
        const string body = """
            { "id": 1, "name": "#1", "total_price": "10.00",
              "billing_address": { "phone": "050-0000001" },
              "shipping_address": { "phone": "050-0000002" },
              "customer": { "first_name": "דנה", "last_name": "כהן", "phone": "+972501234567" },
              "line_items": [ { "id": 5, "title": "טבעת", "quantity": 1, "price": "10.00" } ] }
            """;

        Assert.Equal("+972501234567", ShopifyOrderParser.Parse(body)!.Phone);
        Assert.Equal("050-0000002", ShopifyOrderParser.Parse(body.Replace("\"phone\": \"+972501234567\"", "\"x\": 1"))!.Phone);
        Assert.Null(ShopifyOrderParser.Parse("""{ "id": 1, "total_price": "1", "line_items": [ { "id": 5, "title": "t", "quantity": 1, "price": "1" } ] }""")!.Phone);
    }
}
