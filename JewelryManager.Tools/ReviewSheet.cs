using ClosedXML.Excel;

namespace JewelryManager.Tools;

public record ReviewSummary(
    int Products,
    int Complete,
    int MaterialsSuggested,
    int MaterialsGuessed,
    int TypesSuggested,
    int StillNoMaterial,
    int StillNoType,
    int NoWeight,
    int NoPrice,
    List<string> Unresolved);

/// <summary>
/// Takes the exported workbook and makes it ready for a careful look: fills the blanks it can work out
/// from the product's name (marked, with the reason, so each one can be accepted or corrected), says per
/// product what is still missing, and leaves a column for the owner's own notes. It never touches
/// the database; the file is only applied later by update-products, after the owner has been through it.
/// </summary>
public static class ReviewSheet
{
    private const string HStillMissing = "מה עדיין חסר";
    private const string HSuggestion = "מולא אוטומטית (לבדוק)";
    private const string HNotes = "הערות שלי (למחוק / לעדכן / להוסיף)";
    private const string SummarySheet = "סיכום";

    private static readonly XLColor Sure = XLColor.FromHtml("#C8E6C9");   // worked out from the name, clear
    private static readonly XLColor Guess = XLColor.FromHtml("#FFE0B2");  // a best guess, to be checked
    private static readonly XLColor Missing = XLColor.FromHtml("#FFF59D"); // still empty

    // Keyword (lower case, inside the name) → product type. Order matters: the first match wins.
    private static readonly (string[] Words, string Type)[] TypeWords =
    [
        (["earring", "עגיל"], "עגילים"),
        (["necklace", "שרשרת", "pendant", "תליון"], "שרשרת"),
        (["bracelet", "צמיד"], "צמיד"),
        (["ring", "טבעת"], "טבעת"),
    ];

    public static ReviewSummary Prepare(string inPath, string outPath)
    {
        using var workbook = new XLWorkbook(inPath);
        if (!workbook.TryGetWorksheet(ProductSheet.ProductsSheet, out var sheet))
            throw new InvalidOperationException($"הגיליון '{ProductSheet.ProductsSheet}' לא נמצא בקובץ");

        var columns = ProductSheet.Columns(sheet);
        foreach (var needed in new[] { ProductSheet.HName, ProductSheet.HType, ProductSheet.HMaterial, ProductSheet.HWeight, ProductSheet.HPrice })
        {
            if (!columns.ContainsKey(needed)) throw new InvalidOperationException($"העמודה '{needed}' חסרה בגיליון המוצרים");
        }

        int Col(string header) => columns[header];

        // The materials that really exist, from the workbook's own lists.
        var materials = new List<string>();
        if (workbook.TryGetWorksheet(ProductSheet.ListsSheet, out var lists))
        {
            foreach (var cell in lists.Column(1).CellsUsed().Skip(1)) materials.Add(cell.GetString().Trim());
        }

        var gold = PickGold(materials);
        var silver = PickSilver(materials);

        // New columns go after the last one in use.
        var next = sheet.LastColumnUsed()!.ColumnNumber() + 1;
        var colMissing = columns.TryGetValue(HStillMissing, out var m) ? m : next++;
        var colSuggestion = columns.TryGetValue(HSuggestion, out var s) ? s : next++;
        var colNotes = columns.TryGetValue(HNotes, out var n) ? n : next++;
        sheet.Cell(1, colMissing).Value = HStillMissing;
        sheet.Cell(1, colSuggestion).Value = HSuggestion;
        sheet.Cell(1, colNotes).Value = HNotes;

        int products = 0, complete = 0, matSure = 0, matGuess = 0, typeSure = 0, noMaterial = 0, noType = 0, noWeight = 0, noPrice = 0;
        var unresolved = new List<string>();

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var name = row.Cell(Col(ProductSheet.HName)).GetString().Trim();
            if (name.Length == 0) continue;
            products++;

            var type = row.Cell(Col(ProductSheet.HType)).GetString().Trim();
            var material = row.Cell(Col(ProductSheet.HMaterial)).GetString().Trim();
            var storeName = columns.TryGetValue(ProductSheet.HShopifyName, out var sn) ? row.Cell(sn).GetString().Trim() : "";
            var haystack = (name + " " + storeName).ToLowerInvariant();
            var notes = new List<string>();

            if (type.Length == 0)
            {
                var found = TypeWords.FirstOrDefault(t => t.Words.Any(w => haystack.Contains(w)));
                if (found.Type is not null)
                {
                    type = found.Type;
                    row.Cell(Col(ProductSheet.HType)).Value = type;
                    row.Cell(Col(ProductSheet.HType)).Style.Fill.BackgroundColor = Sure;
                    notes.Add($"סוג '{type}' לפי השם");
                    typeSure++;
                }
            }

            if (material.Length == 0)
            {
                var hasGold = haystack.Contains("gold") || haystack.Contains("זהב");
                var hasSilver = haystack.Contains("silver") || haystack.Contains("כסף");

                if (hasGold && !hasSilver && gold is not null)
                {
                    material = gold;
                    row.Cell(Col(ProductSheet.HMaterial)).Value = material;
                    row.Cell(Col(ProductSheet.HMaterial)).Style.Fill.BackgroundColor = Sure;
                    notes.Add($"חומר '{material}' כי בשם יש Gold");
                    matSure++;
                }
                else if (hasSilver && !hasGold && silver is not null)
                {
                    material = silver;
                    row.Cell(Col(ProductSheet.HMaterial)).Value = material;
                    row.Cell(Col(ProductSheet.HMaterial)).Style.Fill.BackgroundColor = Guess;
                    notes.Add($"חומר '{material}' ניחוש: בשם יש Silver, אבל יש כמה סוגי כסף (רגיל, יציקה, ציפוי). לבדוק");
                    matGuess++;
                }
            }

            if (type.Length == 0)
            {
                row.Cell(Col(ProductSheet.HType)).Style.Fill.BackgroundColor = Missing;
                noType++;
            }

            if (material.Length == 0)
            {
                row.Cell(Col(ProductSheet.HMaterial)).Style.Fill.BackgroundColor = Missing;
                noMaterial++;
            }

            var weight = ProductSheet.Number(row, columns, ProductSheet.HWeight);
            var price = ProductSheet.Number(row, columns, ProductSheet.HPrice);
            var stillMissing = new List<string>();
            if (type.Length == 0) stillMissing.Add("סוג");
            if (material.Length == 0) stillMissing.Add("חומר");
            if (weight is null or 0)
            {
                stillMissing.Add("משקל");
                row.Cell(Col(ProductSheet.HWeight)).Style.Fill.BackgroundColor = Missing;
                noWeight++;
            }

            if (price is null or 0)
            {
                stillMissing.Add("מחיר");
                row.Cell(Col(ProductSheet.HPrice)).Style.Fill.BackgroundColor = Missing;
                noPrice++;
            }

            row.Cell(colMissing).Value = stillMissing.Count == 0 ? "מלא" : string.Join(", ", stillMissing);
            row.Cell(colSuggestion).Value = string.Join("; ", notes);
            if (stillMissing.Count == 0) complete++;
            if (type.Length == 0 || material.Length == 0) unresolved.Add(name);
        }

