using System.ComponentModel.DataAnnotations;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Features.Statements.Dtos;

/// <summary>Body of POST /statements/lines/{id}/match.</summary>
public record MatchLineDto([Required] Guid ExpenseId);

/// <summary>Body of POST /statements/lines/{id}/create-expense. Empty fields take the line's own values.</summary>
public record CreateExpenseFromLineDto(
    [MaxLength(300)] string? Description = null,
    [MaxLength(100)] string? TypeName = null,
    [MaxLength(100)] string? GroupName = null,
    [MaxLength(100)] string? Supplier = null);

/// <summary>Body of PUT /statements/settlement-keywords.</summary>
public record SaveKeywordsDto([Required] List<string> Keywords);

public record UploadResultResponse(
    Guid FileId,
    int Added,
    int AlreadyKnown,
    int CardSettlements,
    int AutoIgnored,
    int Pending);

public record StatementFileResponse(
    Guid Id, StatementSource Source, string FileName, DateOnly? PeriodFrom, DateOnly? PeriodTo,
    decimal TotalOut, int LineCount, int PendingCount, DateTime UploadedAt);

/// <summary>An expense she entered by hand that might be this line.</summary>
public record MatchSuggestion(Guid ExpenseId, DateOnly Date, string Description, string? Supplier, decimal Amount, bool Strong);

public record StatementLineResponse(
    Guid Id,
    StatementSource Source,
    DateOnly Date,
    decimal Amount,
    string Description,
    string? Reference,
    StatementLineStatus Status,
    Guid? ExpenseId,
    string? ExpenseDescription,
    bool CoveredByCardStatement,
    List<MatchSuggestion> Suggestions);

public record UnlinkedExpenseResponse(Guid Id, DateOnly Date, string Description, string? Supplier, decimal Amount);

public record UncoveredSettlementResponse(Guid LineId, DateOnly Date, string Description, decimal Amount);

/// <summary>The state of one month: what still blocks closing it, and what deserves a second look.</summary>
public record MonthCheckResponse(
    string Month,
    bool IsClosed,
    int PendingCount,
    List<UncoveredSettlementResponse> SettlementsWithoutCardStatement,
    List<UnlinkedExpenseResponse> ExpensesNotInStatements,
    bool CanClose);
