using JewelryManager.Api.Features.Expenses.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Expenses;

[ApiController]
[Route("expenses")]
public class ExpensesController(ExpensesService service) : ControllerBase
{
    [HttpGet("suppliers")]
    public Task<List<string>> GetSuppliers() => service.GetSuppliersAsync();

    [HttpGet("supplier-types")]
    public Task<Dictionary<string, string>> GetSupplierTypes() => service.GetSupplierTypesAsync();

    [HttpPost("suppliers/merge")]
    public async Task<IActionResult> MergeSuppliers(MergeSuppliersDto dto)
    {
        await service.MergeSuppliersAsync(dto);
        return NoContent();
    }

    /// <summary>The expenses that have no invoice, to look over before deleting them.</summary>
    [HttpGet("without-receipt")]
    public Task<WithoutReceiptList> GetWithoutReceipt(DateOnly? from, DateOnly? to, bool includeRecurring = false) =>
        service.GetWithoutReceiptAsync(from, to, includeRecurring);

    [HttpPost("without-receipt/delete")]
    public async Task<object> DeleteWithoutReceipt(DeleteWithoutReceiptDto dto) => new { deleted = await service.DeleteWithoutReceiptAsync(dto) };

    [HttpGet("{id:guid}")]
    public Task<ExpenseResponse> GetExpense(Guid id) => service.GetExpenseAsync(id);

    [HttpPost]
    public Task<ExpenseResponse> CreateExpense(CreateExpenseDto dto) => service.CreateExpenseAsync(dto);

    [HttpPut("{id:guid}")]
    public Task<ExpenseResponse> UpdateExpense(Guid id, UpdateExpenseDto dto) => service.UpdateExpenseAsync(id, dto);

    /// <param name="scope">This (default), FromHere or After; the last two only for a recurring expense.</param>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteExpense(Guid id, ExpenseDeleteScope scope = ExpenseDeleteScope.This)
    {
        await service.DeleteExpenseAsync(id, scope);
        return NoContent();
    }
}
