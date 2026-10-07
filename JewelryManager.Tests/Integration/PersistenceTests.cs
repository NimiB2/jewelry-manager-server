using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Collections.Dtos;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Settings.Dtos;
using JewelryManager.Api.Features.Users;
using JewelryManager.Api.Features.Users.Dtos;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Tests.Integration;

[Collection("postgres")]
public class PersistenceTests(PostgresFixture pg)
{
    private static readonly UpdateSettingsDto NoChange = new(null, null, null, null, null, null);

    // Every call builds a fresh DbContext + service = a "restarted server": anything read back
    // has to come from the database, not from memory.
    private async Task<T> WithSettings<T>(Guid business, Func<SettingsService, Task<T>> action)
    {
        await using var db = pg.NewDb();
        return await action(new SettingsService(db, PostgresFixture.TenantFor(business)));
    }

    private async Task SeedSettingsAsync(Guid business)
    {
        await using var db = pg.NewDb();
        if (await db.Settings.AnyAsync(s => s.BusinessId == business)) return;

        var now = DateTime.UtcNow;
        db.Settings.Add(new Settings
        {
            Id = Guid.NewGuid(), BusinessId = business, LaborHourRate = 100, ProfitFloorPercent = 30,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        await WithSettings(business, s => s.UpdateSettingsAsync(NoChange with { PreparationStages = ["a", "b"] }));
    }

    [Fact]
    public async Task ScalarSetting_IsStored_AndSurvivesAFreshContext()
    {
        await SeedSettingsAsync(pg.BusinessA);

        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { LaborHourRate = 150 }));

        var read = await WithSettings(pg.BusinessA, s => s.GetSettingsAsync());
        Assert.Equal(150, read.Data.LaborHourRate);
    }

