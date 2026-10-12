namespace JewelryManager.Api.Features.Orders.Dtos;

/// <summary>One item of an old store order. Title is the store's item name, which may end in " - variant".</summary>
public record HistoricalLine(string Title, string? Sku, int Quantity, decimal UnitPrice);

/// <summary>
/// An order that already happened in the store, read from its export. Fulfilled orders arrive finished;
/// the ones still to make arrive as new orders.
/// </summary>
public record HistoricalOrder(
    string ExternalId,
    string DisplayName,
    DateOnly Date,
    string? Customer,
    decimal Discount,
    string? DiscountReason,
    string? Note,
    bool Fulfilled,
    DateOnly? CompletedOn,
    IReadOnlyList<HistoricalLine> Lines);

/// <summary>What importing one old order did (or would do, in a dry run).</summary>
public record HistoricalImportOutcome(bool Created, int MatchedLines, List<string> UnmatchedTitles);

/// <summary>What tying old order lines to catalog products did (or would do).</summary>
public record RelinkResult(int LinesLinked, int Orders, List<string> StillWithoutProduct);

/// <summary>An item name the store used in old orders, and the catalog product it stands for.</summary>
public record ProductAlias(string StoreTitle, string Name, string? Type, string? Material);
