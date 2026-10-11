using JewelryManager.Api.Features.Shopify;

namespace JewelryManager.Tests;

public class ShopifyOrdersCsvTests
{
    private const string Header =
        "Name,Email,Financial Status,Fulfillment Status,Fulfilled at,Discount Code,Discount Amount,Created at,Cancelled at,Refunded Amount,"
        + "Lineitem quantity,Lineitem name,Lineitem price,Lineitem sku,Billing Name,Shipping Name,Notes,Id";

    private static string Csv(params string[] rows) => "﻿" + Header + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    [Fact]
    public void ReadsAnOrderWithSeveralItems_FromItsFirstRow()
    {
        var csv = Csv(
            "#1001,a@b.c,paid,fulfilled,2023-04-20 10:00:00 +0300,SPRING10,20.00,2023-04-18 23:30:00 +0300,,0.00,2,Gold Ring - 54,150.00,R-54,דנה כהן,,\"שורה 1\nשורה 2\",900",
            "#1001,,,,,,,,,,1,\"Silver \"\"Star\"\" Necklace\",100.50,,,,,");

        var result = ShopifyOrdersCsvReader.Read(csv);
        var order = Assert.Single(result.Orders);

        Assert.Equal("900", order.ExternalId);
        Assert.Equal("#1001", order.DisplayName);
        Assert.Equal("דנה כהן", order.Customer);
        // The shop's own calendar day, not converted to UTC.
        Assert.Equal(new DateOnly(2023, 4, 18), order.Date);
        Assert.Equal(20m, order.Discount);
        Assert.Equal("קופון SPRING10", order.DiscountReason);
        Assert.Equal("שורה 1\nשורה 2", order.Note);
        Assert.True(order.Fulfilled);
        Assert.Equal(new DateOnly(2023, 4, 20), order.CompletedOn);

        Assert.Equal(2, order.Lines.Count);
        Assert.Equal("Gold Ring - 54", order.Lines[0].Title);
        Assert.Equal(2, order.Lines[0].Quantity);
        Assert.Equal(150m, order.Lines[0].UnitPrice);
        Assert.Equal("Silver \"Star\" Necklace", order.Lines[1].Title);
        Assert.Equal(100.50m, order.Lines[1].UnitPrice);
    }

    [Fact]
    public void AnUnfulfilledOrder_IsNotCompleted_AndAnEmptyBillingNameFallsBackToShipping()
    {
        var csv = Csv("#1002,,paid,unfulfilled,,,0.00,2026-09-27 08:00:00 +0300,,0.00,1,Ring,100.00,,,רינה לוי,,901");

        var order = Assert.Single(ShopifyOrdersCsvReader.Read(csv).Orders);

        Assert.False(order.Fulfilled);
        Assert.Null(order.CompletedOn);
        Assert.Equal("רינה לוי", order.Customer);
        Assert.Null(order.DiscountReason);
    }

    [Fact]
    public void OnlyPaidUncancelledUnrefundedOrdersComeIn_AndTheRestAreReported()
    {
        var csv = Csv(
            "#1,,pending,unfulfilled,,,0,2026-01-01 10:00:00 +0200,,0.00,1,Ring,10.00,,x,,,1",
            "#2,,paid,fulfilled,,,0,2026-01-02 10:00:00 +0200,2026-01-03 10:00:00 +0200,0.00,1,Ring,10.00,,x,,,2",
            "#3,,paid,fulfilled,,,0,2026-01-03 10:00:00 +0200,,10.00,1,Ring,10.00,,x,,,3",
            "#4,,paid,fulfilled,,,0,2026-01-04 10:00:00 +0200,,0.00,1,Ring,10.00,,x,,,4");

        var result = ShopifyOrdersCsvReader.Read(csv);

        Assert.Equal(["4"], result.Orders.Select(o => o.ExternalId));
        Assert.Equal(["#1", "#2", "#3"], result.Skipped.Select(s => s.Name));
    }

    [Fact]
    public void OrdersComeOutOldestFirst()
    {
        var csv = Csv(
            "#3,,paid,fulfilled,,,0,2026-03-01 10:00:00 +0200,,0.00,1,Ring,10.00,,x,,,30",
            "#1,,paid,fulfilled,,,0,2025-01-01 10:00:00 +0200,,0.00,1,Ring,10.00,,x,,,10",
            "#2,,paid,fulfilled,,,0,2025-06-01 10:00:00 +0200,,0.00,1,Ring,10.00,,x,,,20");

        Assert.Equal(["#1", "#2", "#3"], ShopifyOrdersCsvReader.Read(csv).Orders.Select(o => o.DisplayName));
    }

    [Fact]
    public void AFileThatIsNotTheOrdersExport_IsRefused() =>
        Assert.Throws<FormatException>(() => ShopifyOrdersCsvReader.Read("Handle,Title\r\nring,Ring\r\n"));
}
