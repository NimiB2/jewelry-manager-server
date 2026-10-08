using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Incomes;

/// <summary>
/// The income row of an order. It follows the order while the order can still change, and is frozen
/// once the order is completed (a completed order is locked, so it is simply never applied again).
/// </summary>
public static class OrderIncomeFactory
{
    public static Income Create(Order order, IEnumerable<OrderLineItem> lines)
    {
        var income = new Income
        {
            Id = Guid.NewGuid(), BusinessId = order.BusinessId, OrderId = order.Id,
            Category = IncomeCategory.Sales, CreatedAt = DateTime.UtcNow,
        };
        Apply(income, order, lines);
        return income;
    }

    public static void Apply(Income income, Order order, IEnumerable<OrderLineItem> lines)
    {
        income.Date = order.Date;
        income.Description = string.IsNullOrWhiteSpace(order.Customer)
            ? $"הזמנה {order.Number}"
            : $"הזמנה {order.Number} · {order.Customer}";
        income.Amount = order.FinalAmount;
        income.WorkHours = lines.Sum(i => i.WorkHours * i.Quantity);
        income.LaborHourRate = order.LaborHourRate;
        income.UpdatedAt = DateTime.UtcNow;
    }
}
