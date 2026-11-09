using System.Text.Json;
using System.Text.Json.Nodes;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent;
using JewelryManager.Api.Features.Agent.Media;
using JewelryManager.Api.Features.Agent.Mcp;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Api.Features.Agent.Services;
using JewelryManager.Api.Features.Agent.WhatsApp;
using JewelryManager.Mcp;
using JewelryManager.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JewelryManager.Tests.Agent;

/// <summary>A model that answers from a script, so no test ever calls the real service or spends a token.</summary>
public class ScriptedModel : IAgentModel
{
    private readonly Queue<ModelTurn> _script = new();

    public string ModelName => "scripted";

    public List<ModelRequest> Requests { get; } = [];

    public ScriptedModel Says(string text) => Enqueue(new TextPart(text));

    public ScriptedModel Calls(string tool, object args, string id = "call") =>
        Enqueue(new ToolCallPart(id, tool, JsonSerializer.Serialize(args)));

    private ScriptedModel Enqueue(ChatBlock block)
    {
        _script.Enqueue(new ModelTurn([block], false, false, new ModelUsage(100, 20, 0, 0)));
        return this;
    }

    public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var turn = _script.Count > 0 ? _script.Dequeue() : new ModelTurn([new TextPart("...")], false, false, new ModelUsage(1, 1, 0, 0));
        return Task.FromResult(turn);
    }
}

public class FakeToolSession : IToolSession
{
    public IReadOnlyList<AgentToolSpec> Tools { get; } = new[] { "list_orders", "advance_stage", "set_status", "add_expense" }
        .Select(n => new AgentToolSpec(n, n, JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }))).ToList();

    public List<(string Name, string Args)> Calls { get; } = [];

    public Dictionary<string, string> Answers { get; } = new()
    {
        ["advance_stage"] = ToolEnvelope.Ok(JsonNode.Parse("""{"number":1042}"""), new UndoCall("PATCH", "/orders/x/stage", new { stage = "ליטוש" })),
        ["add_expense"] = ToolEnvelope.Ok(JsonNode.Parse("""{"id":"55555555-5555-5555-5555-555555555555"}"""),
            new UndoCall("DELETE", "/expenses/55555555-5555-5555-5555-555555555555", null)),
    };

    public Task<string> CallAsync(string name, string argsJson, CancellationToken ct)
    {
        Calls.Add((name, argsJson));
        return Task.FromResult(Answers.TryGetValue(name, out var answer) ? answer : ToolEnvelope.Ok(JsonNode.Parse("""{"count":3}""")));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class FakeToolSessionFactory : IToolSessionFactory
{
    public FakeToolSession Session { get; } = new();

    public Task<IToolSession> StartAsync(Guid agentUserId, CancellationToken ct) => Task.FromResult<IToolSession>(Session);
}

public class FakeBackendApiFactory : IBackendApiFactory
{
    public FakeBackendApi Api { get; } = new();

    public IBackendApi CreateFor(Guid agentUserId) => Api;
}

public class FakeWhatsApp : IWhatsAppClient
{
    public List<(string To, string Text, IReadOnlyList<ReplyButton>? Buttons)> Sent { get; } = [];

    public Task SendTextAsync(string to, string text, CancellationToken ct) { Sent.Add((to, text, null)); return Task.CompletedTask; }

    public Task SendButtonsAsync(string to, string text, IReadOnlyList<ReplyButton> buttons, CancellationToken ct) { Sent.Add((to, text, buttons)); return Task.CompletedTask; }

    public Task SendTemplateAsync(string to, string template, string language, IReadOnlyList<string> bodyParameters, CancellationToken ct) => Task.CompletedTask;

    public Task MarkReadAsync(string messageId, CancellationToken ct) => Task.CompletedTask;

    public DownloadedMedia? Media { get; set; } = new([1, 2, 3], "audio/ogg");

    public Task<DownloadedMedia?> DownloadMediaAsync(string mediaId, int maxBytes, CancellationToken ct) => Task.FromResult(Media);
}

public class FakeTranscriber : ITranscriber
{
    public bool IsAvailable { get; set; } = true;

    public string? Text { get; set; } = "כמה הרווחתי החודש";

    public Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct) => Task.FromResult(Text);
}

