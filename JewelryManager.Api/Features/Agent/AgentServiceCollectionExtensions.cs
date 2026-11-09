using JewelryManager.Api.Features.Agent.Media;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Proactive;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;

namespace JewelryManager.Api.Features.Agent;

/// <summary>Wires the WhatsApp agent into the container. Program.cs only calls this one method.</summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddWhatsAppAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));

        // Model, tools and channel are each behind an interface, so any of them can be replaced (and faked in tests).
        services.AddSingleton<IAgentModel, AnthropicAgentModel>();
        services.AddSingleton<IBackendApiFactory, BackendApiFactory>();
        services.AddSingleton<IToolSessionFactory, ToolSessionFactory>();
        services.AddHttpClient(BackendApiFactory.ClientName);

        services.AddHttpClient<IWhatsAppClient, WhatsAppClient>(client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<ITranscriber, OpenAiCompatibleTranscriber>(client => client.Timeout = TimeSpan.FromSeconds(60));

        services.AddSingleton<AgentInbox>();
        services.AddSingleton<MessageRateLimiter>();
        services.AddSingleton<PendingMediaCache>();

        services.AddScoped<Users.AgentUsersService>();
        services.AddScoped<AgentUserDirectory>();
        services.AddScoped<ConversationStore>();
        services.AddScoped<PendingActionService>();
        services.AddScoped<AuditService>();
        services.AddScoped<PreferenceService>();
        services.AddScoped<AliasService>();
        services.AddScoped<UsageService>();
        services.AddScoped<ProactiveLog>();
        services.AddScoped<ToolExecutor>();
        services.AddScoped<LocalToolRunner>();
        services.AddScoped<AgentRunner>();
        services.AddScoped<IInvoiceIntake, InvoiceIntake>();
        services.AddScoped<AgentProcessor>();

        services.AddHostedService<AgentWorker>();
        services.AddHostedService<ProactiveWorker>();
        return services;
    }

    /// <summary>Stops the server at start-up if the agent is switched on but a required secret is missing.</summary>
    public static void ValidateAgentConfiguration(this AgentOptions options)
    {
        if (!options.Enabled) return;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ServiceKey) || options.ServiceKey.Length < 32) missing.Add("Agent:ServiceKey (at least 32 characters)");
        if (string.IsNullOrWhiteSpace(options.WhatsApp.PhoneNumberId)) missing.Add("Agent:WhatsApp:PhoneNumberId");
        if (string.IsNullOrWhiteSpace(options.WhatsApp.AccessToken)) missing.Add("Agent:WhatsApp:AccessToken");
        if (string.IsNullOrWhiteSpace(options.WhatsApp.AppSecret)) missing.Add("Agent:WhatsApp:AppSecret");
        if (string.IsNullOrWhiteSpace(options.WhatsApp.VerifyToken)) missing.Add("Agent:WhatsApp:VerifyToken");
        if (string.IsNullOrWhiteSpace(options.Model.ApiKey) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            missing.Add("Agent:Model:ApiKey or ANTHROPIC_API_KEY");

        if (missing.Count > 0)
            throw new InvalidOperationException("The WhatsApp agent is enabled but these settings are missing: " + string.Join(", ", missing));
    }
}
