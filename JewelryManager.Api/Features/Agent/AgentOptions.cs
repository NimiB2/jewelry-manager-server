namespace JewelryManager.Api.Features.Agent;

/// <summary>
/// Settings of the WhatsApp agent (section "Agent"). Secrets arrive as environment variables
/// (Agent__ServiceKey, Agent__WhatsApp__AccessToken, ...) and are never written in code or git.
/// The agent stays completely off until Enabled is true and the keys are present.
/// </summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    public bool Enabled { get; set; }

    // Shared secret between the agent and the REST API (the agent calls the API like a signed-in user).
    public string? ServiceKey { get; set; }

    // Where the agent reaches the API; empty = this same server on its own port.
    public string? ApiBaseUrl { get; set; }

    public ModelOptions Model { get; set; } = new();
    public WhatsAppOptions WhatsApp { get; set; } = new();
    public TranscriptionOptions Transcription { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public ProactiveOptions Proactive { get; set; } = new();
}

public class ModelOptions
{
    // Cheapest model first; move a route up only if it fails the evaluation set.
    public string Name { get; set; } = "claude-haiku-5-5";
    public string Effort { get; set; } = "low";
    public int MaxOutputTokens { get; set; } = 700;

    // Falls back to the ANTHROPIC_API_KEY environment variable when empty.
    public string? ApiKey { get; set; }
}

public class WhatsAppOptions
{
    public string? PhoneNumberId { get; set; }
    public string? AccessToken { get; set; }
    public string? AppSecret { get; set; }
    public string? VerifyToken { get; set; }
    public string GraphVersion { get; set; } = "v21.0";
}

/// <summary>Any service that offers the OpenAI-style /audio/transcriptions endpoint.</summary>
public class TranscriptionOptions
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "whisper-1";
    public string Language { get; set; } = "he";
    public int MaxSeconds { get; set; } = 120;
}

public class LimitOptions
{
    public int MaxToolCallsPerMessage { get; set; } = 6;
    public int MaxInputChars { get; set; } = 1500;
    public int HistoryMessages { get; set; } = 10;
    public int HistoryMinutes { get; set; } = 60;
    public int MessagesPerMinute { get; set; } = 15;
    public int DailyTokensPerUser { get; set; } = 200_000;
    public int MonthlyTokensPerBusiness { get; set; } = 3_000_000;
    public int PendingMinutes { get; set; } = 10;
    public int UndoMinutes { get; set; } = 10;
    public int MaxPreferences { get; set; } = 20;
    public int MessageRetentionDays { get; set; } = 30;
    public int AuditRetentionMonths { get; set; } = 12;
    public int MaxMediaBytes { get; set; } = 10 * 1024 * 1024;
}

public class ProactiveOptions
{
    public int MorningHour { get; set; } = 8;
    public int StuckDays { get; set; } = 7;

    // Names of the approved WhatsApp templates (a business-started message outside the 24-hour window needs one).
    public string? MorningTemplate { get; set; }
    public string? StoreOrderTemplate { get; set; }
    public string? StuckOrderTemplate { get; set; }
    public string TemplateLanguage { get; set; } = "he";
}
