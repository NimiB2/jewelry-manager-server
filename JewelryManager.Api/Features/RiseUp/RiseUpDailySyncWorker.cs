using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.RiseUp;

/// <summary>Pulls RiseUp once a day for the owner's business. Without a token it does nothing.</summary>
public class RiseUpDailySyncWorker(
    IServiceScopeFactory scopes, IOptions<RiseUpOptions> options, IConfiguration configuration, ILogger<RiseUpDailySyncWorker> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    private DateOnly? _lastRunDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.CanSync)
        {
            log.LogInformation("RiseUp daily sync is off: no token is set");
            return;
        }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await RunIfDueAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "The RiseUp daily sync failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunIfDueAsync(DateTimeOffset utcNow, CancellationToken ct)
    {
        var localNow = RiseUpSchedule.LocalNow(utcNow);
        if (!RiseUpSchedule.IsDue(localNow, _lastRunDay)) return;

        // Set before the attempt, so a failing token is tried once a day and not every hour.
        _lastRunDay = DateOnly.FromDateTime(localNow);
        await SyncOwnerBusinessAsync(RiseUpSchedule.MonthsToSync(localNow), ct);
    }

    private async Task SyncOwnerBusinessAsync(IReadOnlyList<string> months, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();

        var ownerEmail = configuration["Seed:OwnerEmail"];
        var businessId = string.IsNullOrWhiteSpace(ownerEmail)
            ? null
            : await db.Users.AsNoTracking().Where(u => u.Email == ownerEmail).Select(u => u.BusinessId).FirstOrDefaultAsync(ct);
        if (businessId is null)
        {
            log.LogWarning("RiseUp daily sync skipped: the owner account was not found or has no business");
            return;
        }

        // No signed-in user exists in a background run, so the business is set the way a verified webhook sets it.
        services.GetRequiredService<CurrentUserAccessor>().UseBusiness(businessId.Value);
        var sync = services.GetRequiredService<RiseUpSyncService>();

        foreach (var month in months)
        {
            try
            {
                var result = await sync.SyncMonthAsync(month);
                log.LogInformation("RiseUp {Month} synced: {Added} new, {Known} already known", month, result.Added, result.AlreadyKnown);
            }
            catch (BadRequestException ex)
            {
                // An expired token, a closed month or RiseUp being down. The next day's run tries again.
                log.LogWarning("RiseUp {Month} was not synced: {Reason}", month, ex.Message);
            }
        }
    }
}
