using JewelryManager.Api.Features.RiseUp.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.RiseUp;

[ApiController]
[Route("riseup")]
public class RiseUpController(RiseUpSyncService service) : ControllerBase
{
    /// <param name="month">Like 2026-09. Pulls that cashflow month from RiseUp into the statement checklist.</param>
    [HttpPost("sync")]
    public Task<RiseUpSyncResponse> Sync([FromQuery] string month) => service.SyncMonthAsync(month);
}
