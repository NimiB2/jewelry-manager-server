using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Search.Dtos;

public record OrderHit(Guid Id, int Number, string? Customer, string? ExternalName, DateOnly Date, decimal FinalAmount, OrderStatus Status, bool IsPendingApproval);

public record ProductHit(Guid Id, string Name, string Type, string Material, decimal SitePrice, string? ShopifyName);

public record ExpenseHit(Guid Id, DateOnly Date, string Description, string? Supplier, decimal Amount, bool HasInvoice);

/// <summary>What a search found, a few of each kind, newest or most relevant first.</summary>
public record SearchResponse(string Query, List<OrderHit> Orders, List<ProductHit> Products, List<ExpenseHit> Expenses);
