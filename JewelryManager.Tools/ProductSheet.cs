using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Orders.Dtos;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tools;

public record SheetExportResult(int Products, int Additions, int NeedingDetails);

public record SheetUpdateResult(
    int Updated,
    int Unchanged,
    int Created,
    List<string> Changes,
    List<string> Problems,
    List<string> NotInFile,
    int Deleted);

/// <summary>One product as a line of the workbook. Marks say a cell was filled in for her to check.</summary>
public record SheetRow(
    Guid? Id,
    string Name,
    string Type,
    string Material,
    decimal? Weight,
    decimal? Hours,
    decimal? Price,
    string Additions,
    string Collections,
    string ShopifyName,
    decimal? Recommended,
    string Notes,
    string TypeMark = "",
    string MaterialMark = "",
    string NameMark = "");

/// <summary>
/// The whole catalog as one sheet of one Excel workbook, and back. The owner fills in what is missing and
/// the tool applies it through the same product service the app uses. A product is matched by the id in
/// the last column, so renaming a row renames the product instead of making a second one.
/// </summary>
public static class ProductSheet
{
    internal const string ProductsSheet = "מוצרים";
    internal const string AdditionsSheet = "תוספות";   // the old two-sheet layout only
    internal const string ListsSheet = "רשימות";       // the old two-sheet layout only

    // Columns, by header text, so the owner may move them around.
    internal const string HId = "מזהה (לא לשנות)";
    internal const string HName = "שם המוצר";
    internal const string HType = "סוג";
    internal const string HMaterial = "חומר";
    internal const string HWeight = "משקל (גרם)";
    internal const string HHours = "שעות עבודה נוספות";
    internal const string HPrice = "מחיר באתר";
    internal const string HAdditions = "תוספות";
    internal const string HCollections = "אוספים";
    internal const string HShopifyName = "שם בשופיפי";
    internal const string HRecommended = "מחיר מומלץ (חישוב, לא לערוך)";
    internal const string HMissing = "מה חסר (לא לערוך)";
    internal const string HNotes = "הערות שלי";

    // The old layout's addition columns.
    internal const string AProductId = "מזהה המוצר (לא לשנות)";
    internal const string AType = "סוג תוספת";
    internal const string ACustom = "שם חופשי";
    internal const string APrice = "מחיר";
    internal const string AQuantity = "כמות";

    private static readonly string[] DefaultTypes = ["טבעת", "עגילים", "שרשרת", "צמיד", "תליון", "סט", "אחר"];

    private static readonly XLColor Yellow = XLColor.FromHtml("#FFF59D");
    private static readonly XLColor Orange = XLColor.FromHtml("#FFE0B2");
    private static readonly XLColor Green = XLColor.FromHtml("#C8E6C9");
    private static readonly XLColor Grey = XLColor.WhiteSmoke;

    // ── The additions as one cell ────────────────────────────────────────────

    /// <summary>"אבן 150 x2; אחר:חריטה 15": type (and a free name after a colon), price, and a quantity when it is not 1.</summary>
    public static string FormatAdditions(IEnumerable<ProductAdditionDto> additions) =>
        string.Join("; ", additions.Select(a =>
        {
            var name = string.IsNullOrWhiteSpace(a.CustomName) ? a.TypeName : $"{a.TypeName}:{a.CustomName.Trim()}";
            var price = a.Price.ToString("0.##", CultureInfo.InvariantCulture);
            return a.Quantity > 1 ? $"{name} {price} x{a.Quantity}" : $"{name} {price}";
        }));

    private static readonly Regex AdditionPattern = new(
        @"^(?<name>.+?)\s+(?<price>\d+(?:[.,]\d+)?)(?:\s*[xX×*]\s*(?<qty>\d+))?\s*₪?$", RegexOptions.Compiled);

