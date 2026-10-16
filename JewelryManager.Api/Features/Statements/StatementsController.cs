using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Expenses.Dtos;
using JewelryManager.Api.Features.Statements.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Statements;

[ApiController]
[Route("statements")]
public class StatementsController(StatementsService service) : ControllerBase
{
    /// <param name="source">Bank or Card.</param>
    [HttpPost("upload")]
    [RequestSizeLimit(StatementsService.MaxFileBytes + 1024 * 1024)]
    public async Task<UploadResultResponse> Upload(IFormFile file, [FromForm] StatementSource source)
    {
        if (file is null) throw new BadRequestException("No file was sent");

        await using var stream = file.OpenReadStream();
        return await service.UploadAsync(source, file.FileName, file.Length, stream);
    }

    [HttpGet("files")]
    public Task<List<StatementFileResponse>> GetFiles() => service.GetFilesAsync();

    [HttpDelete("files/{id:guid}")]
    public async Task<IActionResult> DeleteFile(Guid id)
    {
        await service.DeleteFileAsync(id);
        return NoContent();
    }

    /// <param name="month">Like 2026-09. Omit for every month.</param>
    [HttpGet("lines")]
    public Task<List<StatementLineResponse>> GetLines(string? month, StatementLineStatus? status) =>
        service.GetLinesAsync(month, status);

    [HttpPost("lines/{id:guid}/match")]
    public async Task<IActionResult> Match(Guid id, MatchLineDto dto)
    {
        await service.MatchAsync(id, dto.ExpenseId);
        return NoContent();
    }

    [HttpPost("lines/{id:guid}/create-expense")]
    public Task<ExpenseResponse> CreateExpense(Guid id, CreateExpenseFromLineDto dto) => service.CreateExpenseAsync(id, dto);

    [HttpPost("lines/{id:guid}/ignore")]
    public async Task<IActionResult> Ignore(Guid id)
    {
        await service.IgnoreAsync(id);
        return NoContent();
    }

    [HttpPost("lines/{id:guid}/mark-settlement")]
    public async Task<IActionResult> MarkSettlement(Guid id)
    {
        await service.MarkSettlementAsync(id);
        return NoContent();
    }

    [HttpPost("lines/{id:guid}/reset")]
    public async Task<IActionResult> Reset(Guid id)
    {
        await service.ResetAsync(id);
        return NoContent();
    }

    [HttpGet("months/{month}")]
    public Task<MonthCheckResponse> CheckMonth(string month) => service.CheckMonthAsync(month);

    /// <summary>Deletes the month's undecided and ignored lines (not the ones tied to an expense) so the file can be read again.</summary>
    [HttpPost("months/{month}/clear-open")]
    public async Task<object> ClearOpen(string month) => new { deleted = await service.ClearOpenLinesAsync(month) };

    /// <summary>Matches the month's lines to expenses that are clearly the same ones (same amount and name).</summary>
    [HttpPost("months/{month}/auto-match")]
    public async Task<object> AutoMatch(string month) => new { matched = await service.AutoMatchMonthAsync(month) };

    /// <summary>Adds an expense for every undecided line whose payee was filed before.</summary>
    [HttpPost("months/{month}/apply-known")]
    public async Task<object> ApplyKnown(string month) => new { created = await service.ApplyKnownAsync(month) };

    [HttpPost("months/{month}/close")]
    public async Task<IActionResult> CloseMonth(string month)
    {
        await service.CloseMonthAsync(month);
        return NoContent();
    }

    [HttpPost("months/{month}/reopen")]
    public async Task<IActionResult> ReopenMonth(string month)
    {
        await service.ReopenMonthAsync(month);
        return NoContent();
    }

    [HttpGet("settlement-keywords")]
    public Task<List<string>> GetKeywords() => service.GetKeywordsAsync();

    [HttpPut("settlement-keywords")]
    public Task<List<string>> SaveKeywords(SaveKeywordsDto dto) => service.SaveKeywordsAsync(dto.Keywords);
}
