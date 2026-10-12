using ClosedXML.Excel;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Collections.Dtos;
using JewelryManager.Api.Features.Orders;
using JewelryManager.Api.Features.Orders.Dtos;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;
using JewelryManager.Tools;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>The catalog as one Excel sheet and back, against real PostgreSQL, in a business of its own.</summary>
[Collection("postgres")]
public class ProductSheetTests(PostgresFixture pg)
{
    // The columns of the sheet, by position.
    private const int Name = 1, Type = 2, Material = 3, Weight = 4, Hours = 5, Price = 6, Additions = 7, Collections = 8, StoreName = 9, Notes = 12, Id = 13;

    private async Task<Guid> NewBusinessAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var db = pg.NewDb())
        {
            db.Businesses.Add(new Business { Id = id, Name = "Sheet " + id.ToString("N")[..6], CreatedAt = now, UpdatedAt = now });
            db.Settings.Add(new Settings { Id = Guid.NewGuid(), BusinessId = id, CreatedAt = now, UpdatedAt = now });
            db.Collections.Add(new Collection
            {
                Id = Guid.NewGuid(), BusinessId = id, Name = "כללי", Key = "general", IsPermanent = true, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = pg.NewDb())
        {
            await new SettingsService(db, PostgresFixture.TenantFor(id)).UpdateSettingsAsync(new UpdateSettingsDto(
                Materials: new() { ["כסף"] = new(8, 1, 1.5m), ["זהב"] = new(330, 2, 1.8m) },
                LaborHourRate: 100,
                PricingAdditions: [new PricingAdditionDto("אריזה", 0, [new("קופסה", 3)])],
                FeesItems:
                [
                    new("עמלת סליקה", 3, true, FeeKeys.CardFee),
                    new("מע\"מ", 18, true, FeeKeys.Vat),
                    new("עמלת עלויות קבועות", 17, true, FeeKeys.FixedExpenses),
                ],
                ProfitFloorPercent: 30,
                PreparationStages: ["יציקה"],
                ProductAdditionTypes: [new("אבן", false), new("אחר", true)]));
        }

        return id;
    }

    private async Task<T> WithProducts<T>(Guid business, Func<ProductsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(business);
        return await action(new ProductsService(db, tenant, new PricingService(db, tenant)));
    }

    private async Task<Guid> CollectionAsync(Guid business, string name)
    {
        await using var db = pg.NewDb();
        return (await new CollectionsService(db, PostgresFixture.TenantFor(business)).CreateCollectionAsync(new CreateCollectionDto(name))).Id;
    }

    private async Task<Guid> GeneralAsync(Guid business)
    {
        await using var db = pg.NewDb();
        return await db.Collections.Where(c => c.BusinessId == business && c.Key == "general").Select(c => c.Id).SingleAsync();
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"products-{Guid.NewGuid():N}.xlsx");

    private async Task<SheetExportResult> ExportAsync(Guid business, string path)
    {
        await using var db = pg.NewDb();
        return await ProductSheet.ExportAsync(db, business, path);
    }

    private async Task<SheetUpdateResult> ImportAsync(Guid business, string path, bool dryRun, bool deleteMissing = false)
    {
        await using var db = pg.NewDb();
        return await ProductSheet.ImportAsync(db, business, path, dryRun, deleteMissing);
    }

    private static int RowOf(IXLWorksheet sheet, string name) =>
        sheet.RowsUsed().Skip(1).First(r => r.Cell(Name).GetString() == name).RowNumber();

    // ── The additions as one cell ────────────────────────────────────────────

    private static readonly Dictionary<string, bool> Types = new() { ["אבן"] = false, ["אחר"] = true };

    [Fact]
    public void Additions_AreWrittenAndReadBackTheSame()
    {
        List<ProductAdditionDto> original = [new("אבן", null, 150, 2), new("אחר", "חריטה", 15.5m, 1)];

        var text = ProductSheet.FormatAdditions(original);
        Assert.Equal("אבן 150 x2; אחר:חריטה 15.5", text);

        var (parsed, errors) = ProductSheet.ParseAdditions(text, Types);
        Assert.Empty(errors);
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("יהלום 500", "סוג תוספת לא מוכר")]   // not a type of this business
    [InlineData("אבן", "לא הצלחתי לקרוא")]            // no price
    [InlineData("אחר 15", "שם חופשי")]                // "other" needs its own name
    public void Additions_ThatCannotBeReadAreReported_NeverGuessed(string text, string expected)
    {
        var (parsed, errors) = ProductSheet.ParseAdditions(text, Types);

        Assert.Empty(parsed);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    // ── Export and back ──────────────────────────────────────────────────────

    [Fact]
    public async Task Export_IsOneSheet_AndTheFilledSheetUpdatesTheCatalog()
    {
        var business = await NewBusinessAsync();
        var rings = await CollectionAsync(business, "טבעות");
        var general = await GeneralAsync(business);

        var complete = await WithProducts(business, s => s.CreateProductAsync(
            new SaveProductDto("טבעת", "סולטייר", "כסף", 5, 0.5m, 1500, [new("אבן", null, 30, 2)], [rings], "Solitaire")));
        await using (var db = pg.NewDb())
        {
            db.Products.Add(new Product
            {
                Id = Guid.NewGuid(), BusinessId = business, Name = "עגילי גרייס", SitePrice = 449,
                ShopifyName = "Grace Earrings", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                Collections = [new ProductCollection { BusinessId = business, CollectionId = general }],
            });
            await db.SaveChangesAsync();
        }

        var path = TempFile();
        try
        {
            var exported = await ExportAsync(business, path);
            Assert.Equal(2, exported.Products);
            Assert.Equal(1, exported.Additions);
            Assert.Equal(1, exported.NeedingDetails);

            using (var check = new XLWorkbook(path))
            {
                Assert.Single(check.Worksheets); // one sheet, nothing hidden behind it
                var sheet = check.Worksheet("מוצרים");
                Assert.Equal("אבן 30 x2", sheet.Cell(RowOf(sheet, "סולטייר"), Additions).GetString());
                Assert.Equal("סוג, חומר, משקל", sheet.Cell(RowOf(sheet, "עגילי גרייס"), 11).GetString());
            }

            // Nothing was edited: applying the file changes nothing.
            var untouched = await ImportAsync(business, path, dryRun: false);
            Assert.Equal(0, untouched.Updated);
            Assert.Equal(2, untouched.Unchanged);
            Assert.Empty(untouched.Problems);

            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("מוצרים");
                var row = RowOf(sheet, "עגילי גרייס");
                sheet.Cell(row, Type).Value = "עגילים";
                sheet.Cell(row, Material).Value = "זהב";
                sheet.Cell(row, Weight).Value = 2.5;
                sheet.Cell(row, Hours).Value = 0.5;
                sheet.Cell(row, Price).Value = 520;
                sheet.Cell(row, Collections).Value = "טבעות, כללי";
                sheet.Cell(row, Additions).Value = "אחר:חריטה 15";
                workbook.Save();
            }

            // A dry run reports and saves nothing.
            var dry = await ImportAsync(business, path, dryRun: true);
            Assert.Equal(1, dry.Updated);
            Assert.Empty(dry.Problems);
            Assert.Contains(dry.Changes, c => c.Contains("עגילי גרייס") && c.Contains("חומר") && c.Contains("תוספות"));
            Assert.Equal(1, (await WithProducts(business, s => s.GetProductsAsync())).Products.Count(p => p.NeedsDetails));

            var applied = await ImportAsync(business, path, dryRun: false);
            Assert.Equal(1, applied.Updated);
            Assert.Equal(1, applied.Unchanged);

            var products = (await WithProducts(business, s => s.GetProductsAsync())).Products;
            var earrings = products.Single(p => p.Name == "עגילי גרייס");
            Assert.Equal("עגילים", earrings.Type);
            Assert.Equal("זהב", earrings.Material);
            Assert.Equal(2.5m, earrings.Weight);
            Assert.Equal(520m, earrings.SitePrice);
            Assert.Equal("Grace Earrings", earrings.ShopifyName); // untouched
            Assert.False(earrings.NeedsDetails);
            Assert.Contains(rings, earrings.CollectionIds);
            Assert.Contains(general, earrings.CollectionIds);
            Assert.Equal("חריטה", Assert.Single(earrings.Additions).CustomName);

            var solitaire = products.Single(p => p.Id == complete.Id);
            Assert.Equal(1500m, solitaire.SitePrice);
            Assert.Single(solitaire.Additions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_ReportsBadRows_WithoutTouchingThem_AndAddsAProductForARowWithoutAnId()
    {
        var business = await NewBusinessAsync();
        var product = await WithProducts(business, s => s.CreateProductAsync(
            new SaveProductDto("טבעת", "טבעת בדיקה", "כסף", 4, 0, 900, [], [])));

        var path = TempFile();
        try
        {
            await ExportAsync(business, path);

            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("מוצרים");
                var row = RowOf(sheet, "טבעת בדיקה");
                sheet.Cell(row, Material).Value = "פלטינה";   // not a material of this business
                sheet.Cell(row, Price).Value = 1234;

                var fresh = sheet.LastRowUsed()!.RowNumber() + 1;
                sheet.Cell(fresh, Name).Value = "צמיד חדש";
                sheet.Cell(fresh, Type).Value = "צמיד";
                sheet.Cell(fresh, Material).Value = "כסף";
                sheet.Cell(fresh, Weight).Value = 12;
                sheet.Cell(fresh, Price).Value = 700;
                workbook.Save();
            }

            var result = await ImportAsync(business, path, dryRun: false);

            Assert.Equal(1, result.Created);
            Assert.Equal(0, result.Updated);
            Assert.Contains(result.Problems, p => p.Contains("פלטינה"));

            var products = (await WithProducts(business, s => s.GetProductsAsync())).Products;
            Assert.Equal(900m, products.Single(p => p.Id == product.Id).SitePrice); // the bad row changed nothing
            Assert.Equal(700m, products.Single(p => p.Name == "צמיד חדש").SitePrice);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Replacing the catalog with the file ──────────────────────────────────

    [Fact]
    public async Task ProductsMissingFromTheFile_AreOnlyListed_UntilAskedToDelete_AndAMajorityIsNeverDeleted()
    {
        var business = await NewBusinessAsync();
        var keep1 = await WithProducts(business, s => s.CreateProductAsync(new SaveProductDto("טבעת", "נשאר 1", "כסף", 4, 0, 100, [], [])));
        var keep2 = await WithProducts(business, s => s.CreateProductAsync(new SaveProductDto("טבעת", "נשאר 2", "כסף", 4, 0, 100, [], [])));
        var keep3 = await WithProducts(business, s => s.CreateProductAsync(new SaveProductDto("טבעת", "נשאר 3", "כסף", 4, 0, 100, [], [])));
        var drop = await WithProducts(business, s => s.CreateProductAsync(new SaveProductDto("טבעת", "לא בקובץ", "כסף", 4, 0, 100, [], [])));

        var path = TempFile();
        try
        {
            await ExportAsync(business, path);
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("מוצרים");
                sheet.Row(RowOf(sheet, "לא בקובץ")).Delete();
                workbook.Save();
            }

            // Without the flag the product is only reported.
            var listed = await ImportAsync(business, path, dryRun: false);
            Assert.Contains(listed.NotInFile, n => n.Contains("לא בקובץ"));
            Assert.Equal(0, listed.Deleted);
            Assert.Equal(4, (await WithProducts(business, s => s.GetProductsAsync())).Products.Count);

            // A dry run with the flag still deletes nothing.
            var dry = await ImportAsync(business, path, dryRun: true, deleteMissing: true);
            Assert.Equal(0, dry.Deleted);
            Assert.Equal(4, (await WithProducts(business, s => s.GetProductsAsync())).Products.Count);

            var done = await ImportAsync(business, path, dryRun: false, deleteMissing: true);
            Assert.Equal(1, done.Deleted);
            var left = (await WithProducts(business, s => s.GetProductsAsync())).Products;
            Assert.Equal(3, left.Count);
            Assert.DoesNotContain(left, p => p.Id == drop.Id);
            Assert.Contains(left, p => p.Id == keep1.Id);

            // A file that lost most of its rows is not applied as a deletion.
            await ExportAsync(business, path);
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("מוצרים");
                sheet.Row(RowOf(sheet, "נשאר 3")).Delete();
                sheet.Row(RowOf(sheet, "נשאר 2")).Delete();
                workbook.Save();
            }

            var guarded = await ImportAsync(business, path, dryRun: false, deleteMissing: true);
            Assert.Equal(0, guarded.Deleted);
            Assert.Contains(guarded.Problems, p => p.Contains("יותר ממחצית"));
            Assert.Equal(3, (await WithProducts(business, s => s.GetProductsAsync())).Products.Count);
            Assert.NotEqual(keep2.Id, Guid.Empty);
            Assert.NotEqual(keep3.Id, Guid.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Converting the earlier two-sheet file ────────────────────────────────

    [Fact]
    public void Convert_MakesOneSheet_KeepsTheOwnersEdits_AddsNamesAndSoldItems()
    {
        var input = TempFile();
        var output = TempFile();
        var id = Guid.NewGuid();

        try
        {
            using (var workbook = new XLWorkbook())
            {
                var products = workbook.AddWorksheet("מוצרים");
                string[] headers = ["מזהה (לא לשנות)", "שם המוצר", "סוג", "חומר", "משקל (גרם)", "שעות עבודה נוספות", "מחיר באתר",
                    "אוספים (מופרדים בפסיק)", "שם בשופיפי", "מחיר מומלץ (חישוב, לא לערוך)", "הערות שלי (למחוק / לעדכן / להוסיף)"];
                for (var c = 0; c < headers.Length; c++) products.Cell(1, c + 1).Value = headers[c];
                products.Cell(2, 1).Value = id.ToString();
                products.Cell(2, 2).Value = "טביעת אצבע";
                products.Cell(2, 3).Value = "שרשרת";
                products.Cell(2, 4).Value = "זהב 14";
                products.Cell(2, 5).Value = 3;
                products.Cell(2, 7).Value = 900;
                products.Cell(2, 8).Value = "כללי";
                products.Cell(2, 11).Value = "הערה שלי";

                var additions = workbook.AddWorksheet("תוספות");
                string[] aHeaders = ["מזהה המוצר (לא לשנות)", "שם המוצר (לא לערוך)", "סוג תוספת", "שם חופשי", "מחיר", "כמות"];
                for (var c = 0; c < aHeaders.Length; c++) additions.Cell(1, c + 1).Value = aHeaders[c];
                additions.Cell(2, 1).Value = id.ToString();
                additions.Cell(2, 3).Value = "אבן";
                additions.Cell(2, 5).Value = 150;
                additions.Cell(2, 6).Value = 2;

                var lists = workbook.AddWorksheet("רשימות");
                lists.Cell(1, 1).Value = "חומרים";
                lists.Cell(2, 1).Value = "זהב 14";
                workbook.SaveAs(input);
            }

            var hints = new List<ProductAlias> { new("Fingerprint Gold Necklace", "טביעת אצבע", "שרשרת", "זהב 14") };
            var sold = new List<(string Title, decimal Price)>
            {
                ("Fingerprint Gold Necklace - Oval", 900),   // already covered by the hint
                ("TULIP Silver Earrings", 250),               // a product the catalog does not have
            };

            var converted = ProductSheet.Convert(input, output, sold, hints);

            Assert.Equal(2, converted.Rows);
            Assert.Equal(1, converted.FromOrders);
            Assert.Equal(1, converted.Hinted);

            using var result = new XLWorkbook(output);
            Assert.Single(result.Worksheets);
            var sheet = result.Worksheet("מוצרים");

            var first = RowOf(sheet, "טביעת אצבע");
            Assert.Equal("אבן 150 x2", sheet.Cell(first, Additions).GetString());
            Assert.Equal("Fingerprint Gold Necklace", sheet.Cell(first, StoreName).GetString());
            Assert.Contains("הערה שלי", sheet.Cell(first, Notes).GetString());
            Assert.Equal(id.ToString(), sheet.Cell(first, Id).GetString());

            var added = RowOf(sheet, "TULIP Silver Earrings");
            Assert.Equal("עגילים", sheet.Cell(added, Type).GetString());
            Assert.Equal("כסף", sheet.Cell(added, Material).GetString());
            Assert.Equal(250, sheet.Cell(added, Price).GetDouble());
            Assert.Equal("TULIP Silver Earrings", sheet.Cell(added, StoreName).GetString());
            Assert.Equal("", sheet.Cell(added, Id).GetString()); // a new row: no id
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    // ── Tying old order lines to products ────────────────────────────────────

    [Fact]
    public async Task RelinkLines_TiesLinesToProductsByStoreName_WithoutChangingAnyAmount()
    {
        var business = await NewBusinessAsync();
        var u = Guid.NewGuid().ToString("N")[..8];
        var title = "Fingerprint Gold Necklace " + u;

        // An old order whose item had no product at the time.
        await using (var db = pg.NewDb())
        {
            var tenant = PostgresFixture.TenantFor(business);
            await new OrdersService(db, tenant).ImportHistoricalOrderAsync(new HistoricalOrder(
                "R" + u, "#R" + u, new DateOnly(2024, 3, 3), "דנה כהן", 0, null, null, true, new DateOnly(2024, 3, 5),
                [new HistoricalLine(title + " - Oval", null, 1, 900)]));
        }

        Guid orderId;
        await using (var db = pg.NewDb())
        {
            var order = await db.Orders.Include(o => o.Items).SingleAsync(o => o.BusinessId == business && o.ExternalId == "R" + u);
            orderId = order.Id;
            Assert.Null(order.Items.Single().ProductId);
            Assert.Equal(0m, (await db.Incomes.SingleAsync(i => i.OrderId == order.Id)).WorkHours);
        }

        // The catalog has the product, with the store name the order used.
        var product = await WithProducts(business, s => s.CreateProductAsync(
            new SaveProductDto("שרשרת", "טביעת אצבע " + u, "זהב", 3, 0.5m, 900, [], [], title)));

        await using (var db = pg.NewDb())
        {
            var tenant = PostgresFixture.TenantFor(business);
            var dry = await new OrdersService(db, tenant).RelinkLinesAsync(dryRun: true);
            Assert.Equal(1, dry.LinesLinked);
        }

        await using (var db = pg.NewDb())
            Assert.Null((await db.OrderLineItems.SingleAsync(l => l.OrderId == orderId)).ProductId); // a dry run saved nothing

        await using (var db = pg.NewDb())
        {
            var tenant = PostgresFixture.TenantFor(business);
            var done = await new OrdersService(db, tenant).RelinkLinesAsync();
            Assert.Equal(1, done.LinesLinked);
            Assert.Empty(done.StillWithoutProduct);
        }

        await using var check = pg.NewDb();
        var line = await check.OrderLineItems.Include(l => l.Order).SingleAsync(l => l.OrderId == orderId);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal("טביעת אצבע " + u, line.Name);
        Assert.Equal("Oval", line.Note);
        Assert.Equal("זהב", line.Material);
        Assert.Equal(2.5m, line.WorkHours); // 2 hours of gold + 0.5 extra

        // Nothing about the money moved.
        Assert.Equal(900m, line.UnitPrice);
        Assert.Equal(900m, line.Order.FinalAmount);
        var income = await check.Incomes.SingleAsync(i => i.OrderId == orderId);
        Assert.Equal(900m, income.Amount);
        Assert.Equal(2.5m, income.WorkHours); // the profit per hour of the old order can now be worked out
    }

    [Fact]
    public async Task RelinkLines_UsesAnAlias_WhenTheStoreRenamedTheProductSinceTheOldOrder()
    {
        var business = await NewBusinessAsync();
        var u = Guid.NewGuid().ToString("N")[..8];
        var oldTitle = "Fingerprint Gold Necklace " + u;

        await using (var db = pg.NewDb())
        {
            await new OrdersService(db, PostgresFixture.TenantFor(business)).ImportHistoricalOrderAsync(new HistoricalOrder(
                "S" + u, "#S" + u, new DateOnly(2024, 4, 4), "רינה לוי", 0, null, null, true, null,
                [new HistoricalLine(oldTitle + " - Oval", null, 1, 700)]));
        }

        // The product exists, but the store calls it something else today.
        var product = await WithProducts(business, s => s.CreateProductAsync(
            new SaveProductDto("שרשרת", "טביעת אצבע " + u, "זהב", 3, 0, 700, [], [], "14K Gold Fingerprint Necklace " + u)));

        // Without the alias nothing is found.
        await using (var db = pg.NewDb())
        {
            var none = await new OrdersService(db, PostgresFixture.TenantFor(business)).RelinkLinesAsync(dryRun: true);
            Assert.Equal(0, none.LinesLinked);
        }

        await using (var db = pg.NewDb())
        {
            var done = await new OrdersService(db, PostgresFixture.TenantFor(business)).RelinkLinesAsync(
                aliases: [new ProductAlias(oldTitle, "טביעת אצבע " + u, "שרשרת", "זהב")]);
            Assert.Equal(1, done.LinesLinked);
        }

        await using var check = pg.NewDb();
        var line = await check.OrderLineItems.SingleAsync(l => l.BusinessId == business);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal("Oval", line.Note);
        Assert.Equal(700m, line.UnitPrice);
    }
}
