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
    /// <param name="projected">Also list the coming occurrences of recurring expenses (never counted in the totals).</param>
    [HttpGet]
    public Task<FinanceListResponse> GetFinances(DateOnly? from, DateOnly? to, string? type, string? receipt, bool projected = false) =>
        service.GetFinancesAsync(from, to, type, receipt, projected);

    [HttpGet("years")]
    public Task<List<int>> GetYears() => service.GetYearsAsync();
}
