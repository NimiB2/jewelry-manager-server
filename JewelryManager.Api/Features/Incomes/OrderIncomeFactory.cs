using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Incomes;

/// <summary>Builds the frozen income row for a completed order (the order must have its Items loaded).</summary>
public static class OrderIncomeFactory
{
    public static Income Create(Order order)
    {
        var completed = order.CompletedDate ?? DateTime.UtcNow;
        var now = DateTime.UtcNow;

        return new Income
        {
            Id = Guid.NewGuid(), BusinessId = order.BusinessId, OrderId = order.Id,
            Date = DateOnly.FromDateTime(completed),
            Category = IncomeCategory.Sales,
            Description = string.IsNullOrWhiteSpace(order.Customer)
                ? $"הזמנה {order.Number}"
                : $"הזמנה {order.Number} · {order.Customer}",
            Amount = order.FinalAmount,
            WorkHours = order.Items.Sum(i => i.WorkHours * i.Quantity),
            LaborHourRate = order.LaborHourRate,
            CreatedAt = now, UpdatedAt = now,
        };
    }
}
