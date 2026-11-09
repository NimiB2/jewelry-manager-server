namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// A person allowed to talk to the WhatsApp agent. This is the whitelist: a phone number that is not here
/// gets no answer. The business always comes from this row, never from the message or the model.
/// </summary>
public class AgentUser
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid UserId { get; set; }

    // Digits only, with the country code (what WhatsApp sends), e.g. 9725XXXXXXXX.
    public string Phone { get; set; } = string.Empty;

    // "female", "male" or "unspecified": the agent addresses each person accordingly.
    public string Gender { get; set; } = "unspecified";

    public bool IsActive { get; set; } = true;

    // The developer is told when a budget runs out.
    public bool IsDeveloper { get; set; }

    // Proactive messages are all off until the person turns them on.
    public bool MorningSummary { get; set; }
    public bool StoreOrderAlerts { get; set; }
    public bool StuckOrderAlerts { get; set; }

    // The Friday review, and the agent's own ideas and reminders (tasks, occasions, focus, customers). Off until she says yes.
    public bool WeeklyReview { get; set; }
    public bool CoachEnabled { get; set; }

    // The coach is on by default; "פחות הודעות" halves its budget.
    public bool FewerMessages { get; set; }

    // none, saturday or friday_saturday: no proactive message on her rest days.
    public string RestDays { get; set; } = "none";

    // When she last wrote, and when the agent last wrote to her on its own.
    public DateTime? LastUserMessageAt { get; set; }
    public DateTime? LastOutboundAt { get; set; }

    // Coaching messages sent since she last wrote; at 2 all coaching pauses until she writes.
    public int CoachUnanswered { get; set; }
    public DateTime? LastCoachAt { get; set; }

    // The gentle introduction: 0 not started ... 4 finished or skipped.
    public int IntroStep { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>One line of the short conversation memory. Only final texts are kept, never tool results.</summary>
public class AgentMessage
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid AgentUserId { get; set; }

    // "user" or "assistant".
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;

    // chat for ordinary talk; morning, review, alert, reminder, coach... for what the agent sent on its own.
    public string Source { get; set; } = "chat";
    public DateTime CreatedAt { get; set; }
}

/// <summary>An action the agent proposed and the person has not answered yet. Single use, short lived.</summary>
public class AgentPendingAction
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid AgentUserId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ArgsJson { get; set; } = "{}";
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    // Set once, atomically, when the action is approved, cancelled or found expired.
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}

/// <summary>A standing instruction about how to show things. Presentation only: it never changes limits or permissions.</summary>
public class AgentPreference
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    // Null = applies to the whole business.
    public Guid? AgentUserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A nickname for a name ("רוני" means "רונית כהן"), used to find the right customer or supplier faster.</summary>
public class AgentAlias
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Tokens spent by one model call, for budgets and the monthly report.</summary>
public class AgentUsage
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid AgentUserId { get; set; }
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Every tool call the agent made: who, what, with which arguments, and how to take it back.</summary>
public class AgentAuditEntry
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid AgentUserId { get; set; }
    public string Tool { get; set; } = string.Empty;
    public string ArgsJson { get; set; } = "{}";
    public bool Ok { get; set; }

    // A short note only (never the full answer): the audit log must not become a copy of the business data.
    public string? Note { get; set; }
    public string? UndoJson { get; set; }
    public DateTime? UndoneAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A WhatsApp message id already handled: Meta retries, and a retry must not act twice.</summary>
public class AgentProcessedMessage
{
    public string MessageId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

/// <summary>A proactive message already sent, so the same alert is never sent twice.</summary>
public class AgentProactiveSent
{
    public Guid Id { get; set; }
    public Guid AgentUserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
}
