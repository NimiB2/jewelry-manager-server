using JewelryManager.Api.Features.Finances.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Finances;

[ApiController]
[Route("finances")]
public class FinancesController(FinancesService service) : ControllerBase
{
    /// <param name="from">First date, inclusive (yyyy-MM-dd).</param>
    /// <param name="to">Last date, inclusive (yyyy-MM-dd).</param>
    /// <param name="type">all (default), income or expense.</param>
    /// <param name="receipt">all (default), missing or has — expenses only.</param>
    [HttpGet]
    public Task<FinanceListResponse> GetFinances(DateOnly? from, DateOnly? to, string? type, string? receipt) =>
        service.GetFinancesAsync(from, to, type, receipt);

    [HttpGet("years")]
    public Task<List<int>> GetYears() => service.GetYearsAsync();

    /// <summary>The same filtered rows as the list, as a CSV file that opens in Excel.</summary>
    [HttpGet("export")]
    public async Task<FileContentResult> Export(DateOnly? from, DateOnly? to, string? type, string? receipt)
    {
        var bytes = await service.ExportCsvAsync(from, to, type, receipt);
        return File(bytes, "text/csv; charset=utf-8", "finances.csv");
    }
}
