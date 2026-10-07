using System.ComponentModel.DataAnnotations;

namespace JewelryManager.Api.Features.Orders.Dtos;

public enum DiscountMode
{
    // Value is a percentage of the order total.
    Percent,

    // Value is the final amount the customer pays.
    FinalAmount,
}

/// <summary>
/// A line in the request. With LineId it is an existing line (its frozen name/type/material stay;
/// only quantity and price can change). Without it, ProductId picks a catalog product to copy.
/// </summary>
public record OrderItemInputDto(
    Guid? LineId,
    Guid? ProductId,
    [Range(1, 1_000)] int Quantity,
    // Null = the product's current site price.
    [Range(0, 100_000_000)] decimal? UnitPrice);

public record OrderDiscountDto(
    DiscountMode Mode,
    [Range(0, 100_000_000)] decimal Value,
    [MaxLength(300)] string? Reason);

/// <summary>Body of POST /orders and PUT /orders/{id}.</summary>
public record SaveOrderDto(
    [MaxLength(200)] string? Customer,
    DateOnly Date,
    [MaxLength(2_000)] string? Notes,
    [Required, MinLength(1)] List<OrderItemInputDto> Items,
    OrderDiscountDto? Discount);

public record UpdateOrderStatusDto(Data.Entities.OrderStatus Status);

public record UpdateReceiptSentDto(bool ReceiptSent);

public record UpdateOrderStageDto([Required, MaxLength(200)] string Stage);