        var last = sheet.LastRowUsed()!.RowNumber();
        sheet.AutoFilter.Clear();
        sheet.Range(1, 1, last, next - 1).SetAutoFilter();

        var header = sheet.Range(1, colMissing, 1, colNotes);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
        header.Style.Alignment.WrapText = true;
        sheet.Column(colMissing).Width = 22;
        sheet.Column(colSuggestion).Width = 48;
        sheet.Column(colNotes).Width = 36;

        // One short page that says where things stand, placed first.
        if (workbook.TryGetWorksheet(SummarySheet, out var old)) old.Delete();
        var summary = workbook.AddWorksheet(SummarySheet, 1);
        summary.RightToLeft = true;
        var lines = new (string Label, string Value)[]
        {
            ("מוצרים בקובץ", products.ToString()),
            ("מלאים לגמרי (סוג, חומר, משקל ומחיר)", complete.ToString()),
            ("", ""),
            ("חומר שמולא אוטומטית: ירוק (ברור לפי השם)", matSure.ToString()),
            ("חומר שמולא אוטומטית: כתום (ניחוש, לבדוק)", matGuess.ToString()),
            ("סוג שמולא אוטומטית", typeSure.ToString()),
            ("", ""),
            ("עדיין בלי סוג", noType.ToString()),
            ("עדיין בלי חומר", noMaterial.ToString()),
            ("בלי משקל (0 או ריק)", noWeight.ToString()),
            ("בלי מחיר באתר", noPrice.ToString()),
            ("", ""),
            ("איך עוברים על הקובץ", "בגיליון 'מוצרים' מסננים לפי העמודה 'מה עדיין חסר', ומסמנים בעמודת ההערות מה למחוק, לעדכן או להוסיף."),
            ("צבעים", "ירוק = מולא ובטוח. כתום = ניחוש, לבדוק. צהוב = עדיין ריק."),
        };
        for (var i = 0; i < lines.Length; i++)
        {
            summary.Cell(i + 1, 1).Value = lines[i].Label;
            summary.Cell(i + 1, 2).Value = lines[i].Value;
        }

        summary.Column(1).Width = 48;
        summary.Column(2).Width = 80;
        summary.Range(1, 1, lines.Length, 1).Style.Font.Bold = true;
        summary.SetTabActive();

        workbook.SaveAs(outPath);
        return new ReviewSummary(products, complete, matSure, matGuess, typeSure, noMaterial, noType, noWeight, noPrice, unresolved);
    }

    /// <summary>The product type a name shows (ring, earrings, necklace, bracelet), or null.</summary>
    internal static string? GuessType(string name)
    {
        var text = name.ToLowerInvariant();
        var found = TypeWords.FirstOrDefault(t => t.Words.Any(w => text.Contains(w)));
        return found.Type;
    }

    /// <summary>"gold" or "silver" when the name says exactly one of them, else null.</summary>
    internal static string? GuessMaterialKind(string name)
    {
        var text = name.ToLowerInvariant();
        var gold = text.Contains("gold") || text.Contains("זהב");
        var silver = text.Contains("silver") || text.Contains("כסף");
        return gold && !silver ? "gold" : silver && !gold ? "silver" : null;
    }

    // The gold material of this business: the one called gold, preferring 14K when there are several.
    private static string? PickGold(List<string> materials)
    {
        var golds = materials.Where(x => x.Contains("זהב") && !x.Contains("ציפוי")).ToList();
        return golds.FirstOrDefault(x => x.Contains("14")) ?? golds.FirstOrDefault();
    }

    // Plain silver: "כסף" itself, never the cast or plated variants.
    private static string? PickSilver(List<string> materials) =>
        materials.FirstOrDefault(x => x == "כסף")
        ?? materials.FirstOrDefault(x => x.Contains("כסף") && !x.Contains("ציפוי") && !x.Contains("יציקה"));
}
