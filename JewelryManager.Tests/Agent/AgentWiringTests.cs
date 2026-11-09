using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Proactive;
using JewelryManager.Api.Features.Agent.Users;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Finances;
using JewelryManager.Api.Features.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JewelryManager.Tests.Agent;

/// <summary>
/// Builds the agent's part of the container the way Program.cs does and resolves everything in a scope.
/// A missing or circular registration would otherwise only show on the first real WhatsApp message.
/// Needs no database: nothing connects.
/// </summary>
public class AgentWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUserAccessor>();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql("Host=localhost;Database=wiring-test"));

        // What the invoice flow needs from the rest of the application.
        services.AddScoped<ExpensesService>();
        services.AddScoped<FinancesService>();
        services.AddScoped<InvoicesService>();
        services.AddScoped<InvoiceReadingService>();
        services.AddSingleton<IInvoiceReader, NullInvoiceReader>();
        services.AddSingleton<IInvoiceStorage>(new LocalInvoiceStorage(Path.Combine(Path.GetTempPath(), "wiring-test")));

        services.AddWhatsAppAgent(config);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void Every_agent_service_can_be_resolved_inside_a_scope()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        foreach (var type in new[]
        {
            typeof(AgentProcessor), typeof(AgentRunner), typeof(IInvoiceIntake), typeof(AgentUsersService),
            typeof(LocalToolRunner), typeof(ToolExecutor),
        })
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(type));

        Assert.NotNull(provider.GetRequiredService<IAgentModel>());
        Assert.NotNull(provider.GetRequiredService<IToolSessionFactory>());
    }

    [Fact]
    public void The_background_workers_are_registered()
    {
        using var provider = Build();
        var hosted = provider.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        Assert.Contains(typeof(AgentWorker), hosted);
        Assert.Contains(typeof(ProactiveWorker), hosted);
    }

    [Fact]
    public void The_agent_is_off_by_default_and_needs_every_secret_once_switched_on()
    {
        new AgentOptions().ValidateAgentConfiguration();

        var enabledWithoutSecrets = new AgentOptions { Enabled = true };
        var error = Assert.Throws<InvalidOperationException>(enabledWithoutSecrets.ValidateAgentConfiguration);
        Assert.Contains("Agent:ServiceKey", error.Message);
        Assert.Contains("Agent:WhatsApp:AppSecret", error.Message);
    }

    [Fact]
    public void A_short_service_key_is_refused()
    {
        var options = new AgentOptions
        {
            Enabled = true,
            ServiceKey = "too-short",
            WhatsApp = { PhoneNumberId = "1", AccessToken = "t", AppSecret = "s", VerifyToken = "v" },
            Model = { ApiKey = "k" },
        };

        Assert.Contains("ServiceKey", Assert.Throws<InvalidOperationException>(options.ValidateAgentConfiguration).Message);
    }

    [Theory]
    [InlineData("0525550000", false)]
    [InlineData("+972 52-555-0000", true)]
    [InlineData("00972525550000", true)]
    [InlineData("972525550000", true)]
    public void Phone_numbers_are_stored_as_digits_with_the_country_code(string typed, bool valid)
    {
        if (valid)
            Assert.Equal("972525550000", AgentUsersService.NormalizePhone(typed));
        else
            Assert.Throws<JewelryManager.Api.Common.Exceptions.BadRequestException>(() => AgentUsersService.NormalizePhone(typed));
    }
}
