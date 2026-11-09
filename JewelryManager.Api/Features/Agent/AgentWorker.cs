using System.Collections.Concurrent;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Takes messages from the inbox and processes them, each in its own scope. Messages of one person go one at a
/// time (so "אשר" never races the question it answers); different people do not wait for each other.
/// </summary>
public class AgentWorker(AgentInbox inbox, IServiceScopeFactory scopes, IOptions<AgentOptions> options, ILogger<AgentWorker> log) : BackgroundService
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        await foreach (var message in inbox.Reader.ReadAllAsync(stoppingToken))
            _ = Task.Run(() => ProcessAsync(message, stoppingToken), stoppingToken);
    }

    private async Task ProcessAsync(IncomingMessage message, CancellationToken stop)
    {
        var gate = _gates.GetOrAdd(message.From, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(stop);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
            limit.CancelAfter(MessageTimeout);

            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AgentProcessor>().HandleAsync(message, limit.Token);
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("A message took too long and was stopped");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Agent message failed");
        }
        finally
        {
            gate.Release();
        }
    }
}