    /// <summary>Reads that text back. Anything it cannot read is reported, never guessed.</summary>
    public static (List<ProductAdditionDto> Additions, List<string> Errors) ParseAdditions(
        string? text, IReadOnlyDictionary<string, bool> typesAllowingCustomName)
    {
        var result = new List<ProductAdditionDto>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return (result, errors);

        foreach (var part in text.Split([';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = AdditionPattern.Match(part);
            if (!match.Success)
            {
                errors.Add($"לא הצלחתי לקרוא את התוספת '{part}' (הצורה: סוג מחיר, למשל: אבן 150 x2)");
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            string? custom = null;
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                custom = name[(colon + 1)..].Trim();
                name = name[..colon].Trim();
            }

            var type = typesAllowingCustomName.Keys.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            if (type is null)
            {
                errors.Add($"סוג תוספת לא מוכר '{name}'");
                continue;
            }

            var allowsCustom = typesAllowingCustomName[type];
            if (allowsCustom && string.IsNullOrWhiteSpace(custom))
            {
                errors.Add($"לתוספת '{type}' צריך שם חופשי (למשל {type}:חריטה 15)");
                continue;
            }

            var price = decimal.Parse(match.Groups["price"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var quantity = match.Groups["qty"].Success ? int.Parse(match.Groups["qty"].Value) : 1;
            result.Add(new ProductAdditionDto(type, allowsCustom ? custom : null, price, Math.Clamp(quantity, 1, 1_000)));
        }

        return (result, errors);
    }

    // ── Export ───────────────────────────────────────────────────────────────

    public static async Task<SheetExportResult> ExportAsync(AppDbContext db, Guid businessId, string path)
    {
        var tenant = ProductImporter.TenantFor(businessId);
        var products = (await new ProductsService(db, tenant, new PricingService(db, tenant)).GetProductsAsync()).Products;
        var collections = await new CollectionsService(db, tenant).GetCollectionsAsync();
        var collectionName = collections.ToDictionary(c => c.Id, c => c.Name);

        var rows = products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new SheetRow(
            p.Id, p.Name, p.Type, p.Material, p.Weight, p.AdditionalWorkHours, p.SitePrice,
            FormatAdditions(p.Additions),
            string.Join(", ", p.CollectionIds.Select(id => collectionName.GetValueOrDefault(id, "")).Where(n => n.Length > 0)),
            p.ShopifyName ?? "",
            p.Price is null ? null : Math.Round(p.Price.RecommendedPrice),
            "")).ToList();

        var materials = await db.Materials.AsNoTracking().Where(m => m.BusinessId == businessId)
            .OrderBy(m => m.SortOrder).Select(m => m.Name).ToListAsync();
        var types = products.Select(p => p.Type).Where(t => !string.IsNullOrWhiteSpace(t)).Concat(DefaultTypes).Distinct().OrderBy(t => t).ToList();

        Write(rows, materials, types, path);
        return new SheetExportResult(rows.Count, products.Sum(p => p.Additions.Count), products.Count(p => p.NeedsDetails));
    }

    // ── Writing the workbook ─────────────────────────────────────────────────

    /// <summary>One sheet, nothing else: the lists behind the drop-downs live inside the cells' own validation.</summary>
    public static void Write(IReadOnlyList<SheetRow> rows, IReadOnlyList<string> materials, IReadOnlyList<string> types, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(ProductsSheet);
        sheet.RightToLeft = true;

        string[] headers = [HName, HType, HMaterial, HWeight, HHours, HPrice, HAdditions, HCollections, HShopifyName, HRecommended, HMissing, HNotes, HId];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];

        var r = 2;
        foreach (var row in rows)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(row.Type)) missing.Add("סוג");
            if (string.IsNullOrWhiteSpace(row.Material)) missing.Add("חומר");
            if (row.Weight is null or 0) missing.Add("משקל");
            if (row.Price is null or 0) missing.Add("מחיר");

            sheet.Cell(r, 1).Value = row.Name;
            sheet.Cell(r, 2).Value = row.Type;
            sheet.Cell(r, 3).Value = row.Material;
            if (row.Weight is { } w) sheet.Cell(r, 4).Value = w;
            if (row.Hours is { } h) sheet.Cell(r, 5).Value = h;
            if (row.Price is { } p) sheet.Cell(r, 6).Value = p;
            sheet.Cell(r, 7).Value = row.Additions;
            sheet.Cell(r, 8).Value = row.Collections;
            sheet.Cell(r, 9).Value = row.ShopifyName;
            if (row.Recommended is { } rec) sheet.Cell(r, 10).Value = rec;
            sheet.Cell(r, 11).Value = missing.Count == 0 ? "מלא" : string.Join(", ", missing);
            sheet.Cell(r, 12).Value = row.Notes;
            sheet.Cell(r, 13).Value = row.Id?.ToString() ?? "";

            Mark(sheet.Cell(r, 1), row.NameMark, false);
            Mark(sheet.Cell(r, 2), row.TypeMark, string.IsNullOrWhiteSpace(row.Type));
            Mark(sheet.Cell(r, 3), row.MaterialMark, string.IsNullOrWhiteSpace(row.Material));
            if (row.Weight is null or 0) sheet.Cell(r, 4).Style.Fill.BackgroundColor = Yellow;
            if (row.Price is null or 0) sheet.Cell(r, 6).Style.Fill.BackgroundColor = Yellow;
            r++;
        }

        var last = Math.Max(r - 1, 2);
        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
        header.Style.Alignment.WrapText = true;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        foreach (var column in new[] { 10, 11, 13 }) sheet.Range(2, column, last, column).Style.Fill.BackgroundColor = Grey;
        sheet.Range(2, 4, last, 5).Style.NumberFormat.Format = "0.##";
        sheet.Range(2, 6, last, 6).Style.NumberFormat.Format = "#,##0.##";
        sheet.Range(2, 10, last, 10).Style.NumberFormat.Format = "#,##0";
        sheet.Range(1, 1, last, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(1);

        var widths = new[] { 34, 12, 16, 11, 12, 11, 36, 26, 34, 14, 16, 30, 14 };
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];

        // Short help, kept on the headers themselves so there is no second sheet.
        Note(sheet.Cell(1, 7), "תוספות (אבן, ציפוי...) בתא אחד, מופרדות בנקודה-פסיק: סוג, מחיר, וכמות אם היא לא 1. למשל: אבן 150 x2; אחר:חריטה 15");
        Note(sheet.Cell(1, 8), "שמות אוספים קיימים, מופרדים בפסיק. בלי אוסף: כללי.");
        Note(sheet.Cell(1, 12), "מקום להערות שלך. הטעינה למערכת מתעלמת מהעמודה הזו.");
        Note(sheet.Cell(1, 13), "לפי העמודה הזו המוצר מזוהה. לא לשנות. שורה חדשה בלי מזהה = מוצר חדש.");

        if (materials.Count > 0) AddList(sheet.Range(2, 3, last + 500, 3), materials, strict: true, "חומר לא מוכר: יש לבחור מהרשימה");
        if (types.Count > 0) AddList(sheet.Range(2, 2, last + 500, 2), types, strict: false, "");

        workbook.SaveAs(path);
    }

