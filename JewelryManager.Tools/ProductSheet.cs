using System.Globalization;
using ClosedXML.Excel;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Collections;
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
    List<string> Problems);

/// <summary>
/// The whole catalog as one Excel workbook, and back: the owner fills in what is missing and the tool
/// applies it through the same product service the app uses. Products are matched by the hidden-in-plain-sight
/// id column, so renaming a product in the sheet renames it instead of making a second one.
/// </summary>
public static class ProductSheet
{
    private const string ProductsSheet = "מוצרים";
    private const string AdditionsSheet = "תוספות";
    private const string ListsSheet = "רשימות";
    private const string GuideSheet = "הוראות";

    // Product columns (by header text, so the owner may move them around).
    private const string HId = "מזהה (לא לשנות)";
    private const string HName = "שם המוצר";
    private const string HType = "סוג";
    private const string HMaterial = "חומר";
    private const string HWeight = "משקל (גרם)";
    private const string HHours = "שעות עבודה נוספות";
    private const string HPrice = "מחיר באתר";
    private const string HCollections = "אוספים (מופרדים בפסיק)";
    private const string HShopifyName = "שם בשופיפי";
    private const string HRecommended = "מחיר מומלץ (חישוב, לא לערוך)";
    private const string HNeeds = "חסרים פרטים (לא לערוך)";
    private const string HVariants = "אפשרויות בחנות (לא לערוך)";

    // Addition columns.
    private const string AProductId = "מזהה המוצר (לא לשנות)";
    private const string AProductName = "שם המוצר (לא לערוך)";
    private const string AType = "סוג תוספת";
    private const string ACustom = "שם חופשי";
    private const string APrice = "מחיר";
    private const string AQuantity = "כמות";

    private static readonly string[] DefaultTypes = ["טבעת", "עגילים", "שרשרת", "צמיד", "תליון", "סט", "אחר"];

    // ── Export ───────────────────────────────────────────────────────────────

    public static async Task<SheetExportResult> ExportAsync(AppDbContext db, Guid businessId, string path)
    {
        var tenant = ProductImporter.TenantFor(businessId);
        var products = (await new ProductsService(db, tenant, new PricingService(db, tenant)).GetProductsAsync()).Products;
        var collections = await new CollectionsService(db, tenant).GetCollectionsAsync();
        var collectionName = collections.ToDictionary(c => c.Id, c => c.Name);

        var materials = await db.Materials.AsNoTracking().Where(m => m.BusinessId == businessId)
            .OrderBy(m => m.SortOrder).Select(m => m.Name).ToListAsync();
        var additionTypes = await db.ProductAdditionTypes.AsNoTracking().Where(t => t.BusinessId == businessId)
            .OrderBy(t => t.SortOrder).Select(t => t.Name).ToListAsync();
        var types = products.Select(p => p.Type).Where(t => !string.IsNullOrWhiteSpace(t))
            .Concat(DefaultTypes).Distinct().OrderBy(t => t).ToList();

        using var workbook = new XLWorkbook();

        var sheet = workbook.AddWorksheet(ProductsSheet);
        sheet.RightToLeft = true;

        var headers = new[] { HId, HName, HType, HMaterial, HWeight, HHours, HPrice, HCollections, HShopifyName, HRecommended, HNeeds, HVariants };
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];

