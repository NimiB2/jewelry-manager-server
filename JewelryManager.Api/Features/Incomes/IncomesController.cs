using JewelryManager.Api.Features.Incomes.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Incomes;

[ApiController]
[Route("incomes")]
public class IncomesController(IncomesService service) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public Task<IncomeResponse> GetIncome(Guid id) => service.GetIncomeAsync(id);

    [HttpPost]
    public Task<IncomeResponse> CreateIncome(SaveIncomeDto dto) => service.CreateIncomeAsync(dto);

    [HttpPut("{id:guid}")]
    public Task<IncomeResponse> UpdateIncome(Guid id, SaveIncomeDto dto) => service.UpdateIncomeAsync(id, dto);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteIncome(Guid id)
    {
        await service.DeleteIncomeAsync(id);
        return NoContent();
    }
}
