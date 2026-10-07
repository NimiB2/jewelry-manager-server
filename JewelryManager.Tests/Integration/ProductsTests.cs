using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Collections.Dtos;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Pricing.Dtos;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Products.Dtos;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

/// <summary>Products + pricing against real PostgreSQL. Uses its own business so it never touches other tests' data.</summary>
[Collection("postgres")]
public class ProductsTests(PostgresFixture pg)
{
    private Guid Business => pg.BusinessC;

    private static readonly ProductAdditionDto Stone = new("אבן", null, 30, 2);
    private static readonly ProductAdditionDto Other = new("אחר", "חריטה", 15, 1);

    private async Task<T> WithProducts<T>(Guid business, Func<ProductsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        var tenant = PostgresFixture.TenantFor(business);
        return await action(new ProductsService(db, tenant, new PricingService(db, tenant)));
    }

    private async Task<T> WithSettings<T>(Guid business, Func<SettingsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new SettingsService(db, PostgresFixture.TenantFor(business)));
    }

    // A complete, known pricing setup: silver at 8/g (1 fixed labor hour per piece, x1.5),
    // packaging worth 5, labor 100/h, fees 3% card / 18% VAT / 17% fixed.
    private async Task ResetPricingSetupAsync()
    {
        await using (var db = pg.NewDb())
        {
            var now = DateTime.UtcNow;
            if (!await db.Settings.AnyAsync(s => s.BusinessId == Business))
                db.Settings.Add(new Settings { Id = Guid.NewGuid(), BusinessId = Business, CreatedAt = now, UpdatedAt = now });
            if (!await db.Collections.AnyAsync(c => c.BusinessId == Business && c.Key == "general"))
                db.Collections.Add(new Collection
                {
                    Id = Guid.NewGuid(), BusinessId = Business, Name = "כללי", Key = "general",
                    IsPermanent = true, CreatedAt = now, UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        await WithSettings(Business, s => s.UpdateSettingsAsync(new UpdateSettingsDto(
            Materials: new() { ["כסף"] = new(8, 1, 1.5m), ["זהב"] = new(330, 2, 1.8m) },
            LaborHourRate: 100,
            PricingAdditions: [new PricingAdditionDto("אריזה", 0, [new("קופסה", 3), new("שקית", 2)])],
            FeesItems:
            [
                new("עמלת סליקה", 3, true, FeeKeys.CardFee),
                new("מע\"מ", 18, true, FeeKeys.Vat),
                new("עמלת עלויות קבועות", 17, true, FeeKeys.FixedExpenses),
            ],
            ProfitFloorPercent: 30,
            PreparationStages: ["יציקה"],
            ProductAdditionTypes: [new("אבן", false), new("שיבוץ", false), new("אחר", true)])));
    }

    private async Task<Guid> GeneralIdAsync()
    {
        await using var db = pg.NewDb();
        return await db.Collections.Where(c => c.BusinessId == Business && c.Key == "general").Select(c => c.Id).SingleAsync();
    }

    private static SaveProductDto Ring(IEnumerable<Guid>? collections = null, List<ProductAdditionDto>? additions = null,
        string material = "כסף", decimal sitePrice = 1500) =>
        new("טבעת", "סולטייר", material, 5, 0.5m, sitePrice, additions ?? [Stone], (collections ?? []).ToList());

    [Fact]
    public async Task Create_IsStored_AndThePriceIsExactlyWhatTheFormulaGives()
    {
        await ResetPricingSetupAsync();

        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));
        var read = await WithProducts(Business, s => s.GetProductAsync(created.Id));

        // 5g silver: 40 metal + (1 fixed + 0.5 extra hours) x 100 = 150 labor + 60 stones + 5 packaging = 255.
        var expected = PricingFormula.Calculate(new PricingFormulaInput(
            5, 8, 1.5, 100, 0, 60, 0, 5, 0.17, 1.5, 0.03, 0.18));
        Assert.NotNull(read.Price);
        Assert.Equal(255m, read.Price.DirectCosts);
        Assert.Equal(1.5m, read.Price.LaborHours);
        Assert.Equal(Math.Round((decimal)expected.FinalPriceInclVat, 2), read.Price.RecommendedPrice);
        Assert.Equal(1500, read.SitePrice);
        Assert.Equal("סולטייר", read.Name);
        Assert.Equal(("אבן", 2), (read.Additions.Single().TypeName, read.Additions.Single().Quantity));
    }

    [Fact]
    public async Task Create_WithoutCollections_GoesToGeneral()
    {
        await ResetPricingSetupAsync();
        var general = await GeneralIdAsync();

        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));

        Assert.Equal([general], created.CollectionIds);
    }

    [Fact]
    public async Task Create_RejectsACollectionOfAnotherBusiness()
    {
        await ResetPricingSetupAsync();
        Guid foreign;
        await using (var db = pg.NewDb())
        {
            var c = new Collection
            {
                Id = Guid.NewGuid(), BusinessId = pg.BusinessB, Name = "זר", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Collections.Add(c);
            await db.SaveChangesAsync();
            foreign = c.Id;
        }

        await Assert.ThrowsAsync<BadRequestException>(() =>
            WithProducts(Business, s => s.CreateProductAsync(Ring([foreign]))));
    }

    [Fact]
    public async Task Update_ReplacesAdditionsAndCollections_AndKeepsTheRest()
    {
        await ResetPricingSetupAsync();
        var general = await GeneralIdAsync();
        var extra = (await WithCollection("סדרה")).Id;

        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring([general])));
        await WithProducts(Business, s => s.UpdateProductAsync(created.Id, Ring([extra], [Other])));
        // A second save with the very same collection must not clash on the junction key.
        await WithProducts(Business, s => s.UpdateProductAsync(created.Id, Ring([extra], [Other])));

        var read = await WithProducts(Business, s => s.GetProductAsync(created.Id));
        Assert.Equal([extra], read.CollectionIds);
        var addition = Assert.Single(read.Additions);
        Assert.Equal(("אחר", "חריטה"), (addition.TypeName, addition.CustomName));
    }

    [Fact]
    public async Task SitePrice_QuickEdit_Persists()
    {
        await ResetPricingSetupAsync();
        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));

        await WithProducts(Business, s => s.UpdateSitePriceAsync(created.Id, new UpdateSitePriceDto(1777)));

        Assert.Equal(1777, (await WithProducts(Business, s => s.GetProductAsync(created.Id))).SitePrice);
    }

    [Fact]
    public async Task Delete_RemovesTheProductItsAdditionsAndItsCollectionLinks()
    {
        await ResetPricingSetupAsync();
        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));

        await WithProducts(Business, async s => { await s.DeleteProductAsync(created.Id); return 0; });

        await Assert.ThrowsAsync<NotFoundException>(() => WithProducts(Business, s => s.GetProductAsync(created.Id)));
        await using var db = pg.NewDb();
        Assert.False(await db.ProductAdditions.AnyAsync(a => a.ProductId == created.Id));
        Assert.False(await db.ProductCollections.AnyAsync(l => l.ProductId == created.Id));
    }

    [Fact]
    public async Task Products_AreIsolatedBetweenBusinesses()
    {
        await ResetPricingSetupAsync();
        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));

        await Assert.ThrowsAsync<NotFoundException>(() => WithProducts(pg.BusinessB, s => s.GetProductAsync(created.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            WithProducts(pg.BusinessB, s => s.UpdateSitePriceAsync(created.Id, new UpdateSitePriceDto(1))));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            WithProducts(pg.BusinessB, async s => { await s.DeleteProductAsync(created.Id); return 0; }));
        Assert.DoesNotContain((await WithProducts(pg.BusinessB, s => s.GetProductsAsync())).Products, p => p.Id == created.Id);
    }

    [Fact]
    public async Task Additions_MustBeKnownTypes_AndFreeTextTypesNeedAName()
    {
        await ResetPricingSetupAsync();

        await Assert.ThrowsAsync<BadRequestException>(() => WithProducts(Business, s =>
            s.CreateProductAsync(Ring(additions: [new("לא קיים", null, 5, 1)]))));
        await Assert.ThrowsAsync<BadRequestException>(() => WithProducts(Business, s =>
            s.CreateProductAsync(Ring(additions: [new("אחר", "  ", 5, 1)]))));
    }

    [Fact]
    public async Task Prices_FollowTheSettings_NothingIsStoredPerProduct()
    {
        await ResetPricingSetupAsync();
        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));
        var before = created.Price!.RecommendedPrice;

        await WithSettings(Business, s => s.UpdateSettingsAsync(new UpdateSettingsDto(null, 200, null, null, null, null)));

        var after = (await WithProducts(Business, s => s.GetProductAsync(created.Id))).Price!.RecommendedPrice;
        Assert.True(after > before);
    }

    [Fact]
    public async Task RemovedMaterial_KeepsTheProductListed_WithAnExplainedMissingPrice()
    {
        await ResetPricingSetupAsync();
        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring(material: "זהב")));

        await WithSettings(Business, s => s.UpdateSettingsAsync(new UpdateSettingsDto(
            new() { ["כסף"] = new(8, 1, 1.5m) }, null, null, null, null, null)));

        var list = await WithProducts(Business, s => s.GetProductsAsync());
        var product = list.Products.Single(p => p.Id == created.Id);
        Assert.Null(product.Price);
        Assert.Contains("זהב", product.PriceError);
        Assert.NotNull(list.Pricing);
    }

    [Fact]
    public async Task MarginTooLow_GivesAnErrorForThatProductOnly()
    {
        await ResetPricingSetupAsync();
        await WithSettings(Business, s => s.UpdateSettingsAsync(new UpdateSettingsDto(
            new() { ["כסף"] = new(8, 1, 1.5m), ["זהב"] = new(330, 2, 1m) }, null, null, null, null, null)));

        var good = await WithProducts(Business, s => s.CreateProductAsync(Ring()));
        var bad = await WithProducts(Business, s => s.CreateProductAsync(Ring(material: "זהב")));

        var list = (await WithProducts(Business, s => s.GetProductsAsync())).Products;
        Assert.NotNull(list.Single(p => p.Id == good.Id).Price);
        Assert.Null(list.Single(p => p.Id == bad.Id).Price);
        Assert.NotNull(list.Single(p => p.Id == bad.Id).PriceError);
    }

    [Fact]
    public async Task Calculator_Preview_MatchesTheSavedProductPrice_AndSavesNothing()
    {
        await ResetPricingSetupAsync();
        var created = await WithProducts(Business, s => s.CreateProductAsync(Ring()));

        PriceBreakdown preview;
        await using (var db = pg.NewDb())
        {
            var tenant = PostgresFixture.TenantFor(Business);
            preview = await new PricingService(db, tenant).CalculateAsync(new CalculatePriceDto("כסף", 5, 0.5m, [Stone]));
        }

        Assert.Equal(created.Price, preview);
    }

    [Fact]
    public async Task DeletingACollection_MovesItsProductsToGeneral()
    {
        await ResetPricingSetupAsync();
        var general = await GeneralIdAsync();
        var series = (await WithCollection("למחיקה")).Id;
        var only = await WithProducts(Business, s => s.CreateProductAsync(Ring([series])));
        var both = await WithProducts(Business, s => s.CreateProductAsync(Ring([series, general])));

        await using (var db = pg.NewDb())
            await new CollectionsService(db, PostgresFixture.TenantFor(Business)).DeleteCollectionAsync(series);

        Assert.Equal([general], (await WithProducts(Business, s => s.GetProductAsync(only.Id))).CollectionIds);
        Assert.Equal([general], (await WithProducts(Business, s => s.GetProductAsync(both.Id))).CollectionIds);
    }

    private async Task<Collection> WithCollection(string name)
    {
        await using var db = pg.NewDb();
        return await new CollectionsService(db, PostgresFixture.TenantFor(Business))
            .CreateCollectionAsync(new CreateCollectionDto(name + Guid.NewGuid().ToString("N")[..6]));
    }
}
