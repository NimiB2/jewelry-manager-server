using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Orders.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Features.Orders;

public class OrdersService(AppDbContext db, CurrentUserAccessor tenant)
{
    // Test orders are numbered from 500, real orders from 1000, each series per business.
    private const int TestSeriesStart = 500;
    private const int RealSeriesStart = 1000;

    public async Task<OrdersListResponse> GetOrdersAsync(string? status, string? search, DateOnly? from, DateOnly? to)
    {
        var businessId = tenant.GetBusinessId();

        var query = db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.BusinessId == businessId && !o.IsDeleted);

        query = status switch
        {
            null or "" or "active" => query.Where(o => o.Status != OrderStatus.Completed),
            "completed" => query.Where(o => o.Status == OrderStatus.Completed),
            "all" => query,
            _ => throw new BadRequestException("Unknown status filter"),
        };

        if (from is { } fromDate) query = query.Where(o => o.Date >= fromDate);
        if (to is { } toDate) query = query.Where(o => o.Date <= toDate);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

            query = int.TryParse(term, out var number)
                ? query.Where(o => o.Number == number || (o.Customer != null && EF.Functions.ILike(o.Customer, pattern, "\\")))
                : query.Where(o => o.Customer != null && EF.Functions.ILike(o.Customer, pattern, "\\"));
        }

        var orders = await query
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.Number)
            .ToListAsync();

        return new OrdersListResponse(
            orders.Select(ToResponse).ToList(),
            new OrdersSummary(orders.Count, orders.Sum(o => o.FinalAmount)));
    }

    /// <summary>The years that have at least one order, newest first (for the year picker).</summary>
    public async Task<List<int>> GetYearsAsync()
    {
        var businessId = tenant.GetBusinessId();
        return await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && !o.IsDeleted)
            .Select(o => o.Date.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync();
    }

    public async Task<OrderResponse> GetOrderAsync(Guid id) =>
        ToResponse(await FindOrThrowAsync(id, asNoTracking: true));

    public async Task<OrderResponse> CreateOrderAsync(SaveOrderDto dto)
    {
        var businessId = tenant.GetBusinessId();
        if (dto.Items.Any(i => i.LineId is not null))
            throw new BadRequestException("A new order cannot reference existing lines");

        await using var tx = await db.Database.BeginTransactionAsync();

        // Two orders created at the same moment must not be handed the same number.
        var lockKey = $"orders:{businessId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))");

        var settings = await LoadSnapshotSettingsAsync();
        var number = await NextNumberAsync(businessId, IsTestCustomer(dto.Customer, settings.TestPrefix));

        var now = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Number = number,
            Source = OrderSource.Manual, Status = OrderStatus.New,
            LaborHourRate = settings.LaborHourRate, CreatedAt = now, UpdatedAt = now,
        };
        ApplyDetails(order, dto);

        var lines = await BuildNewLinesAsync(dto.Items, settings);
        order.Items = lines.Select(l => l.Line).ToList();
        ApplyAmounts(order, order.Items, dto.Discount);

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(order.Id);
    }

    public async Task<OrderResponse> UpdateOrderAsync(Guid id, SaveOrderDto dto)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        if (order.IsCompleted)
            throw new BadRequestException("A completed order is locked; move it back to an earlier status to edit it");

        var settings = await LoadSnapshotSettingsAsync();

        // Existing lines keep their frozen snapshot; only quantity and price may change.
        var byId = order.Items.ToDictionary(i => i.Id);
        var kept = new List<OrderLineItem>();
        for (var index = 0; index < dto.Items.Count; index++)
        {
            var input = dto.Items[index];
            if (input.LineId is not { } lineId) continue;

            if (!byId.TryGetValue(lineId, out var line))
                throw new BadRequestException("Order line not found");

            line.Quantity = input.Quantity;
            if (input.UnitPrice is { } price) line.UnitPrice = Math.Round(price, 2);
            line.SortOrder = index;
            kept.Add(line);
        }

        var removed = order.Items.Where(i => !kept.Contains(i)).ToList();
        db.OrderLineItems.RemoveRange(removed);

        var fresh = await BuildNewLinesAsync(
            dto.Items.Where(i => i.LineId is null).ToList(), settings,
            positions: dto.Items.Select((item, index) => (item, index)).Where(p => p.item.LineId is null).Select(p => p.index).ToList());
        foreach (var (line, _) in fresh)
        {
            line.OrderId = order.Id;
            db.OrderLineItems.Add(line);
        }

        ApplyDetails(order, dto);
        ApplyAmounts(order, kept.Concat(fresh.Select(f => f.Line)).ToList(), dto.Discount);
        order.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    public async Task<OrderResponse> UpdateStatusAsync(Guid id, OrderStatus status)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);

        if (order.Status != status)
        {
            order.Status = status;
            // The stage exists only while the order is in progress, and always starts at the first one.
            order.PreparationStage = status == OrderStatus.InProgress ? (await GetStageNamesAsync()).FirstOrDefault() : null;
            order.IsCompleted = status == OrderStatus.Completed;
            order.CompletedDate = order.IsCompleted ? DateTime.UtcNow : null;
            order.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        return await GetOrderAsync(id);
    }

    /// <summary>Moves an in-progress order to the next stage; after the last stage it becomes Ready.</summary>
    public async Task<OrderResponse> AdvanceStageAsync(Guid id)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        if (order.Status != OrderStatus.InProgress)
            throw new BadRequestException("Only an order in progress has a preparation stage");

        var stages = await GetStageNamesAsync();
        if (stages.Count == 0)
            throw new BadRequestException("No preparation stages are defined in settings");

        var current = order.PreparationStage is null ? -1 : stages.IndexOf(order.PreparationStage);
        if (current >= 0 && current + 1 >= stages.Count)
        {
            order.Status = OrderStatus.Ready;
            order.PreparationStage = null;
        }
        else
        {
            // A stage that was renamed or removed in settings restarts from the first one.
            order.PreparationStage = stages[current + 1];
        }

        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    // Allowed even on a completed order: the receipt is often sent after the work is done.
    public async Task<OrderResponse> SetReceiptSentAsync(Guid id, bool receiptSent)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        order.ReceiptSent = receiptSent;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    public async Task DeleteOrderAsync(Guid id)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        order.IsDeleted = true;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static void ApplyDetails(Order order, SaveOrderDto dto)
    {
        order.Date = dto.Date;
        order.Customer = string.IsNullOrWhiteSpace(dto.Customer) ? null : dto.Customer.Trim();
        order.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
    }

    private static void ApplyAmounts(Order order, IEnumerable<OrderLineItem> lines, OrderDiscountDto? discount)
    {
        var amount = lines.Sum(l => Math.Round(l.UnitPrice * l.Quantity, 2));
        var final = amount;

        if (discount is not null)
        {
            switch (discount.Mode)
            {
                case DiscountMode.Percent:
                    if (discount.Value > 100) throw new BadRequestException("A discount cannot exceed 100%");
                    final = Math.Round(amount * (1 - discount.Value / 100), 2);
                    break;
                case DiscountMode.FinalAmount:
                    if (discount.Value > amount) throw new BadRequestException("The final amount cannot be higher than the order total");
                    final = Math.Round(discount.Value, 2);
                    break;
            }
        }

        order.Amount = amount;
        order.FinalAmount = final;
        order.HasDiscount = final < amount;
        order.DiscountReason = order.HasDiscount && !string.IsNullOrWhiteSpace(discount?.Reason) ? discount!.Reason!.Trim() : null;
    }

    private async Task<List<(OrderLineItem Line, int Index)>> BuildNewLinesAsync(
        List<OrderItemInputDto> inputs, SnapshotSettings settings, List<int>? positions = null)
    {
        if (inputs.Count == 0) return [];

        var businessId = tenant.GetBusinessId();
        var ids = inputs.Select(i => i.ProductId ?? throw new BadRequestException("Each new line needs a product")).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => p.BusinessId == businessId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        return inputs.Select((input, i) =>
        {
            if (!products.TryGetValue(input.ProductId!.Value, out var product))
                throw new BadRequestException("Product not found");

            var line = new OrderLineItem
            {
                Id = Guid.NewGuid(), BusinessId = businessId, ProductId = product.Id,
                Name = product.Name, Type = product.Type, Material = product.Material,
                UnitPrice = Math.Round(input.UnitPrice ?? product.SitePrice, 2),
                Quantity = input.Quantity,
                WorkHours = settings.MaterialHours.GetValueOrDefault(product.Material) + product.AdditionalWorkHours,
                // Position in the request, so the order of the lines is the order she arranged.
                SortOrder = positions?[i] ?? i,
            };
            return (line, line.SortOrder);
        }).ToList();
    }

    private async Task<int> NextNumberAsync(Guid businessId, bool isTest)
    {
        // Deleted orders keep their numbers, so a number is never handed out twice.
        var numbers = db.Orders.Where(o => o.BusinessId == businessId);

        if (isTest)
        {
            var max = await numbers.Where(o => o.Number < RealSeriesStart).MaxAsync(o => (int?)o.Number);
            var next = (max ?? TestSeriesStart - 1) + 1;
            if (next >= RealSeriesStart) throw new BadRequestException("The test order numbers are used up");
            return next;
        }

        var maxReal = await numbers.Where(o => o.Number >= RealSeriesStart).MaxAsync(o => (int?)o.Number);
        return (maxReal ?? RealSeriesStart - 1) + 1;
    }

    private static bool IsTestCustomer(string? customer, string? prefix) =>
        !string.IsNullOrWhiteSpace(prefix)
        && customer is not null
        && customer.TrimStart().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<SnapshotSettings> LoadSnapshotSettingsAsync()
    {
        var businessId = tenant.GetBusinessId();

        var settings = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BusinessId == businessId)
            ?? throw new NotFoundException("Settings not found for this business");

        var hours = await db.Materials.AsNoTracking()
            .Where(m => m.BusinessId == businessId)
            .ToDictionaryAsync(m => m.Name, m => m.LaborHours);

        return new SnapshotSettings(settings.LaborHourRate, settings.TestOrderPrefix, hours);
    }

    private async Task<List<string>> GetStageNamesAsync()
    {
        var businessId = tenant.GetBusinessId();
        return await db.PreparationStages.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.Name)
            .ToListAsync();
    }

    private async Task<Order> FindOrThrowAsync(Guid id, bool asNoTracking)
    {
        var businessId = tenant.GetBusinessId();
        var query = db.Orders.Include(o => o.Items).AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();

        return await query.FirstOrDefaultAsync(o => o.Id == id && o.BusinessId == businessId && !o.IsDeleted)
            ?? throw new NotFoundException("Order not found");
    }

    private static OrderResponse ToResponse(Order o)
    {
        var discount = o.Amount - o.FinalAmount;

        return new OrderResponse(
            o.Id, o.Number, o.Number < RealSeriesStart, o.Date, o.Customer,
            o.Amount, o.FinalAmount, o.HasDiscount, discount,
            o.Amount > 0 ? Math.Round(discount / o.Amount * 100, 2) : 0,
            o.DiscountReason, o.Source, o.ReceiptSent, o.Status, o.PreparationStage, o.Notes,
            o.IsCompleted, o.CompletedDate,
            o.Items.OrderBy(i => i.SortOrder)
                .Select(i => new OrderItemResponse(
                    i.Id, i.ProductId, i.Name, i.Type, i.Material, i.UnitPrice, i.Quantity,
                    Math.Round(i.UnitPrice * i.Quantity, 2), i.WorkHours))
                .ToList());
    }

    private record SnapshotSettings(decimal LaborHourRate, string TestPrefix, Dictionary<string, decimal> MaterialHours);
}