        var row = 2;
        var needing = 0;
        foreach (var p in products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            sheet.Cell(row, 1).Value = p.Id.ToString();
            sheet.Cell(row, 2).Value = p.Name;
            sheet.Cell(row, 3).Value = p.Type;
            sheet.Cell(row, 4).Value = p.Material;
            sheet.Cell(row, 5).Value = p.Weight;
            sheet.Cell(row, 6).Value = p.AdditionalWorkHours;
            sheet.Cell(row, 7).Value = p.SitePrice;
            sheet.Cell(row, 8).Value = string.Join(", ", p.CollectionIds.Select(id => collectionName.GetValueOrDefault(id, "")).Where(n => n.Length > 0));
            sheet.Cell(row, 9).Value = p.ShopifyName ?? "";
            sheet.Cell(row, 10).Value = p.Price is null ? "" : Math.Round(p.Price.RecommendedPrice);
            sheet.Cell(row, 11).Value = p.NeedsDetails ? "כן" : "";
            sheet.Cell(row, 12).Value = string.Join(" | ", p.ShopifyVariants.Select(v => string.IsNullOrWhiteSpace(v.Title) ? $"{v.Price:0.##}" : $"{v.Title}: {v.Price:0.##}"));

            // What still has to be filled in stands out.
            if (string.IsNullOrWhiteSpace(p.Type)) sheet.Cell(row, 3).Style.Fill.BackgroundColor = XLColor.LightYellow;
            if (string.IsNullOrWhiteSpace(p.Material)) sheet.Cell(row, 4).Style.Fill.BackgroundColor = XLColor.LightYellow;
            if (p.NeedsDetails) needing++;
            row++;
        }

        var lastProductRow = Math.Max(row - 1, 2);
        StyleHeader(sheet, headers.Length);
        foreach (var c in new[] { 1, 10, 11, 12 })
            sheet.Range(2, c, lastProductRow, c).Style.Fill.BackgroundColor = XLColor.WhiteSmoke;
        sheet.Range(2, 5, lastProductRow, 5).Style.NumberFormat.Format = "0.##";
        sheet.Range(2, 6, lastProductRow, 6).Style.NumberFormat.Format = "0.##";
        sheet.Range(2, 7, lastProductRow, 7).Style.NumberFormat.Format = "#,##0.##";
        sheet.Range(2, 10, lastProductRow, 10).Style.NumberFormat.Format = "#,##0";
        sheet.Range(1, 1, lastProductRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.SheetView.FreezeColumns(2);
        foreach (var (column, width) in new[] { (1, 14), (2, 34), (3, 14), (4, 16), (5, 12), (6, 14), (7, 12), (8, 26), (9, 34), (10, 14), (11, 12), (12, 40) })
            sheet.Column(column).Width = width;

        // The additions: one row per priced extra of a product.
        var additions = workbook.AddWorksheet(AdditionsSheet);
        additions.RightToLeft = true;
        var additionHeaders = new[] { AProductId, AProductName, AType, ACustom, APrice, AQuantity };
        for (var c = 0; c < additionHeaders.Length; c++) additions.Cell(1, c + 1).Value = additionHeaders[c];

        var additionRow = 2;
        foreach (var p in products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            foreach (var a in p.Additions)
            {
                additions.Cell(additionRow, 1).Value = p.Id.ToString();
                additions.Cell(additionRow, 2).Value = p.Name;
                additions.Cell(additionRow, 3).Value = a.TypeName;
                additions.Cell(additionRow, 4).Value = a.CustomName ?? "";
                additions.Cell(additionRow, 5).Value = a.Price;
                additions.Cell(additionRow, 6).Value = a.Quantity;
                additionRow++;
            }
        }

        StyleHeader(additions, additionHeaders.Length);
        additions.Range(2, 1, Math.Max(additionRow - 1, 2) + 200, 2).Style.Fill.BackgroundColor = XLColor.WhiteSmoke;
        additions.SheetView.FreezeRows(1);
        foreach (var (column, width) in new[] { (1, 14), (2, 34), (3, 18), (4, 22), (5, 12), (6, 8) })
            additions.Column(column).Width = width;

        // Lists behind the drop-downs.
        var lists = workbook.AddWorksheet(ListsSheet);
        WriteList(lists, 1, "חומרים", materials);
        WriteList(lists, 2, "סוגי תוספת", additionTypes);
        WriteList(lists, 3, "אוספים", collections.Select(c => c.Name).ToList());
        WriteList(lists, 4, "סוגי מוצר", types);

        if (materials.Count > 0)
        {
            var rule = sheet.Range(2, 4, lastProductRow + 500, 4).CreateDataValidation();
            rule.List(lists.Range(2, 1, materials.Count + 1, 1), true);
            rule.ErrorTitle = "חומר לא מוכר";
            rule.ErrorMessage = "יש לבחור חומר מהרשימה (חומרים מנוהלים בהגדרות).";
        }

        var typeRule = sheet.Range(2, 3, lastProductRow + 500, 3).CreateDataValidation();
        typeRule.List(lists.Range(2, 4, types.Count + 1, 4), true);
        typeRule.ErrorStyle = XLErrorStyle.Warning; // a new type is allowed, the list is only a help

        if (additionTypes.Count > 0)
        {
            var rule = additions.Range(2, 3, additionRow + 200, 3).CreateDataValidation();
            rule.List(lists.Range(2, 2, additionTypes.Count + 1, 2), true);
            rule.ErrorTitle = "סוג תוספת לא מוכר";
            rule.ErrorMessage = "יש לבחור סוג תוספת מהרשימה (מנוהלים בהגדרות האישיות).";
        }

        lists.Visibility = XLWorksheetVisibility.Hidden;

        var guide = workbook.AddWorksheet(GuideSheet);
        guide.RightToLeft = true;
        var lines = new[]
        {
            "איך עובדים עם הקובץ",
            "",
            "1. בגיליון 'מוצרים' שורה אחת לכל מוצר. תא צהוב = חסר פרט. ממלאים סוג וחומר (בחירה מהרשימה), משקל, שעות עבודה נוספות ומחיר.",
            "2. העמודות האפורות (מזהה, מחיר מומלץ, חסרים פרטים, אפשרויות בחנות) הן לקריאה בלבד. אסור לשנות את עמודת המזהה: לפיה המוצר מזוהה.",
            "3. בגיליון 'תוספות' שורה לכל תוספת (אבן, ציפוי...): מזהה המוצר, סוג תוספת, מחיר וכמות. כל התוספות של מוצר מוחלפות במה שכתוב כאן.",
            "4. אוספים: שמות אוספים קיימים, מופרדים בפסיק. מוצר בלי אוסף עובר ל'כללי'.",
            "5. שורה חדשה בלי מזהה = מוצר חדש. מחיקת שורה לא מוחקת את המוצר מהמערכת.",
            "6. שומרים את הקובץ (xlsx), ומריצים את הטעינה קודם עם --dry-run כדי לראות מה ישתנה, ואז בלי.",
        };
        for (var i = 0; i < lines.Length; i++) guide.Cell(i + 1, 1).Value = lines[i];
        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Column(1).Width = 120;
        guide.SetTabActive();

        workbook.SaveAs(path);
        return new SheetExportResult(products.Count, additionRow - 2, needing);
    }

