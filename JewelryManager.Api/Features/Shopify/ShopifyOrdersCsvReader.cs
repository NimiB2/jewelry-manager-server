using System.Globalization;
using System.Text;
using JewelryManager.Api.Features.Orders.Dtos;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>An order of the export that was left out on purpose, and why.</summary>
public record SkippedStoreOrder(string Name, string Reason);

public record StoreOrdersCsv(List<HistoricalOrder> Orders, List<SkippedStoreOrder> Skipped);

/// <summary>
/// Reads the store's "Export orders" CSV. Pure (no database, no network), so it is tested on its own.
/// One order spans one row per item; the order-level columns are read from its first row.
/// </summary>
public static class ShopifyOrdersCsvReader
{
    private static readonly string[] Required = ["Name", "Id", "Created at", "Lineitem quantity", "Lineitem name", "Lineitem price"];

    public static StoreOrdersCsv Read(string csv)
    {
        var rows = Parse(csv.TrimStart('﻿'));
        if (rows.Count == 0) throw new FormatException("The file is empty");

        var header = rows[0].Select((name, i) => (name: name.Trim(), i)).GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.First().i);
        foreach (var column in Required)
        {
            if (!header.ContainsKey(column)) throw new FormatException($"The column \"{column}\" is missing. Is this the store's orders export?");
        }

        string Cell(List<string> row, string column) =>
            header.TryGetValue(column, out var i) && i < row.Count ? row[i].Trim() : "";

        // Group the rows of one order, keeping the order the file lists them in.
        var groups = new List<(string Name, List<List<string>> Rows)>();
        foreach (var row in rows.Skip(1))
        {
            var name = Cell(row, "Name");
            if (name.Length == 0) continue;

            if (groups.Count > 0 && groups[^1].Name == name) groups[^1].Rows.Add(row);
            else groups.Add((name, [row]));
        }

        var orders = new List<HistoricalOrder>();
        var skipped = new List<SkippedStoreOrder>();

        foreach (var (name, group) in groups)
        {
            var first = group[0];

            // Only completed sales belong in the books.
            if (!string.Equals(Cell(first, "Financial Status"), "paid", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new SkippedStoreOrder(name, $"not paid ({Cell(first, "Financial Status")})"));
                continue;
            }

            if (Cell(first, "Cancelled at").Length > 0)
            {
                skipped.Add(new SkippedStoreOrder(name, "cancelled"));
                continue;
            }

            if (Money(Cell(first, "Refunded Amount")) > 0)
            {
                skipped.Add(new SkippedStoreOrder(name, "refunded"));
                continue;
            }

            var id = Cell(first, "Id");
            var date = Day(Cell(first, "Created at"));
            if (id.Length == 0 || date is null)
            {
                skipped.Add(new SkippedStoreOrder(name, "no order id or date"));
                continue;
            }

            var lines = new List<HistoricalLine>();
            var broken = false;
            foreach (var row in group)
            {
                var title = Cell(row, "Lineitem name");
                if (title.Length == 0) continue;

                if (!int.TryParse(Cell(row, "Lineitem quantity"), out var quantity) || quantity is <= 0 or > 1_000
                    || Money(Cell(row, "Lineitem price")) is not { } price)
                {
                    broken = true;
                    break;
                }

                var sku = Cell(row, "Lineitem sku");
                lines.Add(new HistoricalLine(Cut(title, 300), sku.Length == 0 ? null : Cut(sku, 100), quantity, price));
            }

            if (broken || lines.Count == 0)
            {
                skipped.Add(new SkippedStoreOrder(name, "an item has no valid quantity or price"));
                continue;
            }

            var code = Cell(first, "Discount Code");
            var fulfilled = string.Equals(Cell(first, "Fulfillment Status"), "fulfilled", StringComparison.OrdinalIgnoreCase);
            var customer = Cell(first, "Billing Name");
            if (customer.Length == 0) customer = Cell(first, "Shipping Name");

            orders.Add(new HistoricalOrder(
                id,
                Cut(name, 50),
                date.Value,
                customer.Length == 0 ? null : Cut(customer, 200),
                Money(Cell(first, "Discount Amount")) ?? 0m,
                code.Length == 0 ? null : $"קופון {Cut(code, 100)}",
                Cell(first, "Notes") is { Length: > 0 } note ? Cut(note, 2_000) : null,
                fulfilled,
                fulfilled ? Day(Cell(first, "Fulfilled at")) : null,
                lines));
        }

        // Oldest first, so the order numbers follow the order the sales happened in.
        orders = orders.OrderBy(o => o.Date).ThenBy(o => o.ExternalId.Length).ThenBy(o => o.ExternalId, StringComparer.Ordinal).ToList();
        return new StoreOrdersCsv(orders, skipped);
    }

    // "2023-04-18 12:12:58 +0300": the shop's own calendar day is the first ten characters.
    private static DateOnly? Day(string text) =>
        text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    private static decimal? Money(string text)
    {
        if (text.Length == 0) return null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount is >= 0 and < 100_000_000m
            ? amount
            : null;
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;

    // A small RFC 4180 reader: quoted cells may hold commas, quotes ("") and line breaks.
    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
