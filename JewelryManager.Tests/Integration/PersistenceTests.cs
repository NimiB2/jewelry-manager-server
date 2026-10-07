using System.Text.Json;
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

    // Each helper builds fresh DbContext + service instances = a "restarted server".
    private SettingsService Settings(Guid business, out AppDbContext db)
    {
        db = pg.NewDb();
        return new SettingsService(db, PostgresFixture.TenantFor(business));
    }

    private async Task SeedSettingsAsync(Guid business)
    {
        await using var db = pg.NewDb();
        if (await db.Settings.AnyAsync(s => s.BusinessId == business)) return;
        db.Settings.Add(new Settings
        {
            Id = Guid.NewGuid(), BusinessId = business, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Data = JsonDocument.Parse("""{"laborHourRate":100,"profitFloorPercent":30,"preparationStages":["a","b"]}"""),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SettingsPatch_IsStored_AndSurvivesAFreshContext()
    {
        await SeedSettingsAsync(pg.BusinessA);

        var first = Settings(pg.BusinessA, out var db1);
        await using (db1)
            await first.UpdateSettingsAsync(NoChange with { LaborHourRate = 150 });

        var second = Settings(pg.BusinessA, out var db2);
        await using (db2)
        {
            var data = (await second.GetSettingsAsync()).Data.RootElement;
            Assert.Equal(150, data.GetProperty("laborHourRate").GetDouble());
        }
    }

    [Fact]
    public async Task SettingsPatch_OnlyTouchesSentKeys()
    {
        await SeedSettingsAsync(pg.BusinessA);

        var svc = Settings(pg.BusinessA, out var db);
        await using (db)
            await svc.UpdateSettingsAsync(NoChange with { ProfitFloorPercent = 45 });

        var read = Settings(pg.BusinessA, out var db2);
        await using (db2)
        {
            var data = (await read.GetSettingsAsync()).Data.RootElement;
            Assert.Equal(45, data.GetProperty("profitFloorPercent").GetDouble());
            Assert.True(data.TryGetProperty("preparationStages", out var stages));
            Assert.Equal(2, stages.GetArrayLength());
        }
    }

    [Fact]
    public async Task SettingsPatch_RoundTripsComplexSections()
    {
        await SeedSettingsAsync(pg.BusinessA);
        var dto = NoChange with
        {
            Materials = new() { ["זהב"] = new MaterialSettingsDto(12, 0.5, 1.8) },
            FeesItems = [new FeeItemDto("מע\"מ", 18, true), new FeeItemDto("אחר", 2, null)],
            PricingAdditions = [new PricingAdditionDto("אריזה", 1, [new PricingItemDto("קופסה", 8)])],
        };

        var svc = Settings(pg.BusinessA, out var db);
        await using (db) await svc.UpdateSettingsAsync(dto);

        var read = Settings(pg.BusinessA, out var db2);
        await using (db2)
        {
            var data = (await read.GetSettingsAsync()).Data.RootElement;
            Assert.Equal(1.8, data.GetProperty("materials").GetProperty("זהב").GetProperty("profitMultiplier").GetDouble());
            Assert.True(data.GetProperty("feesItems")[0].GetProperty("isPermanent").GetBoolean());
            Assert.False(data.GetProperty("feesItems")[1].TryGetProperty("isPermanent", out _));
            Assert.Equal("קופסה", data.GetProperty("pricingAdditions")[0].GetProperty("items")[0].GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task OverlappingPatchesToDifferentSections_BothSurvive()
    {
        await SeedSettingsAsync(pg.BusinessA);

        await Task.WhenAll(
            Task.Run(async () =>
            {
                var s = Settings(pg.BusinessA, out var d);
                await using (d) await s.UpdateSettingsAsync(NoChange with { LaborHourRate = 222 });
            }),
            Task.Run(async () =>
            {
                var s = Settings(pg.BusinessA, out var d);
                await using (d) await s.UpdateSettingsAsync(NoChange with { ProfitFloorPercent = 33 });
            }));

        var read = Settings(pg.BusinessA, out var db);
        await using (db)
        {
            var data = (await read.GetSettingsAsync()).Data.RootElement;
            Assert.Equal(222, data.GetProperty("laborHourRate").GetDouble());
            Assert.Equal(33, data.GetProperty("profitFloorPercent").GetDouble());
        }
    }

    [Fact]
    public async Task Settings_AreIsolatedBetweenBusinesses()
    {
        await SeedSettingsAsync(pg.BusinessA);

        var other = Settings(pg.BusinessB, out var db);
        await using (db)
            await Assert.ThrowsAsync<NotFoundException>(() => other.GetSettingsAsync());
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
    public async Task Seeder_IsIdempotent()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        for (var i = 0; i < 2; i++)
        {
            await using var db = pg.NewDb();
            await new DbSeeder(db, config).SeedAsync();
        }

        await using var check = pg.NewDb();
        var seeded = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal(1, await check.Settings.CountAsync(s => s.BusinessId == seeded));
        Assert.Equal(2, await check.Collections.CountAsync(c => c.BusinessId == seeded && c.IsPermanent));
    }
}
