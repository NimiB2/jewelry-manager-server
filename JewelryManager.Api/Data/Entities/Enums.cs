namespace JewelryManager.Api.Data.Entities;

public enum Role
{
    SuperAdmin,
    Owner,
    Employee,
}

public enum OrderSource
{
    Manual,
    Shopify,
}

/// <summary>What the store told us about an order after we received it. Only ever a warning for the owner.</summary>
public enum StoreOrderAlert
{
    None,
    Updated,
    Cancelled,
    Deleted,
}

public enum OrderStatus
{
    New,
    InProgress,
    Ready,
    Completed,
}

public enum ExpenseCategory
{
    Fixed,
    Variable,
}

public enum IncomeCategory
{
    Sales,
    Other,
}

public enum WorkTaskStatus
{
    New,
    InProgress,
    Completed,
}
