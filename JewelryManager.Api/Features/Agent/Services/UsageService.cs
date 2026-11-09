using JewelryManager.Api.Data;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Agent.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Features.Agent.Services;

public enum BudgetState
{
    Ok,
    NearLimit,
    Exhausted,
}

public record BudgetStatus(BudgetState State, string? Message)
{
    public static BudgetStatus Fine { get; } = new(BudgetState.Ok, null);
}

/// <summary>Counts the tokens each call spends and stops the agent politely when a daily or monthly budget runs out.</summary>
public class UsageService(AppDbContext db, IOptions<AgentOptions> options)
{
    public async Task RecordAsync(AgentUser user, string model, ModelUsage usage, CancellationToken ct = default)
    {
        db.AgentUsages.Add(new AgentUsage
        {
            Id = Guid.NewGuid(),
            BusinessId = user.BusinessId,
            AgentUserId = user.Id,
            Model = model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<BudgetStatus> CheckAsync(AgentUser user, CancellationToken ct = default)
    {
        var limits = options.Value.Limits;

        var today = await SumAsync(db.AgentUsages.Where(u => u.BusinessId == user.BusinessId && u.AgentUserId == user.Id
                                                             && u.CreatedAt >= AgentClock.StartOfTodayUtc()), ct);
        var month = await SumAsync(db.AgentUsages.Where(u => u.BusinessId == user.BusinessId
                                                             && u.CreatedAt >= AgentClock.StartOfMonthUtc()), ct);

        return Evaluate(today, limits.DailyTokensPerUser, month, limits.MonthlyTokensPerBusiness);
    }

    public static BudgetStatus Evaluate(long today, int dailyLimit, long month, int monthlyLimit)
    {
        if (today >= dailyLimit)
            return new BudgetStatus(BudgetState.Exhausted, "הגעתי למכסה היומית של השימוש. אפשר להמשיך מחר, או לפנות למפתח.");
        if (month >= monthlyLimit)
            return new BudgetStatus(BudgetState.Exhausted, "הגענו למכסה החודשית של השימוש. צריך לפנות למפתח.");

        if (today >= dailyLimit * 0.8 || month >= monthlyLimit * 0.8)
            return new BudgetStatus(BudgetState.NearLimit, "שימו לב: אני מתקרבת למכסת השימוש.");

        return BudgetStatus.Fine;
    }

    private static async Task<long> SumAsync(IQueryable<AgentUsage> query, CancellationToken ct) =>
        await query.SumAsync(u => (long?)(u.InputTokens + u.OutputTokens + u.CacheWriteTokens), ct) ?? 0;
}
