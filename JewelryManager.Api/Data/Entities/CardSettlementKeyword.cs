namespace JewelryManager.Api.Data.Entities;

/// <summary>A word in a bank line that marks it as a credit card settlement (Visa, Max, Cal...). Editable by her.</summary>
public class CardSettlementKeyword
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string Keyword { get; set; } = "";
}
