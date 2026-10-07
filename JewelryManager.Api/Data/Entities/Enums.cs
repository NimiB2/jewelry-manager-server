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
