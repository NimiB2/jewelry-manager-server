using JewelryManager.Api.Features.Expenses.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Expenses;

[ApiController]
[Route("expenses")]
public class ExpensesController(ExpensesService service) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public Task<ExpenseResponse> GetExpense(Guid id) => service.GetExpenseAsync(id);

    [HttpPost]
    public Task<ExpenseResponse> CreateExpense(CreateExpenseDto dto) => service.CreateExpenseAsync(dto);

    [HttpPut("{id:guid}")]
    public Task<ExpenseResponse> UpdateExpense(Guid id, UpdateExpenseDto dto) => service.UpdateExpenseAsync(id, dto);

    /// <param name="wholeSeries">For a recurring expense: delete this and all later occurrences and stop the series.</param>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteExpense(Guid id, bool wholeSeries = false)
    {
        await service.DeleteExpenseAsync(id, wholeSeries);
        return NoContent();
    }
}