    private static void Mark(IXLCell cell, string mark, bool empty)
    {
        if (mark == "guess") cell.Style.Fill.BackgroundColor = Orange;
        else if (mark is "sure" or "new") cell.Style.Fill.BackgroundColor = Green;
        else if (empty) cell.Style.Fill.BackgroundColor = Yellow;
    }

    private static void Note(IXLCell cell, string text) => cell.CreateComment().AddText(text);

    private static void AddList(IXLRange range, IReadOnlyList<string> items, bool strict, string message)
    {
        var list = string.Join(",", items.Where(i => !i.Contains(',')));
        if (list.Length == 0 || list.Length > 250) return; // Excel limits an inline list to 255 characters

        var rule = range.CreateDataValidation();
        rule.List(list, true);
        if (strict)
        {
            rule.ErrorTitle = "ערך לא מוכר";
            rule.ErrorMessage = message;
        }
        else
        {
            rule.ErrorStyle = XLErrorStyle.Warning; // a new value is allowed, the list is only a help
        }
    }

    // ── Converting the old two-sheet file ────────────────────────────────────

    /// <summary>
    /// Turns a workbook in the earlier layout (products + additions + lists, as the owner may already have
    /// edited it) into the single-sheet one. Optionally adds a row for every item the old orders sold that
    /// the catalog has no product for, and ties catalog products to store names from a hints file.
    /// </summary>
    public static (int Rows, int FromOrders, int Hinted) Convert(
        string inPath, string outPath, IReadOnlyList<(string Title, decimal Price)>? soldItems, IReadOnlyList<ProductAlias>? hints)
    {
        using var workbook = new XLWorkbook(inPath);
        if (!workbook.TryGetWorksheet(ProductsSheet, out var sheet))
            throw new InvalidOperationException($"הגיליון '{ProductsSheet}' לא נמצא בקובץ");

        var columns = Columns(sheet);
        foreach (var needed in new[] { HName, HId })
        {
            if (!columns.ContainsKey(needed)) throw new InvalidOperationException($"העמודה '{needed}' חסרה");
        }

        // The additions of the old layout, one text per product.
        var additionsByProduct = new Dictionary<string, List<ProductAdditionDto>>();
        if (workbook.TryGetWorksheet(AdditionsSheet, out var additionsSheet))
        {
            var aCols = Columns(additionsSheet);
            foreach (var row in additionsSheet.RowsUsed().Skip(1))
            {
                var id = Text(row, aCols, AProductId);
                var type = Text(row, aCols, AType);
                if (id.Length == 0 || type.Length == 0) continue;

                var custom = Text(row, aCols, ACustom);
                var price = Number(row, aCols, APrice) ?? 0m;
                var quantity = (int)(Number(row, aCols, AQuantity) ?? 1m);
                if (!additionsByProduct.TryGetValue(id, out var list)) additionsByProduct[id] = list = [];
                list.Add(new ProductAdditionDto(type, custom.Length == 0 ? null : custom, price, Math.Max(quantity, 1)));
            }
        }

        string MarkOf(IXLCell cell) =>
            cell.Style.Fill.BackgroundColor == Orange ? "guess" : cell.Style.Fill.BackgroundColor == Green ? "sure" : "";

        var rows = new List<SheetRow>();
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var name = Text(row, columns, HName);
            if (name.Length == 0) continue;

            var id = Text(row, columns, HId);
            rows.Add(new SheetRow(
                Guid.TryParse(id, out var guid) ? guid : null,
                name,
                Text(row, columns, HType),
                Text(row, columns, HMaterial),
                Number(row, columns, HWeight),
                Number(row, columns, HHours),
                Number(row, columns, HPrice),
                additionsByProduct.TryGetValue(id, out var adds) ? FormatAdditions(adds) : Text(row, columns, HAdditions),
                Text(row, columns, HCollections),
                Text(row, columns, HShopifyName),
                Number(row, columns, HRecommended),
                Text(row, columns, HNotes),
                columns.TryGetValue(HType, out var tc) ? MarkOf(row.Cell(tc)) : "",
                columns.TryGetValue(HMaterial, out var mc) ? MarkOf(row.Cell(mc)) : ""));
        }

