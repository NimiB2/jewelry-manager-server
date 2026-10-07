using JewelryManager.Api.Features.Orders.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Orders;

[ApiController]
[Route("orders")]
public class OrdersController(OrdersService service) : ControllerBase
{
    /// <param name="status">active (default), completed or all.</param>
    /// <param name="search">Customer name or order number.</param>
    /// <param name="from">First order date, inclusive (yyyy-MM-dd).</param>
    /// <param name="to">Last order date, inclusive (yyyy-MM-dd).</param>
    [HttpGet]
    public Task<OrdersListResponse> GetOrders(string? status, string? search, DateOnly? from, DateOnly? to) =>
        service.GetOrdersAsync(status, search, from, to);

    [HttpGet("years")]
    public Task<List<int>> GetYears() => service.GetYearsAsync();

    [HttpGet("{id:guid}")]
    public Task<OrderResponse> GetOrder(Guid id) => service.GetOrderAsync(id);

    [HttpPost]
    public Task<OrderResponse> CreateOrder(SaveOrderDto dto) => service.CreateOrderAsync(dto);

    [HttpPut("{id:guid}")]
    public Task<OrderResponse> UpdateOrder(Guid id, SaveOrderDto dto) => service.UpdateOrderAsync(id, dto);

    [HttpPatch("{id:guid}/status")]
    public Task<OrderResponse> UpdateStatus(Guid id, UpdateOrderStatusDto dto) =>
        service.UpdateStatusAsync(id, dto.Status);

    [HttpPost("{id:guid}/advance-stage")]
    public Task<OrderResponse> AdvanceStage(Guid id) => service.AdvanceStageAsync(id);

    [HttpPatch("{id:guid}/stage")]
    public Task<OrderResponse> SetStage(Guid id, UpdateOrderStageDto dto) => service.SetStageAsync(id, dto.Stage);

    [HttpPatch("{id:guid}/receipt-sent")]
    public Task<OrderResponse> SetReceiptSent(Guid id, UpdateReceiptSentDto dto) =>
        service.SetReceiptSentAsync(id, dto.ReceiptSent);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteOrder(Guid id)
    {
        await service.DeleteOrderAsync(id);
        return NoContent();
    }
}
