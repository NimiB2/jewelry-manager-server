namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// Stable identifiers for the fees the pricing formula depends on, independent of their
/// renamable display names (same idea as Collection.Key).
/// </summary>
public static class FeeKeys
{
    public const string CardFee = "cardFee";
    public const string Vat = "vat";
    public const string FixedExpenses = "fixedExpenses";

    public static readonly string[] Required = [CardFee, Vat, FixedExpenses];
}
