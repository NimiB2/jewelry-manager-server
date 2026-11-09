using System.Globalization;
using System.Text.Json;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Mcp;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Proactive;

/// <summary>
/// The agent's own messages: a morning summary, a new store order waiting, an order open too long. Each is off
/// until the person turns it on, each is sent at most once, and the daily cleanup of old data runs here too.
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
                using var scope = scopes.CreateScope();
                await RunRoundAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "A proactive round failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunRoundAsync(IServiceProvider services, CancellationToken ct)
    {
        var directory = services.GetRequiredService<AgentUserDirectory>();
        var apis = services.GetRequiredService<IBackendApiFactory>();
        var sent = services.GetRequiredService<ProactiveLog>();
        var whatsapp = services.GetRequiredService<IWhatsAppClient>();

        foreach (var user in await directory.ListActiveAsync(ct))
        {
            var api = apis.CreateFor(user.Id);

            if (user.MorningSummary && AgentClock.LocalNow().Hour >= options.Value.Proactive.MorningHour)
                await SendOnceAsync(whatsapp, sent, user, "morning", AgentClock.LocalToday().ToString("yyyy-MM-dd"),
                    () => BuildMorningAsync(api, user, ct), options.Value.Proactive.MorningTemplate, ct);

            if (user.StoreOrderAlerts)
                foreach (var (number, customer) in await PendingStoreOrdersAsync(api, ct))
                    await SendOnceAsync(whatsapp, sent, user, "store_order", number.ToString(CultureInfo.InvariantCulture),
                        () => Task.FromResult<string?>(ProactiveMessages.StoreOrder(number, customer)), options.Value.Proactive.StoreOrderTemplate, ct);

            if (user.StuckOrderAlerts)
                foreach (var (number, customer, days) in await StuckOrdersAsync(api, ct))
                {
                    // One reminder per order per StuckDays period, not a message every round.
                    var bucket = AgentClock.LocalToday().DayNumber / Math.Max(1, options.Value.Proactive.StuckDays);
                    await SendOnceAsync(whatsapp, sent, user, "stuck_order", $"{number}:{bucket}",
                        () => Task.FromResult<string?>(ProactiveMessages.StuckOrder(number, customer, days)), options.Value.Proactive.StuckOrderTemplate, ct);
                }
        }

        await CleanupOncePerDayAsync(services, ct);
    }

    private async Task SendOnceAsync(IWhatsAppClient whatsapp, ProactiveLog sent, AgentUser user, string kind, string key,
        Func<Task<string?>> build, string? template, CancellationToken ct)
    {
        if (!await sent.TryClaimAsync(user.Id, kind, key, ct)) return;

        try
        {
            var text = await build();
            if (text is null)
            {
                await sent.ReleaseAsync(user.Id, kind, key, ct);
                return;
            }

            try
            {
                await whatsapp.SendTextAsync(user.Phone, text, ct);
            }
            catch (HttpRequestException) when (!string.IsNullOrWhiteSpace(template))
            {
                // Outside the 24-hour window only an approved template may be sent.
                await whatsapp.SendTemplateAsync(user.Phone, template, options.Value.Proactive.TemplateLanguage, [text], ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Proactive {Kind} message could not be sent; it will be tried again", kind);
            await sent.ReleaseAsync(user.Id, kind, key, ct);
        }
    }

    private static async Task<string?> BuildMorningAsync(IBackendApi api, AgentUser user, CancellationToken ct)
    {
        var attention = await api.GetAsync("/orders/attention", ct);
        var active = await api.GetAsync("/orders?status=active", ct);
        var yesterday = AgentClock.LocalToday().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var money = await api.GetAsync($"/finances?from={yesterday}&to={yesterday}", ct);

        var summary = money.TryGetProperty("summary", out var s) ? s : default;
        return ProactiveMessages.Morning(
            user.Gender,
            Int(attention, "pending"), Int(attention, "alerts"), Int(attention, "messagesToSend"),
            active.TryGetProperty("summary", out var a) ? Int(a, "count") : 0,
            Dec(summary, "totalIncome"), Dec(summary, "totalExpenses"));
    }

    private static async Task<List<(int Number, string? Customer)>> PendingStoreOrdersAsync(IBackendApi api, CancellationToken ct)
    {
        var pending = await api.GetAsync("/orders/pending", ct);
        return pending.ValueKind != JsonValueKind.Array
            ? []
            : pending.EnumerateArray().Where(o => o.TryGetProperty("number", out _))
                .Select(o => (o.GetProperty("number").GetInt32(), Text(o, "customer") ?? Text(o, "externalName"))).ToList();
    }

    private async Task<List<(int Number, string? Customer, int Days)>> StuckOrdersAsync(IBackendApi api, CancellationToken ct)
    {
        var data = await api.GetAsync("/orders?status=active", ct);
        var limit = options.Value.Proactive.StuckDays;
        var today = AgentClock.LocalToday();
        var result = new List<(int, string?, int)>();

        if (data.TryGetProperty("orders", out var orders))
        {
            foreach (var order in orders.EnumerateArray())
            {
                if (order.TryGetProperty("isPendingApproval", out var pending) && pending.ValueKind == JsonValueKind.True) continue;
                if (!DateOnly.TryParse(Text(order, "date"), CultureInfo.InvariantCulture, out var date)) continue;

                var days = today.DayNumber - date.DayNumber;
                if (days >= limit && order.TryGetProperty("number", out var n))
                    result.Add((n.GetInt32(), Text(order, "customer"), days));
            }
        }

        return result;
    }

    private async Task CleanupOncePerDayAsync(IServiceProvider services, CancellationToken ct)
    {
        var today = AgentClock.LocalToday();
        if (_lastCleanup == today) return;
        _lastCleanup = today;

        await services.GetRequiredService<ConversationStore>().PurgeAsync(ct);
        await services.GetRequiredService<AuditService>().PurgeAsync(ct);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : null;
}
