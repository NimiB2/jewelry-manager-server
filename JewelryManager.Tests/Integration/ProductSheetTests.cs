using ClosedXML.Excel;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Collections.Dtos;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;
using JewelryManager.Tools;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>The catalog as an Excel workbook and back, against real PostgreSQL, in a business of its own.</summary>
[Collection("postgres")]
public class ProductSheetTests(PostgresFixture pg)
{
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

    private async Task<SheetUpdateResult> ImportAsync(Guid business, string path, bool dryRun)
    {
        await using var db = pg.NewDb();
        return await ProductSheet.ImportAsync(db, business, path, dryRun);
    }

    // The 1-based row of a product in the products sheet, found by its name.
    private static int RowOf(IXLWorksheet sheet, string name) =>
        sheet.RowsUsed().Skip(1).First(r => r.Cell(2).GetString() == name).RowNumber();

    [Fact]
    public async Task Export_ListsEveryProduct_MarksTheMissingOnes_AndTheFilledSheetUpdatesTheCatalog()
    {
        var business = await NewBusinessAsync();
        var rings = await CollectionAsync(business, "טבעות");
        var general = await GeneralAsync(business);

        // One complete product (with an addition) and one that came from the store with only a name and a price.
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

            // Nothing was edited: applying the file changes nothing.
            var untouched = await ImportAsync(business, path, dryRun: false);
            Assert.Equal(0, untouched.Updated);
            Assert.Equal(2, untouched.Unchanged);
            Assert.Empty(untouched.Problems);

            // Fill in what is missing.
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("מוצרים");
                var row = RowOf(sheet, "עגילי גרייס");
                sheet.Cell(row, 3).Value = "עגילים";
                sheet.Cell(row, 4).Value = "זהב";
                sheet.Cell(row, 5).Value = 2.5;
                sheet.Cell(row, 6).Value = 0.5;
                sheet.Cell(row, 7).Value = 520;
                sheet.Cell(row, 8).Value = "טבעות, כללי";

                var additions = workbook.Worksheet("תוספות");
                var last = additions.LastRowUsed()!.RowNumber() + 1;
                additions.Cell(last, 1).Value = sheet.Cell(row, 1).GetString();
                additions.Cell(last, 3).Value = "אחר";
                additions.Cell(last, 4).Value = "חריטה";
                additions.Cell(last, 5).Value = 15;
                additions.Cell(last, 6).Value = 1;
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
            Assert.Equal(0.5m, earrings.AdditionalWorkHours);
            Assert.Equal(520m, earrings.SitePrice);
            Assert.Equal("Grace Earrings", earrings.ShopifyName); // untouched
            Assert.False(earrings.NeedsDetails);
            Assert.Contains(rings, earrings.CollectionIds);
            Assert.Contains(general, earrings.CollectionIds);
            Assert.Equal("חריטה", Assert.Single(earrings.Additions).CustomName);

            // The first product kept everything it had, including its addition.
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
                sheet.Cell(row, 4).Value = "פלטינה";   // not a material of this business
                sheet.Cell(row, 7).Value = 1234;

                var fresh = sheet.LastRowUsed()!.RowNumber() + 1;
                sheet.Cell(fresh, 2).Value = "צמיד חדש";
                sheet.Cell(fresh, 3).Value = "צמיד";
                sheet.Cell(fresh, 4).Value = "כסף";
                sheet.Cell(fresh, 5).Value = 12;
                sheet.Cell(fresh, 7).Value = 700;
                workbook.Save();
            }

            var result = await ImportAsync(business, path, dryRun: false);

            Assert.Equal(1, result.Created);
            Assert.Equal(0, result.Updated);
            Assert.Contains(result.Problems, p => p.Contains("פלטינה"));

            var products = (await WithProducts(business, s => s.GetProductsAsync())).Products;
            Assert.Equal(900m, products.Single(p => p.Id == product.Id).SitePrice); // the bad row changed nothing
            var created = products.Single(p => p.Name == "צמיד חדש");
            Assert.Equal(700m, created.SitePrice);
            Assert.Equal("כסף", created.Material);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PrepareReview_FillsWhatTheNameShows_MarksGuesses_AndTheFileStillAppliesCleanly()
    {
        var business = await NewBusinessAsync();
        var general = await GeneralAsync(business);

        await using (var db = pg.NewDb())
        {
            var now = DateTime.UtcNow;
            Product Store(string name, string type) => new()
            {
                Id = Guid.NewGuid(), BusinessId = business, Name = name, Type = type, SitePrice = 500, ShopifyName = name,
                CreatedAt = now, UpdatedAt = now,
                Collections = [new ProductCollection { BusinessId = business, CollectionId = general }],
            };

            db.Products.AddRange(
                Store("LIA Gold Necklace", "שרשרת"),     // gold: clear
                Store("LIA Silver Necklace", "שרשרת"),   // silver: a guess
                Store("Mystery Piece", "טבעת"),            // nothing to go on
                Store("NAYA Gold Earrings", ""));          // type from the name
            await db.SaveChangesAsync();
        }

        var original = TempFile();
        var reviewed = TempFile();
        try
        {
            await ExportAsync(business, original);
            var summary = ReviewSheet.Prepare(original, reviewed);

            Assert.Equal(4, summary.Products);
            Assert.Equal(2, summary.MaterialsSuggested);   // both gold items
            Assert.Equal(1, summary.MaterialsGuessed);     // the silver one
            Assert.Equal(1, summary.TypesSuggested);       // earrings
            Assert.Equal(["Mystery Piece"], summary.Unresolved);

            using (var workbook = new XLWorkbook(reviewed))
            {
                var sheet = workbook.Worksheet("מוצרים");
                Assert.Equal("זהב", sheet.Cell(RowOf(sheet, "LIA Gold Necklace"), 4).GetString());
                Assert.Equal("כסף", sheet.Cell(RowOf(sheet, "LIA Silver Necklace"), 4).GetString());
                Assert.Equal("עגילים", sheet.Cell(RowOf(sheet, "NAYA Gold Earrings"), 3).GetString());
                Assert.Equal("", sheet.Cell(RowOf(sheet, "Mystery Piece"), 4).GetString());
                Assert.True(workbook.Worksheets.Contains("סיכום"));
            }

            // The extra columns do not get in the way: the file applies, and only the filled cells are changes.
            var dry = await ImportAsync(business, reviewed, dryRun: true);
            Assert.Equal(3, dry.Updated);
            Assert.Empty(dry.Problems);
            Assert.Equal(1, dry.Unchanged);
        }
        finally
        {
            File.Delete(original);
            File.Delete(reviewed);
        }
    }
}
