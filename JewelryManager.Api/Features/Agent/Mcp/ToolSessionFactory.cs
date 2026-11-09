using JewelryManager.Mcp;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Mcp;

/// <summary>Gives the agent an API connection that acts as one whitelisted person.</summary>
public interface IBackendApiFactory
{
    IBackendApi CreateFor(Guid agentUserId);
}

public interface IToolSessionFactory
{
    Task<IToolSession> StartAsync(Guid agentUserId, CancellationToken ct);
}

public class BackendApiFactory(IHttpClientFactory httpFactory, IOptions<AgentOptions> options, IConfiguration configuration) : IBackendApiFactory
{
    public const string ClientName = "agent-api";

    public IBackendApi CreateFor(Guid agentUserId)
    {
        var config = options.Value;
        var http = httpFactory.CreateClient(ClientName);
        http.BaseAddress = new Uri(ResolveBaseAddress(config.ApiBaseUrl, configuration["ASPNETCORE_URLS"]));
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.Add(Auth.AgentServiceAuthMiddleware.KeyHeader, config.ServiceKey ?? "");
        http.DefaultRequestHeaders.Add(Auth.AgentServiceAuthMiddleware.UserHeader, agentUserId.ToString());
        return new HttpBackendApi(http);
    }

    /// <summary>An explicit address wins; otherwise the first address this server listens on, reached through localhost.</summary>
    public static string ResolveBaseAddress(string? configured, string? listening)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/') + "/";

        var first = (listening ?? "http://localhost:5000").Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        foreach (var wildcard in new[] { "+", "*", "0.0.0.0", "[::]" })
            first = first.Replace(wildcard, "localhost", StringComparison.Ordinal);
        return first.TrimEnd('/') + "/";
    }
}

public class ToolSessionFactory(IBackendApiFactory apis) : IToolSessionFactory
{
    public async Task<IToolSession> StartAsync(Guid agentUserId, CancellationToken ct) =>
        await McpToolSession.StartAsync(apis.CreateFor(agentUserId), ct);
}