    [Fact]
    public async Task Patch_OnlyReplacesTheSectionsThatWereSent()
    {
        await SeedSettingsAsync(pg.BusinessA);
        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { PreparationStages = ["a", "b"] }));

        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { ProfitFloorPercent = 45 }));

        var read = await WithSettings(pg.BusinessA, s => s.GetSettingsAsync());
        Assert.Equal(45, read.Data.ProfitFloorPercent);
        Assert.Equal(["a", "b"], read.Data.PreparationStages);
    }

    [Fact]
    public async Task Sections_AreStoredAsRealRows_InTheOrderTheyWereSent()
    {
        await SeedSettingsAsync(pg.BusinessA);
        var dto = NoChange with
        {
            Materials = new() { ["זהב"] = new(12, 0.5m, 1.8m), ["כסף"] = new(8, 1, 1.5m), ["אבץ"] = new(1, 1, 1.2m) },
            FeesItems =
            [
                new FeeItemDto("מע\"מ", 18, true, FeeKeys.Vat),
                new FeeItemDto("אחר", 2, null),
                new FeeItemDto("סליקה", 3, true, FeeKeys.CardFee),
                new FeeItemDto("קבועות", 17, true, FeeKeys.FixedExpenses),
            ],
            ProductAdditionTypes = [new("אבן", false), new("אחר", true)],
            PricingAdditions =
            [
                new PricingAdditionDto("אריזה", 1, [new("קופסה", 8), new("שקית", 3)]),
                new PricingAdditionDto("משלוח", 0, []),
            ],
            PreparationStages = ["ניקוי", "יציקה", "ליטוש"],
        };

        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(dto));

        var read = (await WithSettings(pg.BusinessA, s => s.GetSettingsAsync())).Data;
        Assert.Equal(["זהב", "כסף", "אבץ"], read.Materials.Keys);
        Assert.Equal(1.8m, read.Materials["זהב"].ProfitMultiplier);
        Assert.Equal(["מע\"מ", "אחר", "סליקה", "קבועות"], read.FeesItems.Select(f => f.Name));
        Assert.True(read.FeesItems[0].IsPermanent);
        Assert.Equal(FeeKeys.Vat, read.FeesItems[0].Key);
        Assert.False(read.FeesItems[1].IsPermanent);
        Assert.Null(read.FeesItems[1].Key);
        Assert.Equal([("אבן", false), ("אחר", true)], read.ProductAdditionTypes.Select(t => (t.Name, t.AllowsCustomName)));
        Assert.Equal(["אריזה", "משלוח"], read.PricingAdditions.Select(c => c.Name));
        Assert.Equal(["קופסה", "שקית"], read.PricingAdditions[0].Items.Select(i => i.Name));
        Assert.Empty(read.PricingAdditions[1].Items);
        Assert.Equal(["ניקוי", "יציקה", "ליטוש"], read.PreparationStages);

        // Not a blob: the data is genuinely in separate tables.
        await using var db = pg.NewDb();
        Assert.Equal(3, await db.Materials.CountAsync(m => m.BusinessId == pg.BusinessA));
        Assert.Equal(2, await db.PricingAdditionItems.CountAsync(i => i.BusinessId == pg.BusinessA));
    }

    [Fact]
    public async Task SendingASectionAgain_ReplacesItInsteadOfAppending()
    {
        await SeedSettingsAsync(pg.BusinessA);

        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { Materials = new() { ["x"] = new(1, 1, 1.5m) } }));
        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { Materials = new() { ["y"] = new(2, 2, 1.5m) } }));

        var read = await WithSettings(pg.BusinessA, s => s.GetSettingsAsync());
        Assert.Equal(["y"], read.Data.Materials.Keys);
    }

    [Fact]
    public async Task OverlappingPatches_ToDifferentSections_BothSurvive()
    {
        await SeedSettingsAsync(pg.BusinessA);

        await Task.WhenAll(
            WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { LaborHourRate = 222 })),
            WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { ProfitFloorPercent = 33 })),
            WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { Materials = new() { ["m"] = new(1, 1, 1.5m) } })),
            WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { PreparationStages = ["p", "q"] })));

        var read = (await WithSettings(pg.BusinessA, s => s.GetSettingsAsync())).Data;
        Assert.Equal(222, read.LaborHourRate);
        Assert.Equal(33, read.ProfitFloorPercent);
        Assert.Equal(["m"], read.Materials.Keys);
        Assert.Equal(["p", "q"], read.PreparationStages);
    }

    [Fact]
    public async Task OverlappingPatches_ToTheSameSection_DoNotFail_AndLeaveOneConsistentResult()
    {
        await SeedSettingsAsync(pg.BusinessA);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(n =>
            WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(
                NoChange with { Materials = new() { [$"mat{n}"] = new(n, 1, 1.5m) } }))));

        var read = await WithSettings(pg.BusinessA, s => s.GetSettingsAsync());
        Assert.Single(read.Data.Materials);
    }

    [Fact]
    public async Task DuplicateNames_AreRejectedWithBadRequest()
    {
        await SeedSettingsAsync(pg.BusinessA);

        await Assert.ThrowsAsync<BadRequestException>(() => WithSettings(pg.BusinessA, s =>
            s.UpdateSettingsAsync(NoChange with { PreparationStages = ["same", " same "] })));
        await Assert.ThrowsAsync<BadRequestException>(() => WithSettings(pg.BusinessA, s =>
            s.UpdateSettingsAsync(NoChange with { FeesItems = [new("f", 1, null), new("f", 2, null)] })));
    }

    [Fact]
    public async Task Fees_CannotLoseTheKeysThePricingFormulaNeeds()
    {
        await SeedSettingsAsync(pg.BusinessA);

        // Missing the VAT fee entirely.
        await Assert.ThrowsAsync<BadRequestException>(() => WithSettings(pg.BusinessA, s =>
            s.UpdateSettingsAsync(NoChange with
            {
                FeesItems = [new("c", 3, true, FeeKeys.CardFee), new("f", 17, true, FeeKeys.FixedExpenses)],
            })));

        // Unknown key.
        await Assert.ThrowsAsync<BadRequestException>(() => WithSettings(pg.BusinessA, s =>
            s.UpdateSettingsAsync(NoChange with { FeesItems = [new("x", 1, null, "bogus")] })));
    }

    [Fact]
    public async Task Settings_AreIsolatedBetweenBusinesses()
    {
        await SeedSettingsAsync(pg.BusinessA);
        await WithSettings(pg.BusinessA, s => s.UpdateSettingsAsync(NoChange with { Materials = new() { ["secret"] = new(1, 1, 1.5m) } }));

        await Assert.ThrowsAsync<NotFoundException>(() => WithSettings(pg.BusinessB, s => s.GetSettingsAsync()));
    }

    [Fact]
    public async Task Collections_CreateRenameDelete_PersistAcrossContexts()
    {
        Guid id;
        await using (var db = pg.NewDb())
        {
            var svc = new CollectionsService(db, PostgresFixture.TenantFor(pg.BusinessA));
            id = (await svc.CreateCollectionAsync(new CreateCollectionDto("חדשה"))).Id;
        }

        await using (var db = pg.NewDb())
        {
            var svc = new CollectionsService(db, PostgresFixture.TenantFor(pg.BusinessA));
            await svc.RenameCollectionAsync(id, new UpdateCollectionDto("שם חדש"));
        }

        await using (var db = pg.NewDb())
        {
            var svc = new CollectionsService(db, PostgresFixture.TenantFor(pg.BusinessA));
            Assert.Contains(await svc.GetCollectionsAsync(), c => c.Id == id && c.Name == "שם חדש");
            await svc.DeleteCollectionAsync(id);
        }

        await using (var db = pg.NewDb())
        {
            var svc = new CollectionsService(db, PostgresFixture.TenantFor(pg.BusinessA));
            Assert.DoesNotContain(await svc.GetCollectionsAsync(), c => c.Id == id);
        }
    }

    [Fact]
    public async Task Collections_PermanentCannotBeDeleted_AndOtherBusinessCannotTouchThem()
    {
        Guid permanentId;
        await using (var db = pg.NewDb())
        {
            var now = DateTime.UtcNow;
            var c = new Collection
            {
                Id = Guid.NewGuid(), BusinessId = pg.BusinessA, Name = "כללי", Key = "general",
                IsPermanent = true, CreatedAt = now, UpdatedAt = now,
            };
            db.Collections.Add(c);
            await db.SaveChangesAsync();
            permanentId = c.Id;
        }

        await using var db1 = pg.NewDb();
        var owner = new CollectionsService(db1, PostgresFixture.TenantFor(pg.BusinessA));
        await Assert.ThrowsAsync<BadRequestException>(() => owner.DeleteCollectionAsync(permanentId));

        await using var db2 = pg.NewDb();
        var intruder = new CollectionsService(db2, PostgresFixture.TenantFor(pg.BusinessB));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            intruder.RenameCollectionAsync(permanentId, new UpdateCollectionDto("x")));
        Assert.DoesNotContain(await intruder.GetCollectionsAsync(), c => c.Id == permanentId);
    }

    [Fact]
    public async Task Users_Created_ArePersisted_ScopedToBusiness_AndSuperAdminIsRejected()
    {
        await using (var db = pg.NewDb())
        {
            var svc = new UsersService(db, PostgresFixture.TenantFor(pg.BusinessA));
            await svc.CreateUserAsync(new CreateUserDto("emp@test.com", "Emp", null, null));
            await Assert.ThrowsAsync<BadRequestException>(() =>
                svc.CreateUserAsync(new CreateUserDto("x@test.com", null, null, Role.SuperAdmin)));
        }

        await using (var db = pg.NewDb())
        {
            var list = await new UsersService(db, PostgresFixture.TenantFor(pg.BusinessA)).GetUsersAsync();
            Assert.Contains(list, u => u.Email == "emp@test.com" && u.Role == Role.Employee);
        }

        await using (var db = pg.NewDb())
        {
            var list = await new UsersService(db, PostgresFixture.TenantFor(pg.BusinessB)).GetUsersAsync();
            Assert.DoesNotContain(list, u => u.Email == "emp@test.com");
        }
    }

    [Fact]
    public async Task Seeder_IsIdempotent_AndSeedsTheRealMaterialsAndPackaging()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        for (var i = 0; i < 2; i++)
        {
            await using var db = pg.NewDb();
            await new DbSeeder(db, config).SeedAsync();
        }

        var seeded = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var read = (await WithSettings(seeded, s => s.GetSettingsAsync())).Data;
        Assert.Equal(5, read.Materials.Count);
        Assert.Equal(["אבן", "שיבוץ", "ציפוי", "תוספת עגילים", "תוספת שרשרת", "אחר"], read.ProductAdditionTypes.Select(t => t.Name));
        Assert.True(read.ProductAdditionTypes.Single(t => t.Name == "אחר").AllowsCustomName);
        Assert.Equal(FeeKeys.Required.Order(), read.FeesItems.Select(f => f.Key!).Order());
        Assert.Equal(330, read.Materials["14K זהב"].PricePerGram);
        Assert.Equal(8, read.PricingAdditions.Single(c => c.Name == "אריזה").Items.Count);

        await using var check = pg.NewDb();
        Assert.Equal(1, await check.Settings.CountAsync(s => s.BusinessId == seeded));
        Assert.Equal(2, await check.Collections.CountAsync(c => c.BusinessId == seeded && c.IsPermanent));
    }
}