        // Catalog products that stand for an item the store knows by another name. A product without a store
        // name gets it. One that already has another name keeps it, and the old name is only an alias: the old
        // orders are tied to it (relink-orders), and no second product is made.
        var hinted = 0;
        var aliasTitles = new HashSet<string>();
        foreach (var hint in hints ?? [])
        {
            var index = rows.FindIndex(r => r.Name == hint.Name
                && (hint.Type is null || r.Type == hint.Type) && (hint.Material is null || r.Material == hint.Material));
            if (index < 0) continue;

            if (rows[index].ShopifyName.Length == 0)
            {
                rows[index] = rows[index] with
                {
                    ShopifyName = hint.StoreTitle,
                    Notes = Join(rows[index].Notes, "שם בשופיפי הוצע לפי ההזמנות הישנות. לבדוק"),
                };
            }
            else if (!string.Equals(rows[index].ShopifyName, hint.StoreTitle, StringComparison.OrdinalIgnoreCase))
            {
                rows[index] = rows[index] with
                {
                    Notes = Join(rows[index].Notes, $"בהזמנות ישנות נקרא: {hint.StoreTitle}"),
                };
            }

            aliasTitles.Add(Normalize(hint.StoreTitle));
            hinted++;
        }

        // Items the old orders sold that still have no product: one new row each, for her to confirm.
        var fromOrders = 0;
        if (soldItems is not null)
        {
            var known = rows.SelectMany(r => new[] { r.Name, r.ShopifyName }).Where(x => x.Length > 0)
                .Select(Normalize).Concat(aliasTitles).ToHashSet();

            foreach (var group in soldItems.Select(s => (Title: BaseTitle(s.Title), s.Price))
                         .GroupBy(s => Normalize(s.Title)).OrderBy(g => g.Key))
            {
                if (known.Contains(group.Key)) continue;

                var title = group.First().Title;
                var type = ReviewSheet.GuessType(title);
                var kind = ReviewSheet.GuessMaterialKind(title);
                rows.Add(new SheetRow(
                    null, title, type ?? "", kind == "gold" ? "זהב 14" : kind == "silver" ? "כסף" : "",
                    null, null, group.Max(g => g.Price), "", "לא באתר", title, null,
                    "מוצר שנמכר בהזמנות ישנות ואין לו מוצר בקטלוג. לאשר, להשלים משקל, או למחוק את השורה",
                    type is null ? "" : "guess", kind is null ? "" : "guess", "new"));
                fromOrders++;
            }
        }

