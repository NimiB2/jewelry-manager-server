using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Statements;

/// <summary>One row read from a statement. Money going out is positive.</summary>
public record ParsedStatementLine(DateOnly Date, decimal Amount, string Description, string? Reference);

public interface IStatementFileParser
{
    List<ParsedStatementLine> Parse(string fileName, Stream content, StatementSource source);
}

/// <summary>
/// Reads an Excel or CSV statement. Banks and card companies each lay their files out differently, so the
/// columns are found by their Hebrew/English header text instead of a fixed position.
/// </summary>
public class StatementFileParser : IStatementFileParser
{
    private static readonly string[] DateFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd-MM-yyyy", "yyyy-MM-dd"];

    public List<ParsedStatementLine> Parse(string fileName, Stream content, StatementSource source)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var rows = extension switch
        {
            ".xlsx" or ".xlsm" => ReadExcel(content),
            ".csv" or ".txt" => ReadCsv(content),
            _ => throw new BadRequestException("אפשר להעלות רק קובץ Excel (xlsx) או CSV"),
        };

        var (headerIndex, map) = FindHeader(rows, source);
        var lines = new List<ParsedStatementLine>();

        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var date = ParseDate(Cell(row, map.Date));
            if (date is null) continue;

            var amount = ReadAmount(row, map, source);
            if (amount is null or 0m) continue;

            var description = Cell(row, map.Description).Trim();
            var reference = Cell(row, map.Reference).Trim();
            lines.Add(new ParsedStatementLine(
                date.Value, Math.Round(amount.Value, 2), description.Length == 0 ? "ללא תיאור" : Truncate(description, 300),
                reference.Length == 0 ? null : Truncate(reference, 60)));
        }

        if (lines.Count == 0) throw new BadRequestException("לא נמצאו בקובץ שורות עם תאריך וסכום");
        return lines;
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    private record ColumnMap(int Date, int Description, int Reference, int Debit, int Credit, int Amount);

    private static (int HeaderIndex, ColumnMap Map) FindHeader(List<string[]> rows, StatementSource source)
    {
        for (var i = 0; i < Math.Min(rows.Count, 40); i++)
        {
            var headers = rows[i].Select(h => h.Trim().ToLowerInvariant()).ToArray();

            var date = Best(headers, [["עסקה", "רכישה", "transaction"], ["תאריך", "date"], ["ערך"], ["חיוב"]], "תאריך", "date");
            if (date < 0) continue;

            var debit = Best(headers, [["חובה"]], "חובה", "debit");
            var credit = Best(headers, [["זכות"]], "זכות", "credit");
            var amount = Best(headers, [["סכום חיוב"], ["סכום"], ["amount"]], "סכום", "amount");
            if (debit < 0 && credit < 0 && amount < 0) continue;

            var description = Best(headers, [["בית עסק"], ["תיאור", "תאור", "description"], ["פרטים"]], "בית עסק", "תיאור", "תאור", "פרטים", "description");
            var reference = Best(headers, [["אסמכתא", "אסמכתה", "reference"]], "אסמכתא", "אסמכתה", "reference");
            return (i, new ColumnMap(date, description, reference, debit, credit, amount));
        }

        throw new BadRequestException("לא הצלחתי לזהות את העמודות בקובץ (תאריך, תיאור, סכום). אפשר לשלוח לי דוגמה ואתאים.");
    }

    // The first header holding any of the words, tried in priority order (a transaction date beats a billing date).
    private static int Best(string[] headers, string[][] priority, params string[] mustContainOneOf)
    {
        foreach (var words in priority)
        {
            for (var c = 0; c < headers.Length; c++)
            {
                if (headers[c].Length == 0) continue;
                if (words.Any(headers[c].Contains) && mustContainOneOf.Any(headers[c].Contains)) return c;
            }
        }

        // Fallback: any header with one of the base words.
        for (var c = 0; c < headers.Length; c++)
            if (headers[c].Length > 0 && mustContainOneOf.Any(headers[c].Contains)) return c;
        return -1;
    }

    private static decimal? ReadAmount(string[] row, ColumnMap map, StatementSource source)
    {
        if (map.Debit >= 0 || map.Credit >= 0)
        {
            var debit = ParseNumber(Cell(row, map.Debit)) ?? 0m;
            var credit = ParseNumber(Cell(row, map.Credit)) ?? 0m;
            if (debit != 0 || credit != 0) return Math.Abs(debit) - Math.Abs(credit);
        }

        var single = ParseNumber(Cell(row, map.Amount));
        if (single is null) return null;

        // Bank files show money out as a negative number; card files list the charges as positive ones.
        return source == StatementSource.Bank ? -single : single;
    }

    private static string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";

    // ── Values ────────────────────────────────────────────────────────────────

    private static DateOnly? ParseDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        // A date with a time ("15/03/2026 10:22") keeps only the date.
        var spaceAt = text.IndexOf(' ');
        if (spaceAt > 0) text = text[..spaceAt];

        return DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static decimal? ParseNumber(string text)
    {
        text = text.Trim().Replace("₪", "").Replace(" ", "").Replace(" ", "").Replace(",", "").Replace("−", "-");
        if (text.Length == 0) return null;

        var negative = false;
        if (text.StartsWith('(') && text.EndsWith(')')) { negative = true; text = text[1..^1]; }
        if (text.EndsWith('-')) { negative = true; text = text[..^1]; }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    // ── Files ─────────────────────────────────────────────────────────────────

    private static List<string[]> ReadExcel(Stream content)
    {
        try
        {
            using var workbook = new XLWorkbook(content);
            var sheet = workbook.Worksheets.FirstOrDefault() ?? throw new BadRequestException("הקובץ ריק");
            var range = sheet.RangeUsed();
            if (range is null) return [];

            var rows = new List<string[]>();
            foreach (var row in range.Rows())
            {
                rows.Add(row.Cells().Select(CellText).ToArray());
            }
            return rows;
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw new BadRequestException("לא הצלחתי לקרוא את קובץ ה-Excel");
        }
    }

    private static string CellText(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsDateTime) return value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (value.IsNumber) return value.GetNumber().ToString(CultureInfo.InvariantCulture);
        return value.ToString();
    }

    private static List<string[]> ReadCsv(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var text = Decode(buffer.ToArray());

        var firstLines = text.Split('\n').Take(10).ToArray();
        var delimiter = new[] { ',', ';', '\t' }.MaxBy(d => firstLines.Sum(l => l.Count(ch => ch == d)));

        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                if (fields.Any(f => f.Length > 0)) rows.Add(fields.ToArray());
                fields.Clear();
            }
            else field.Append(ch);
        }

        fields.Add(field.ToString());
        if (fields.Any(f => f.Length > 0)) rows.Add(fields.ToArray());
        return rows;
    }

    // Israeli exports are often Windows-1255 rather than UTF-8.
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1255).GetString(bytes);
        }
    }
}