public class FakeInvoiceIntake : IInvoiceIntake
{
    public Task<AgentReply> ReadAsync(AgentUser user, IncomingMessage message, CancellationToken ct) => Task.FromResult(new AgentReply("חשבונית"));

    public Task<bool> AttachAsync(Guid pendingId, Guid expenseId) => Task.FromResult(false);
}

/// <summary>Everything the agent needs, wired to fakes and a real test database, with one invented person.</summary>
public sealed class AgentHarness : IAsyncDisposable
{
    private static int _counter = 100;

    public AppDbContext Db { get; }
    public AgentUser User { get; private set; } = null!;
    public AgentOptions Options { get; } = new() { Enabled = true };
    public ScriptedModel Model { get; } = new();
    public FakeToolSessionFactory Tools { get; } = new();
    public FakeBackendApiFactory Apis { get; } = new();
    public FakeWhatsApp WhatsApp { get; } = new();
    public FakeTranscriber Transcriber { get; } = new();
    public AgentRunner Runner { get; private set; } = null!;
    public AgentProcessor Processor { get; private set; } = null!;
    public PendingActionService Pending { get; private set; } = null!;
    public PreferenceService Preferences { get; private set; } = null!;
    public AuditService Audit { get; private set; } = null!;
    public UsageService Usage { get; private set; } = null!;
    public ConversationStore Conversation { get; private set; } = null!;

    private AgentHarness(AppDbContext db) => Db = db;

    public static async Task<AgentHarness> CreateAsync(PostgresFixture fixture, Action<AgentOptions>? tweak = null, Guid? businessId = null, string gender = "female")
    {
        var harness = new AgentHarness(fixture.NewDb());
        tweak?.Invoke(harness.Options);
        await harness.SetUpAsync(businessId, gender);
        return harness;
    }

    private async Task SetUpAsync(Guid? existingBusiness, string gender)
    {
        var now = DateTime.UtcNow;
        var n = Interlocked.Increment(ref _counter);
        var businessId = existingBusiness ?? Guid.NewGuid();

        if (existingBusiness is null)
            Db.Businesses.Add(new Business { Id = businessId, Name = $"agent-test-{n}", CreatedAt = now, UpdatedAt = now });

        var person = new JewelryManager.Api.Data.Entities.User
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Email = $"agent{n}-{Guid.NewGuid():N}@test",
            Role = Role.Owner,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(person);
        await Db.SaveChangesAsync();

        User = new AgentUser
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            UserId = person.Id,
            Phone = "9725" + Random.Shared.NextInt64(10_000_000, 99_999_999),
            Gender = gender,
            CreatedAt = now,
        };
        Db.AgentUsers.Add(User);
        await Db.SaveChangesAsync();

        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        Pending = new PendingActionService(Db, opts);
        Preferences = new PreferenceService(Db, opts);
        Audit = new AuditService(Db, opts);
        Usage = new UsageService(Db, opts);
        Conversation = new ConversationStore(Db, opts);
        var aliases = new AliasService(Db);
        var directory = new AgentUserDirectory(Db);

        var executor = new ToolExecutor(Audit, NullLogger<ToolExecutor>.Instance);
        var local = new LocalToolRunner(Preferences, aliases, Audit, directory, Apis, NullLogger<LocalToolRunner>.Instance);

        Runner = new AgentRunner(Model, Tools, Conversation, Pending, Preferences, aliases, Usage, executor, local, opts, NullLogger<AgentRunner>.Instance);
        Processor = new AgentProcessor(Db, directory, WhatsApp, Transcriber, Runner, Pending, executor, Tools, local, Conversation, Usage,
            new ProactiveLog(Db), new FakeInvoiceIntake(), new MessageRateLimiter(), PostgresFixture.TenantFor(businessId), opts, NullLogger<AgentProcessor>.Instance);
    }

    public IncomingMessage Text(string text, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N"), User.Phone, IncomingKind.Text, Text: text);

    public IncomingMessage Button(string buttonId) =>
        new(Guid.NewGuid().ToString("N"), User.Phone, IncomingKind.ButtonReply, ButtonId: buttonId);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
