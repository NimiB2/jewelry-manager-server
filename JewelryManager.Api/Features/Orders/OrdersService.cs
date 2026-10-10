using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Incomes;
using JewelryManager.Api.Features.Orders.Dtos;
using JewelryManager.Api.Features.Shopify;
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

        // Orders waiting for approval have their own list; they are not part of the books yet.
        var query = db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.BusinessId == businessId && !o.IsDeleted && !o.IsPendingApproval);

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

        var testPrefix = await GetTestPrefixAsync();
        var orders = await query
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.Number)
            .ToListAsync();

        return new OrdersListResponse(
            orders.Select(o => ToResponse(o, testPrefix)).ToList(),
            new OrdersSummary(orders.Count, orders.Sum(o => o.FinalAmount)));
    }

    /// <summary>The years that have at least one order, newest first (for the year picker).</summary>
    public async Task<List<int>> GetYearsAsync()
    {
        var businessId = tenant.GetBusinessId();
        return await db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && !o.IsDeleted && !o.IsPendingApproval)
            .Select(o => o.Date.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync();
    }

    public async Task<OrderResponse> GetOrderAsync(Guid id) =>
        ToResponse(await FindOrThrowAsync(id, asNoTracking: true), await GetTestPrefixAsync());

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
        await SyncBooksAsync(order, order.Items, isNew: true);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(order.Id);
    }

    public async Task<OrderResponse> UpdateOrderAsync(Guid id, SaveOrderDto dto)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        EnsureApproved(order);
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
            line.Note = CleanNote(input.Note);
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
        var finalLines = kept.Concat(fresh.Select(f => f.Line)).ToList();
        ApplyAmounts(order, finalLines, dto.Discount);
        order.UpdatedAt = DateTime.UtcNow;

        await SyncBooksAsync(order, finalLines, isNew: false);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    public async Task<OrderResponse> UpdateStatusAsync(Guid id, OrderStatus status)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        EnsureApproved(order);

        // The books need a receipt for every finished sale.
        if (status == OrderStatus.Completed && !order.ReceiptSent)
            throw new BadRequestException("לא ניתן להשלים הזמנה לפני שנשלחה קבלה");

        if (order.Status != status)
        {
            order.Status = status;
            // The stage exists only while the order is in progress, and always starts at the first one.
            order.PreparationStage = status == OrderStatus.InProgress ? (await GetStageNamesAsync()).FirstOrDefault() : null;
            order.IsCompleted = status == OrderStatus.Completed;
            order.CompletedDate = order.IsCompleted ? DateTime.UtcNow : null;
            order.UpdatedAt = DateTime.UtcNow;

            await SyncTaskStatusAsync(order);
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        return await GetOrderAsync(id);
    }

    /// <summary>Moves an in-progress order to the next stage; after the last stage it becomes Ready.</summary>
    public async Task<OrderResponse> AdvanceStageAsync(Guid id)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        EnsureApproved(order);
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

    /// <summary>Jumps an in-progress order straight to any stage defined in settings (forward or back).</summary>
    public async Task<OrderResponse> SetStageAsync(Guid id, string stage)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        EnsureApproved(order);
        if (order.Status != OrderStatus.InProgress)
            throw new BadRequestException("Only an order in progress has a preparation stage");

        if (!(await GetStageNamesAsync()).Contains(stage))
            throw new BadRequestException("Unknown preparation stage");

        order.PreparationStage = stage;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    // A completed order always has its receipt, so the mark can only be removed after reopening it.
    public async Task<OrderResponse> SetReceiptSentAsync(Guid id, bool receiptSent)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        EnsureApproved(order);
        if (!receiptSent && order.IsCompleted)
            throw new BadRequestException("לא ניתן לבטל את סימון הקבלה בהזמנה שהושלמה");
        order.ReceiptSent = receiptSent;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    // ── Orders that arrive from the online store ─────────────────────────────

    /// <summary>
    /// Creates an order from a store order delivered by a verified webhook. It waits for the owner's approval:
    /// no income and no task until then. Returns false when this store order was already imported.
    /// </summary>
    public async Task<bool> ImportStoreOrderAsync(IncomingOrder incoming)
    {
        var businessId = tenant.GetBusinessId();

        await using var tx = await db.Database.BeginTransactionAsync();
        var lockKey = $"orders:{businessId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))");

        // The store retries a delivery it thinks failed; the same order must never be created twice.
        if (await db.Orders.AnyAsync(o => o.BusinessId == businessId && o.ExternalId == incoming.ExternalId))
            return false;

        var settings = await LoadSnapshotSettingsAsync();
        var number = await NextNumberAsync(businessId, IsTestCustomer(incoming.Customer, settings.TestPrefix));

        // Products that already know their store identity: by the store's id first, then by the store name.
        var known = await db.Products
            .Where(p => p.BusinessId == businessId && (p.ShopifyProductId != null || p.ShopifyName != null))
            .ToListAsync();
        var byId = known.Where(p => p.ShopifyProductId != null).ToDictionary(p => p.ShopifyProductId!);
        var byName = known.Where(p => p.ShopifyName != null)
            .GroupBy(p => p.ShopifyName!.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var now = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(), BusinessId = businessId, Number = number,
            Date = incoming.Date,
            Customer = string.IsNullOrWhiteSpace(incoming.Customer) ? null : incoming.Customer.Trim(),
            Notes = string.IsNullOrWhiteSpace(incoming.Note) ? null : incoming.Note.Trim(),
            Source = OrderSource.Shopify, Status = OrderStatus.New, IsPendingApproval = true,
            ExternalId = incoming.ExternalId, ExternalName = incoming.DisplayName,
            LaborHourRate = settings.LaborHourRate, CreatedAt = now, UpdatedAt = now,
        };

        for (var index = 0; index < incoming.Lines.Count; index++)
        {
            var source = incoming.Lines[index];

            Product? product = null;
            if (source.ExternalProductId is { } storeId && byId.TryGetValue(storeId, out var byStoreId))
            {
                product = byStoreId;
            }
            else if (byName.TryGetValue(source.Title.Trim().ToLowerInvariant(), out var byStoreName))
            {
                product = byStoreName;

                // A match by name teaches the store's id, so the next order matches without the name.
                if (product.ShopifyProductId is null && source.ExternalProductId is { } learned && !byId.ContainsKey(learned))
                {
                    product.ShopifyProductId = learned;
                    product.UpdatedAt = now;
                    byId[learned] = product;
                }
            }

            var line = new OrderLineItem
            {
                Id = Guid.NewGuid(), BusinessId = businessId, SortOrder = index,
                Quantity = source.Quantity, UnitPrice = Math.Round(source.UnitPrice, 2),
                Note = CleanNote(source.VariantTitle), ExternalProductId = source.ExternalProductId,
            };
            ApplyProductSnapshot(line, product, source.Title, settings);
            order.Items.Add(line);
        }

        var amount = order.Items.Sum(l => Math.Round(l.UnitPrice * l.Quantity, 2));
        var discount = Math.Min(Math.Max(incoming.TotalDiscounts, 0), amount);
        order.Amount = amount;
        order.FinalAmount = amount - discount;
        order.HasDiscount = discount > 0;
        order.DiscountReason = discount > 0 ? "הנחה מהחנות" : null;

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();
        return true;
    }

    public async Task<List<OrderResponse>> GetPendingOrdersAsync()
    {
        var businessId = tenant.GetBusinessId();
        var testPrefix = await GetTestPrefixAsync();

        var orders = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.BusinessId == businessId && !o.IsDeleted && o.IsPendingApproval)
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.Number)
            .ToListAsync();

        return orders.Select(o => ToResponse(o, testPrefix)).ToList();
    }

    /// <summary>
    /// Ties a line of a pending order to a catalog product and remembers the match, so the same store
    /// product is recognised from now on (also in the other pending orders). A product that is already
    /// tied to a different store product raises a conflict until the caller confirms with Replace.
    /// </summary>
    public async Task<OrderResponse> LinkLineAsync(Guid orderId, Guid lineId, Guid productId, bool replace)
    {
        var businessId = tenant.GetBusinessId();
        var order = await FindOrThrowAsync(orderId, asNoTracking: false);
        if (!order.IsPendingApproval)
            throw new BadRequestException("ההזמנה כבר אושרה");

        var target = order.Items.FirstOrDefault(i => i.Id == lineId)
            ?? throw new NotFoundException("Order line not found");

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.BusinessId == businessId)
            ?? throw new NotFoundException("Product not found");

        var storeId = target.ExternalProductId;
        var storeName = target.Name;

        Product? previousOwner = null;
        if (storeId is not null)
        {
            if (!replace && product.ShopifyProductId is not null && product.ShopifyProductId != storeId)
                throw new ConflictException(
                    $"המוצר \"{product.Name}\" כבר מקושר למוצר \"{product.ShopifyName ?? "אחר"}\" בשופיפי", "PRODUCT_ALREADY_LINKED");

            previousOwner = await db.Products.FirstOrDefaultAsync(
                p => p.BusinessId == businessId && p.Id != product.Id && p.ShopifyProductId == storeId);
            if (previousOwner is not null && !replace)
                throw new ConflictException(
                    $"המוצר \"{storeName}\" בשופיפי כבר מקושר ל-\"{previousOwner.Name}\"", "STORE_PRODUCT_ALREADY_LINKED");
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;

        // The old owner lets go first: the store id is unique, and the check runs per statement.
        if (previousOwner is not null)
        {
            previousOwner.ShopifyProductId = null;
            previousOwner.ShopifyName = null;
            previousOwner.UpdatedAt = now;
            await db.SaveChangesAsync();
        }

        if (storeId is not null) product.ShopifyProductId = storeId;
        if (string.IsNullOrWhiteSpace(product.ShopifyName)
            && !await db.Products.AnyAsync(p => p.BusinessId == businessId && p.Id != product.Id && p.ShopifyName != null
                                                && p.ShopifyName.ToLower() == storeName.ToLower()))
            product.ShopifyName = storeName;
        product.UpdatedAt = now;

        var settings = await LoadSnapshotSettingsAsync();

        // Every pending line of the same store product is tied at once; this line always is.
        var siblings = storeId is null
            ? []
            : await db.OrderLineItems
                .Where(l => l.BusinessId == businessId && l.ProductId == null && l.ExternalProductId == storeId
                            && l.Order.IsPendingApproval && !l.Order.IsDeleted)
                .ToListAsync();
        if (siblings.All(l => l.Id != target.Id)) siblings.Add(target);

        foreach (var line in siblings)
            ApplyProductSnapshot(line, product, line.Name, settings);

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(orderId);
    }

    /// <summary>Approves a store order: it becomes a regular order, with its income and task like a manual one.</summary>
    public async Task<OrderResponse> ApproveAsync(Guid id)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        if (!order.IsPendingApproval)
            throw new BadRequestException("ההזמנה כבר אושרה");

        if (order.Items.Any(i => i.ProductId is null))
            throw new BadRequestException("יש בהזמנה פריטים שעדיין לא זוהו. יש לבחור להם מוצר מהרשימה");

        order.IsPendingApproval = false;
        order.UpdatedAt = DateTime.UtcNow;

        await SyncBooksAsync(order, order.Items, isNew: true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await GetOrderAsync(id);
    }

    private static void EnsureApproved(Order order)
    {
        if (order.IsPendingApproval)
            throw new BadRequestException("ההזמנה ממתינה לאישור. יש לאשר אותה קודם");
    }

    // The frozen copy of a catalog product on an order line. Without a product the line keeps the store's wording.
    private static void ApplyProductSnapshot(OrderLineItem line, Product? product, string storeTitle, SnapshotSettings settings)
    {
        line.ProductId = product?.Id;
        line.Name = product?.Name ?? (storeTitle.Length > 200 ? storeTitle[..200] : storeTitle);
        line.Type = product?.Type ?? "";
        line.Material = product?.Material ?? "";
        line.WorkHours = product is null
            ? 0
            : settings.MaterialHours.GetValueOrDefault(product.Material) + product.AdditionalWorkHours;
    }

    public async Task DeleteOrderAsync(Guid id)
    {
        var order = await FindOrThrowAsync(id, asNoTracking: false);
        order.IsDeleted = true;
        order.UpdatedAt = DateTime.UtcNow;

        // A deleted order disappears from the app, so its income and its automatic task go with it.
        await RemoveBooksAsync(order.Id);
        await db.SaveChangesAsync();
    }

    // A new order adds an income record and a task; later edits keep both in step, and a completed
    // order is locked, so its income is frozen. Demo orders never reach the books.
    private async Task SyncBooksAsync(Order order, IEnumerable<OrderLineItem> lines, bool isNew)
    {
        var businessId = tenant.GetBusinessId();
        var isDemo = IsTestCustomer(order.Customer, await GetTestPrefixAsync());

        var income = isNew ? null : await db.Incomes.FirstOrDefaultAsync(i => i.BusinessId == businessId && i.OrderId == order.Id);
        var task = isNew ? null : await db.Tasks.FirstOrDefaultAsync(t => t.BusinessId == businessId && t.OrderId == order.Id && t.IsAutomatic);

        if (isDemo)
        {
            if (income is not null) db.Incomes.Remove(income);
            if (task is not null) db.Tasks.Remove(task);
            return;
        }

        if (income is null)
        {
            db.Incomes.Add(OrderIncomeFactory.Create(order, lines));

            // Only an order that was not on the books yet gets a task, so it never gets a second one.
            if (task is null) db.Tasks.Add(NewOrderTask(order, lines));
            return;
        }

        OrderIncomeFactory.Apply(income, order, lines);
        if (task is not null)
        {
            task.Title = TaskTitle(order);
            task.Content = TaskContent(lines);
            task.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task SyncTaskStatusAsync(Order order)
    {
        var businessId = tenant.GetBusinessId();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.BusinessId == businessId && t.OrderId == order.Id && t.IsAutomatic);
        if (task is null) return;

        task.Status = order.Status switch
        {
            OrderStatus.New => WorkTaskStatus.New,
            OrderStatus.Completed => WorkTaskStatus.Completed,
            _ => WorkTaskStatus.InProgress,
        };
        task.CompletedAt = task.Status == WorkTaskStatus.Completed ? task.CompletedAt ?? DateTime.UtcNow : null;
        task.UpdatedAt = DateTime.UtcNow;
    }

    private async Task RemoveBooksAsync(Guid orderId)
    {
        var businessId = tenant.GetBusinessId();
        db.Incomes.RemoveRange(await db.Incomes.Where(i => i.BusinessId == businessId && i.OrderId == orderId).ToListAsync());
        db.Tasks.RemoveRange(await db.Tasks.Where(t => t.BusinessId == businessId && t.OrderId == orderId && t.IsAutomatic).ToListAsync());
    }

    private static TaskItem NewOrderTask(Order order, IEnumerable<OrderLineItem> lines)
    {
        var now = DateTime.UtcNow;
        return new TaskItem
        {
            Id = Guid.NewGuid(), BusinessId = order.BusinessId, OrderId = order.Id, IsAutomatic = true,
            Title = TaskTitle(order), Content = TaskContent(lines), Status = WorkTaskStatus.New,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    private static string TaskTitle(Order order) =>
        string.IsNullOrWhiteSpace(order.Customer) ? $"הזמנה {order.Number}" : $"הזמנה {order.Number} · {order.Customer}";

    private static string? TaskContent(IEnumerable<OrderLineItem> lines)
    {
        var text = string.Join(", ", lines.OrderBy(l => l.SortOrder).Select(l => $"{l.Name} × {l.Quantity}"));
        return text.Length == 0 ? null : text.Length > 4_000 ? text[..4_000] : text;
    }

    private static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

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
                Note = CleanNote(input.Note),
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

    public static bool IsTestCustomer(string? customer, string? prefix) =>
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

    private async Task<string?> GetTestPrefixAsync()
    {
        var businessId = tenant.GetBusinessId();
        return await db.Settings.AsNoTracking()
            .Where(s => s.BusinessId == businessId)
            .Select(s => s.TestOrderPrefix)
            .FirstOrDefaultAsync();
    }

    // A demo order is recognised by its customer name (decision 8 in the spec), so renaming a customer
    // to or from the test prefix changes the label immediately; the order number never changes.
    private static OrderResponse ToResponse(Order o, string? testPrefix)
    {
        var discount = o.Amount - o.FinalAmount;

        return new OrderResponse(
            o.Id, o.Number, IsTestCustomer(o.Customer, testPrefix), o.Date, o.Customer,
            o.Amount, o.FinalAmount, o.HasDiscount, discount,
            o.Amount > 0 ? Math.Round(discount / o.Amount * 100, 2) : 0,
            o.DiscountReason, o.Source, o.ReceiptSent, o.Status, o.PreparationStage, o.Notes,
            o.IsCompleted, o.CompletedDate,
            o.Items.OrderBy(i => i.SortOrder)
                .Select(i => new OrderItemResponse(
                    i.Id, i.ProductId, i.Name, i.Type, i.Material, i.UnitPrice, i.Quantity,
                    Math.Round(i.UnitPrice * i.Quantity, 2), i.WorkHours, i.Note,
                    i.ExternalProductId, o.IsPendingApproval && i.ProductId is null))
                .ToList(),
            o.ExternalName, o.IsPendingApproval);
    }

    private record SnapshotSettings(decimal LaborHourRate, string TestPrefix, Dictionary<string, decimal> MaterialHours);
}
