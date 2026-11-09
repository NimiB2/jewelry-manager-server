namespace JewelryManager.Api.Data.Entities;

/// <summary>
/// One thing the agent ever suggested or reminded about. This ledger is how "done" and "declined" are remembered:
/// the code checks it before anything is said, so a finished or refused item never comes back.
/// </summary>
public class CoachItem
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    // Stable key such as task:{id}, season:{occasion}:{year}:{step}, review:{iso-week}. Unique per business.
    public string Key { get; set; } = string.Empty;

    // task, season, growth, review, anniversary, metal, mute...
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    // suggested, accepted, done, declined, snoozed, expired, parked
    public string State { get; set; } = "suggested";

    // For tasks: 0 none, 1 reminded, 2 breakdown offered, 3 decision asked, 4 parked.
    public int Stage { get; set; }

    public Guid? TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastMentionedAt { get; set; }
    public int MentionCount { get; set; }
    public DateTime? SnoozedUntil { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>The area the business is concentrating on now (website, new jewelry, advertising, customers, admin).</summary>
public class CoachFocus
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string Area { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? FirstStep { get; set; }
    public DateTime StartedAt { get; set; }
}

/// <summary>Whether an occasion (Tu B'Av, Hanukkah...) is one she wants help preparing for.</summary>
public class CoachOccasion
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A monthly duty she defined ("send invoices to the accountant on the 5th"); becomes a task each month.</summary>
public class RecurringObligation
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DayOfMonth { get; set; }
    public bool IsActive { get; set; } = true;

    // yyyy-MM of the last task created, so one month never gets two.
    public string? LastCreatedMonth { get; set; }
}

/// <summary>Which metal and purity a material in the settings is, and the market level its price was last true at.</summary>
public class MetalMapping
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public string MaterialName { get; set; } = string.Empty;

    // gold or silver
    public string Metal { get; set; } = string.Empty;

    // e.g. 0.585 for 14K gold, 0.925 for sterling silver.
    public decimal Purity { get; set; }

    // The price in the settings when last seen; a change re-sets the baseline to today's market.
    public decimal LastSettingsPrice { get; set; }
    public decimal BaselineSpotIlsPerGram { get; set; }
    public decimal ImpliedPremium { get; set; }
    public DateTime BaselineAt { get; set; }

    // The day the index first crossed its threshold; an alert needs a second day, and is sent once per crossing.
    public DateOnly? FirstCrossedOn { get; set; }
    public DateOnly? AlertedOn { get; set; }
}

/// <summary>One accepted market reading (the raw numbers behind every alert, kept for checking).</summary>
public class MetalPriceReading
{
    public Guid Id { get; set; }
    public string Metal { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public decimal UsdPerOunce { get; set; }
    public decimal UsdIls { get; set; }
    public decimal IlsPerGram { get; set; }
    public bool Accepted { get; set; }
    public string? Note { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Every change to a task, so a wrong change can be found and restored.</summary>
public class TaskChange
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid TaskId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Source { get; set; } = "app";
    public DateTime At { get; set; }
}