        var materials = ReadList(workbook, 1);
        var types = ReadList(workbook, 4);
        if (types.Count == 0) types = rows.Select(r => r.Type).Where(t => t.Length > 0).Concat(DefaultTypes).Distinct().OrderBy(t => t).ToList();

        Write(rows, materials, types, outPath);
        return (rows.Count, fromOrders, hinted);
    }

    private static List<string> ReadList(XLWorkbook workbook, int column)
    {
        var result = new List<string>();
        if (workbook.TryGetWorksheet(ListsSheet, out var lists))
        {
            foreach (var cell in lists.Column(column).CellsUsed().Skip(1)) result.Add(cell.GetString().Trim());
        }

        return result;
    }

    // "Gold Ring - 54" is the product "Gold Ring" in size 54.
    internal static string BaseTitle(string storeName)
    {
        var name = storeName.Trim();
        var cut = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return cut > 0 ? name[..cut].Trim() : name;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "; " + b;

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // ── Import ───────────────────────────────────────────────────────────────

    public static async Task<SheetUpdateResult> ImportAsync(
        AppDbContext db, Guid businessId, string path, bool dryRun, bool deleteMissing = false)
    {
        var tenant = ProductImporter.TenantFor(businessId);
        var products = new ProductsService(db, tenant, new PricingService(db, tenant));

        var current = (await products.GetProductsAsync()).Products.ToDictionary(p => p.Id);
        var collectionId = (await new CollectionsService(db, tenant).GetCollectionsAsync())
            .ToDictionary(c => c.Name.Trim(), c => c.Id, StringComparer.OrdinalIgnoreCase);
        var materials = (await db.Materials.AsNoTracking().Where(m => m.BusinessId == businessId).Select(m => m.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additionTypes = (await db.ProductAdditionTypes.AsNoTracking().Where(t => t.BusinessId == businessId).ToListAsync())
            .ToDictionary(t => t.Name.Trim(), t => t.AllowsCustomName, StringComparer.OrdinalIgnoreCase);

        var changes = new List<string>();
        var problems = new List<string>();
        var seen = new HashSet<Guid>();
        int updated = 0, unchanged = 0, created = 0;

        using var workbook = new XLWorkbook(path);
        if (!workbook.TryGetWorksheet(ProductsSheet, out var sheet))
            throw new InvalidOperationException($"הגיליון '{ProductsSheet}' לא נמצא בקובץ");

        var productColumns = Columns(sheet);
        if (!productColumns.ContainsKey(HName))
            throw new InvalidOperationException($"העמודה '{HName}' חסרה בגיליון המוצרים");

        // The old layout kept the additions on a sheet of their own; without that and without a column, they stay as they are.
        Dictionary<Guid, List<ProductAdditionDto>>? legacyAdditions = null;
        if (!productColumns.ContainsKey(HAdditions) && workbook.TryGetWorksheet(AdditionsSheet, out var additionsSheet))
            legacyAdditions = ReadLegacyAdditions(additionsSheet, additionTypes, current.Keys.ToHashSet(), problems);

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var n = row.RowNumber();
            var name = Text(row, productColumns, HName);
            var idText = Text(row, productColumns, HId);
            if (name.Length == 0 && idText.Length == 0) continue;

            Guid? id = null;
            if (idText.Length > 0)
            {
                if (!Guid.TryParse(idText, out var parsed))
                {
                    problems.Add($"שורה {n}: המזהה אינו תקין. לא לשנות את עמודת המזהה");
                    continue;
                }

                id = parsed;
                seen.Add(parsed); // a row with a problem must never make its product look removed
            }

            if (id is { } known && !current.ContainsKey(known))
            {
                problems.Add($"שורה {n} ({name}): המוצר לא נמצא במערכת (נמחק?)");
                continue;
            }

            if (name.Length == 0)
            {
                problems.Add($"שורה {n}: שם המוצר ריק");
                continue;
            }

            var existing = id is { } k ? current[k] : null;
            var weight = Number(row, productColumns, HWeight);
            var hours = Number(row, productColumns, HHours);
            var price = Number(row, productColumns, HPrice);
            var type = productColumns.ContainsKey(HType) ? Text(row, productColumns, HType) : existing?.Type ?? "";
            var material = productColumns.ContainsKey(HMaterial) ? Text(row, productColumns, HMaterial) : existing?.Material ?? "";

            if (material.Length > 0 && !materials.Contains(material))
            {
                problems.Add($"שורה {n} ({name}): חומר לא מוכר '{material}'");
                continue;
            }

            if (weight is < 0 || hours is < 0 || price is < 0)
            {
                problems.Add($"שורה {n} ({name}): משקל, שעות או מחיר שליליים");
                continue;
            }

            // Collections: the names as written; blank means "as it is".
            List<Guid>? collectionIds = null;
            var collectionsText = Text(row, productColumns, HCollections);
            var unknown = new List<string>();
            if (collectionsText.Length > 0)
            {
                collectionIds = [];
                foreach (var part in collectionsText.Split([',', '،', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (collectionId.TryGetValue(part, out var cid)) collectionIds.Add(cid);
                    else unknown.Add(part);
                }
            }

            if (unknown.Count > 0)
            {
                problems.Add($"שורה {n} ({name}): אוסף לא מוכר: {string.Join(", ", unknown)}");
                continue;
            }

            // The additions: from the cell, from the old sheet, or as they are.
            List<ProductAdditionDto> additions;
            if (productColumns.ContainsKey(HAdditions))
            {
                var (parsedAdditions, errors) = ParseAdditions(Text(row, productColumns, HAdditions), additionTypes);
                if (errors.Count > 0)
                {
                    problems.Add($"שורה {n} ({name}): {string.Join("; ", errors)}");
                    continue;
                }

                additions = parsedAdditions;
            }
            else if (legacyAdditions is not null && existing is not null)
            {
                additions = legacyAdditions.GetValueOrDefault(existing.Id) ?? [];
            }
            else
            {
                additions = existing?.Additions ?? [];
            }

            var shopifyCell = Text(row, productColumns, HShopifyName);
            // Blank on both sides leaves it alone; blank where there was a name clears it.
            string? shopifyName = shopifyCell.Length > 0 ? shopifyCell : existing?.ShopifyName is { Length: > 0 } ? "" : null;

            var dto = new SaveProductDto(
                type, name, material,
                weight ?? existing?.Weight ?? 0, hours ?? existing?.AdditionalWorkHours ?? 0, price ?? existing?.SitePrice ?? 0,
                additions, collectionIds ?? existing?.CollectionIds ?? [], shopifyName);

            if (existing is null)
            {
                // A row without an id is a new product.
                try
                {
                    if (!dryRun) await products.CreateProductAsync(dto);
                    created++;
                    changes.Add($"שורה {n}: מוצר חדש '{name}'");
                }
                catch (Exception ex)
                {
                    db.ChangeTracker.Clear();
                    problems.Add($"שורה {n} ({name}): {ex.Message}");
                }

                continue;
            }

            var diff = Describe(existing, dto);
            if (diff.Count == 0)
            {
                unchanged++;
                continue;
            }

            try
            {
                if (!dryRun) await products.UpdateProductAsync(existing.Id, dto);
                updated++;
                changes.Add($"שורה {n} ({name}): {string.Join("; ", diff)}");
            }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                problems.Add($"שורה {n} ({name}): {ex.Message}");
            }
        }

        // Products that are in the system but not in the file. They go only when asked to, and never a majority
        // at once: a file that lost most of its rows is far more likely a mistake than a wish.
        var notInFile = current.Values.Where(p => !seen.Contains(p.Id)).OrderBy(p => p.Name).ToList();
        var deleted = 0;
        if (deleteMissing && notInFile.Count > 0)
        {
            if (notInFile.Count > current.Count / 2)
            {
                problems.Add($"לא נמחקו מוצרים: הקובץ חסר {notInFile.Count} מתוך {current.Count} מוצרים (יותר ממחצית). זה נראה כמו טעות בקובץ");
            }
            else if (!dryRun)
            {
                foreach (var product in notInFile)
                {
                    try
                    {
                        await products.DeleteProductAsync(product.Id);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        db.ChangeTracker.Clear();
                        problems.Add($"מחיקת '{product.Name}': {ex.Message}");
                    }
                }
            }
        }

        return new SheetUpdateResult(
            updated, unchanged, created, changes, problems,
            notInFile.Select(p => $"{p.Name} ({p.Type}, {p.Material})").ToList(), deleted);
    }

    private static Dictionary<Guid, List<ProductAdditionDto>> ReadLegacyAdditions(
        IXLWorksheet sheet, IReadOnlyDictionary<string, bool> types, HashSet<Guid> productIds, List<string> problems)
    {
        var result = new Dictionary<Guid, List<ProductAdditionDto>>();
        var cols = Columns(sheet);

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var typeText = Text(row, cols, AType);
            var idText = Text(row, cols, AProductId);
            if (typeText.Length == 0 && idText.Length == 0) continue;

            if (!Guid.TryParse(idText, out var productId) || !productIds.Contains(productId))
            {
                problems.Add($"תוספות שורה {row.RowNumber()}: מזהה המוצר חסר או לא מוכר");
                continue;
            }

            if (!types.TryGetValue(typeText, out var allowsCustom))
            {
                problems.Add($"תוספות שורה {row.RowNumber()}: סוג תוספת לא מוכר '{typeText}'");
                result[productId] = [.. result.GetValueOrDefault(productId) ?? [], new ProductAdditionDto("", null, 0, 1)];
                continue;
            }

            var custom = Text(row, cols, ACustom);
            if (allowsCustom && custom.Length == 0)
            {
                problems.Add($"תוספות שורה {row.RowNumber()}: לתוספת '{typeText}' חייב להיות שם חופשי");
                result[productId] = [.. result.GetValueOrDefault(productId) ?? [], new ProductAdditionDto("", null, 0, 1)];
                continue;
            }

            var list = result.GetValueOrDefault(productId) ?? [];
            list.Add(new ProductAdditionDto(typeText, allowsCustom ? custom : null, Number(row, cols, APrice) ?? 0m,
                Math.Max((int)(Number(row, cols, AQuantity) ?? 1m), 1)));
            result[productId] = list;
        }

        return result;
    }

    // What differs between the product now and what the sheet says, in the owner's words.
    private static List<string> Describe(ProductResponse now, SaveProductDto next)
    {
        var diff = new List<string>();
        void Check(string label, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal)) diff.Add($"{label}: {(a.Length == 0 ? "(ריק)" : a)} ← {(b.Length == 0 ? "(ריק)" : b)}");
        }

        string Num(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

        Check("שם", now.Name, next.Name);
        Check("סוג", now.Type, next.Type);
        Check("חומר", now.Material, next.Material);
        Check("משקל", Num(now.Weight), Num(next.Weight));
        Check("שעות נוספות", Num(now.AdditionalWorkHours), Num(next.AdditionalWorkHours));
        Check("מחיר", Num(now.SitePrice), Num(next.SitePrice));

        if (!now.CollectionIds.OrderBy(i => i).SequenceEqual(next.CollectionIds.OrderBy(i => i))) diff.Add("אוספים");

        string Adds(IEnumerable<ProductAdditionDto> list) =>
            string.Join(",", list.Select(a => $"{a.TypeName}|{a.CustomName}|{a.Price:0.##}|{a.Quantity}").OrderBy(s => s, StringComparer.Ordinal));
        if (Adds(now.Additions) != Adds(next.Additions)) diff.Add($"תוספות: {FormatAdditions(next.Additions)}");

        if (next.ShopifyName is not null && next.ShopifyName != (now.ShopifyName ?? ""))
            diff.Add($"שם בשופיפי: {now.ShopifyName ?? "(ריק)"} ← {(next.ShopifyName.Length == 0 ? "(ריק)" : next.ShopifyName)}");

        return diff;
    }

    // Header text → column number. The earlier layout called the collections column a longer name.
    internal static Dictionary<string, int> Columns(IXLWorksheet sheet)
    {
        var result = new Dictionary<string, int>();
        foreach (var cell in sheet.Row(1).CellsUsed())
        {
            var header = cell.GetString().Trim();
            if (header.StartsWith(HCollections, StringComparison.Ordinal)) header = HCollections;
            // The review file named its notes column with a longer text; it is the same column.
            if (header.StartsWith(HNotes, StringComparison.Ordinal)) header = HNotes;
            result.TryAdd(header, cell.Address.ColumnNumber);
        }

        return result;
    }

    internal static string Text(IXLRow row, Dictionary<string, int> columns, string header) =>
        columns.TryGetValue(header, out var c) ? row.Cell(c).GetString().Trim() : "";

    // Numbers typed as text ("12,5" or "12.5 ₪") are accepted; empty or unreadable gives null.
    internal static decimal? Number(IXLRow row, Dictionary<string, int> columns, string header)
    {
        if (!columns.TryGetValue(header, out var c)) return null;

        var cell = row.Cell(c);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();

        var text = new string(cell.GetString().Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray()).Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
