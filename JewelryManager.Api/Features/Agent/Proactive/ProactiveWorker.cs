using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Coach;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Mcp;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Proactive;

/// <summary>
/// The agent's own round, every five minutes, for each whitelisted person: real alerts (a store order waiting,
/// an order open too long), then the rhythm she asked for (morning, Friday, month end), then the coach's ideas.
/// Everything speaks through the outbox, so the attention rules apply, and every item through the ledger,
/// so nothing finished or refused is raised again. The daily upkeep (metal index, closing what she finished,
/// removing old data) runs here too.
/// </summary>
public class ProactiveWorker(IServiceScopeFactory scopes, IOptions<AgentOptions> options, ILogger<ProactiveWorker> log) : BackgroundService
{
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(5);
    private DateOnly _lastCleanup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        using var timer = new PeriodicTimer(Round);
        do
        {
            try
            {
                await RunRoundAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "A proactive round failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunRoundAsync(CancellationToken ct)
    {
        List<AgentUser> users;
        using (var scope = scopes.CreateScope())
            users = await scope.ServiceProvider.GetRequiredService<AgentUserDirectory>().ListActiveAsync(ct);

        foreach (var user in users)
        {
            // One scope per person, so a problem with one never disturbs another.
            try
            {
                using var scope = scopes.CreateScope();
                await RunForUserAsync(scope.ServiceProvider, user, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "The round failed for one person");
            }
        }

        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MetalIndexService>().RunDailyAsync(users, ct);
            await CleanupOncePerDayAsync(scope.ServiceProvider, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The daily upkeep failed");
        }
    }

    private async Task RunForUserAsync(IServiceProvider services, AgentUser user, CancellationToken ct)
    {
        var apis = services.GetRequiredService<IBackendApiFactory>();
        var rhythm = services.GetRequiredService<RhythmService>();

        if (user.StoreOrderAlerts) await StoreOrderAlertsAsync(services, user, apis.CreateFor(user.Id), ct);
        if (user.StuckOrderAlerts) await StuckOrderAlertsAsync(services, user, apis.CreateFor(user.Id), ct);

        await rhythm.ObligationsAsync(user, ct);
        await rhythm.MorningAsync(user, ct);
        await rhythm.FridayAsync(user, ct);
        await services.GetRequiredService<TaskCoach>().RunAsync(user, ct);
        await rhythm.MonthEndAsync(user, ct);
        await services.GetRequiredService<OccasionPlanner>().RunAsync(user, ct);
        await services.GetRequiredService<AnniversaryCoach>().RunAsync(user, ct);

        // Late in the day, close what she told the agent she finished.
        if (AgentClock.LocalNow().Hour >= 20)
            await services.GetRequiredService<DoneReconciler>().RunAsync(user, ct);
    }

    private async Task StoreOrderAlertsAsync(IServiceProvider services, AgentUser user, IBackendApi api, CancellationToken ct)
    {
        var sent = services.GetRequiredService<ProactiveLog>();
        var outbox = services.GetRequiredService<ProactiveOutbox>();

        var pending = await api.GetAsync("/orders/pending", ct);
        if (pending.ValueKind != JsonValueKind.Array) return;

        foreach (var order in pending.EnumerateArray().Where(o => o.TryGetProperty("number", out _)))
        {
            var number = order.GetProperty("number").GetInt32();
            var key = number.ToString(CultureInfo.InvariantCulture);
            if (!await sent.TryClaimAsync(user.Id, "store_order", key, ct)) continue;

            var customer = Text(order, "customer") ?? Text(order, "externalName");
            if (!await outbox.SendAsync(user, ProactiveMessages.StoreOrder(number, customer), MessageClass.Alert, "store_order",
                    null, ct, options.Value.Proactive.StoreOrderTemplate))
                await sent.ReleaseAsync(user.Id, "store_order", key, ct);
        }
    }

    private async Task StuckOrderAlertsAsync(IServiceProvider services, AgentUser user, IBackendApi api, CancellationToken ct)
    {
        var sent = services.GetRequiredService<ProactiveLog>();
        var outbox = services.GetRequiredService<ProactiveOutbox>();

        var data = await api.GetAsync("/orders?status=active", ct);
        if (!data.TryGetProperty("orders", out var orders)) return;

        var limit = options.Value.Proactive.StuckDays;
        var today = AgentClock.LocalToday();
        foreach (var order in orders.EnumerateArray())
        {
            if (order.TryGetProperty("isPendingApproval", out var waiting) && waiting.ValueKind == JsonValueKind.True) continue;
            if (!DateOnly.TryParse(Text(order, "date"), CultureInfo.InvariantCulture, out var date) || !order.TryGetProperty("number", out var n)) continue;

            var days = today.DayNumber - date.DayNumber;
            if (days < limit) continue;

            // One reminder per order per StuckDays period, not a message every round.
            var key = $"{n.GetInt32()}:{today.DayNumber / Math.Max(1, limit)}";
            if (!await sent.TryClaimAsync(user.Id, "stuck_order", key, ct)) continue;

            if (!await outbox.SendAsync(user, ProactiveMessages.StuckOrder(n.GetInt32(), Text(order, "customer"), days), MessageClass.Alert, "stuck_order",
                    null, ct, options.Value.Proactive.StuckOrderTemplate))
                await sent.ReleaseAsync(user.Id, "stuck_order", key, ct);
        }
    }

    private async Task CleanupOncePerDayAsync(IServiceProvider services, CancellationToken ct)
    {
        var today = AgentClock.LocalToday();
        if (_lastCleanup == today) return;
        _lastCleanup = today;

        await services.GetRequiredService<ConversationStore>().PurgeAsync(ct);
        await services.GetRequiredService<AuditService>().PurgeAsync(ct);
        await services.GetRequiredService<CoachLedger>().ExpireStaleIdeasAsync(ct);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