    private static void StyleHeader(IXLWorksheet sheet, int columns)
    {
        var header = sheet.Range(1, 1, 1, columns);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
        header.Style.Alignment.WrapText = true;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void WriteList(IXLWorksheet sheet, int column, string title, List<string> items)
    {
        sheet.Cell(1, column).Value = title;
        for (var i = 0; i < items.Count; i++) sheet.Cell(i + 2, column).Value = items[i];
    }

    // ── Import ───────────────────────────────────────────────────────────────

    private record SheetProduct(int Row, Guid? Id, string Name, string Type, string Material, decimal Weight,
        decimal Hours, decimal Price, string? Collections, string ShopifyName);

    public static async Task<SheetUpdateResult> ImportAsync(AppDbContext db, Guid businessId, string path, bool dryRun)
    {
        var tenant = ProductImporter.TenantFor(businessId);
        var pricing = new PricingService(db, tenant);
        var products = new ProductsService(db, tenant, pricing);
        var collectionsService = new CollectionsService(db, tenant);

        var current = (await products.GetProductsAsync()).Products.ToDictionary(p => p.Id);
        var collectionId = (await collectionsService.GetCollectionsAsync())
            .ToDictionary(c => c.Name.Trim(), c => c.Id, StringComparer.OrdinalIgnoreCase);
        var materials = (await db.Materials.AsNoTracking().Where(m => m.BusinessId == businessId).Select(m => m.Name).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additionTypes = (await db.ProductAdditionTypes.AsNoTracking().Where(t => t.BusinessId == businessId).ToListAsync())
            .ToDictionary(t => t.Name.Trim(), t => t, StringComparer.OrdinalIgnoreCase);

        var changes = new List<string>();
        var problems = new List<string>();
        int updated = 0, unchanged = 0, created = 0;

        using var workbook = new XLWorkbook(path);
        if (!workbook.TryGetWorksheet(ProductsSheet, out var sheet))
            throw new InvalidOperationException($"הגיליון '{ProductsSheet}' לא נמצא בקובץ");

        // The additions, grouped by product. Without that sheet the additions are left exactly as they are.
        Dictionary<Guid, List<ProductAdditionDto>>? additionsByProduct = null;
        if (workbook.TryGetWorksheet(AdditionsSheet, out var additionsSheet))
        {
            additionsByProduct = [];
            var cols = Columns(additionsSheet);
            foreach (var row in additionsSheet.RowsUsed().Skip(1))
            {
                var typeText = Text(row, cols, AType);
                var idText = Text(row, cols, AProductId);
                if (typeText.Length == 0 && idText.Length == 0) continue;

                if (!Guid.TryParse(idText, out var productId) || !current.ContainsKey(productId))
                {
                    problems.Add($"תוספות שורה {row.RowNumber()}: מזהה המוצר חסר או לא מוכר");
                    continue;
                }

                if (!additionTypes.TryGetValue(typeText, out var type))
                {
                    problems.Add($"תוספות שורה {row.RowNumber()}: סוג תוספת לא מוכר '{typeText}'");
                    additionsByProduct[productId] = [.. additionsByProduct.GetValueOrDefault(productId) ?? [], new ProductAdditionDto("", null, 0, 1)];
                    continue;
                }

                var price = Number(row, cols, APrice) ?? 0m;
                var quantity = (int)(Number(row, cols, AQuantity) ?? 1m);
                var custom = Text(row, cols, ACustom);
                if (type.AllowsCustomName && custom.Length == 0)
                {
                    problems.Add($"תוספות שורה {row.RowNumber()}: לתוספת '{type.Name}' חייב להיות שם חופשי");
                    additionsByProduct[productId] = [.. additionsByProduct.GetValueOrDefault(productId) ?? [], new ProductAdditionDto("", null, 0, 1)];
                    continue;
                }

                var list = additionsByProduct.GetValueOrDefault(productId) ?? [];
                list.Add(new ProductAdditionDto(type.Name, type.AllowsCustomName ? custom : null, price, Math.Max(quantity, 1)));
                additionsByProduct[productId] = list;
            }
        }

        var productColumns = Columns(sheet);
        foreach (var column in new[] { HId, HName })
        {
            if (!productColumns.ContainsKey(column))
                throw new InvalidOperationException($"העמודה '{column}' חסרה בגיליון המוצרים");
        }

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
            }

            if (id is { } known && !current.ContainsKey(known))
            {
                problems.Add($"שורה {n} ({name}): המוצר לא נמצא במערכת (נמחק?)");
                continue;
            }

            var weight = Number(row, productColumns, HWeight);
            var hours = Number(row, productColumns, HHours);
            var price = Number(row, productColumns, HPrice);

            var existing = id is { } k ? current[k] : null;
            var type = productColumns.ContainsKey(HType) ? Text(row, productColumns, HType) : existing?.Type ?? "";
            var material = productColumns.ContainsKey(HMaterial) ? Text(row, productColumns, HMaterial) : existing?.Material ?? "";

            if (name.Length == 0) { problems.Add($"שורה {n}: שם המוצר ריק"); continue; }
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

            var shopifyCell = Text(row, productColumns, HShopifyName);
            // Blank on both sides leaves it alone; blank where there was a name clears it.
            string? shopifyName = shopifyCell.Length > 0 ? shopifyCell : existing?.ShopifyName is { Length: > 0 } ? "" : null;

            var dto = new SaveProductDto(
                type, name, material,
                weight ?? existing?.Weight ?? 0, hours ?? existing?.AdditionalWorkHours ?? 0, price ?? existing?.SitePrice ?? 0,
                additionsByProduct is not null && existing is not null
                    ? additionsByProduct.GetValueOrDefault(existing.Id) ?? []
                    : existing?.Additions ?? [],
                collectionIds ?? existing?.CollectionIds ?? [],
                shopifyName);

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

            if (existing.Additions.Any(a => a.TypeName.Length == 0) || dto.Additions.Any(a => a.TypeName.Length == 0))
            {
                problems.Add($"שורה {n} ({name}): יש בעיה בתוספות שלו, ולכן לא עודכן");
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

        return new SheetUpdateResult(updated, unchanged, created, changes, problems);
    }

    // What differs between the product now and what the sheet says, in the owner's words.
    private static List<string> Describe(ProductResponse now, SaveProductDto next)
    {
        var diff = new List<string>();
        void Check(string label, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal)) diff.Add($"{label}: {(a.Length == 0 ? "(ריק)" : a)} ← {(b.Length == 0 ? "(ריק)" : b)}");
        }

        Check("שם", now.Name, next.Name);
        Check("סוג", now.Type, next.Type);
        Check("חומר", now.Material, next.Material);
        Check("משקל", now.Weight.ToString("0.##", CultureInfo.InvariantCulture), next.Weight.ToString("0.##", CultureInfo.InvariantCulture));
        Check("שעות נוספות", now.AdditionalWorkHours.ToString("0.##", CultureInfo.InvariantCulture), next.AdditionalWorkHours.ToString("0.##", CultureInfo.InvariantCulture));
        Check("מחיר", now.SitePrice.ToString("0.##", CultureInfo.InvariantCulture), next.SitePrice.ToString("0.##", CultureInfo.InvariantCulture));

        if (!now.CollectionIds.OrderBy(i => i).SequenceEqual(next.CollectionIds.OrderBy(i => i))) diff.Add("אוספים");

        string Adds(IEnumerable<ProductAdditionDto> list) =>
            string.Join(",", list.Select(a => $"{a.TypeName}|{a.CustomName}|{a.Price:0.##}|{a.Quantity}").OrderBy(s => s, StringComparer.Ordinal));
        if (Adds(now.Additions) != Adds(next.Additions)) diff.Add("תוספות");

        if (next.ShopifyName is not null && next.ShopifyName != (now.ShopifyName ?? ""))
            diff.Add($"שם בשופיפי: {now.ShopifyName ?? "(ריק)"} ← {(next.ShopifyName.Length == 0 ? "(ריק)" : next.ShopifyName)}");

        return diff;
    }

    // Header text → column number.
    private static Dictionary<string, int> Columns(IXLWorksheet sheet)
    {
        var result = new Dictionary<string, int>();
        var header = sheet.Row(1);
        foreach (var cell in header.CellsUsed()) result.TryAdd(cell.GetString().Trim(), cell.Address.ColumnNumber);
        return result;
    }

    private static string Text(IXLRow row, Dictionary<string, int> columns, string header) =>
        columns.TryGetValue(header, out var c) ? row.Cell(c).GetString().Trim() : "";

    // Numbers typed as text ("12,5" or "12.5 ₪") are accepted; empty or unreadable gives null.
    private static decimal? Number(IXLRow row, Dictionary<string, int> columns, string header)
    {
        if (!columns.TryGetValue(header, out var c)) return null;

        var cell = row.Cell(c);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();

        var text = new string(cell.GetString().Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray()).Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
