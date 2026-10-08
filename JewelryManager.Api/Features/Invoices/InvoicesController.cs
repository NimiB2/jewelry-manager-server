using JewelryManager.Api.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Invoices;

[ApiController]
public class InvoicesController(InvoicesService service, InvoiceReadingService reading) : ControllerBase
{
    /// <summary>Whether invoices can be read automatically, so the screen knows what to promise.</summary>
    [HttpGet("invoices/reader")]
    public object ReaderStatus() => new { available = reading.IsAvailable };

    /// <summary>Reads the details off an invoice (nothing is saved). Empty when no reader is connected.</summary>
    [HttpPost("invoices/read")]
    [RequestSizeLimit(InvoicesService.MaxFileBytes + 1024 * 1024)]
    public async Task<InvoiceReadResponse> Read(IFormFile file)
    {
        if (file is null) throw new BadRequestException("No file was sent");

        await using var stream = file.OpenReadStream();
        return await reading.ReadAsync(file.FileName, file.Length, stream);
    }

    [HttpPost("expenses/{id:guid}/invoice")]
    [RequestSizeLimit(InvoicesService.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Attach(Guid id, IFormFile file)
    {
        if (file is null) throw new BadRequestException("No file was sent");

        await using var stream = file.OpenReadStream();
        await service.AttachAsync(id, file.FileName, file.Length, stream);
        return NoContent();
    }

    /// <summary>The invoice file, only for a signed-in user of the same business.</summary>
    [HttpGet("expenses/{id:guid}/invoice")]
    public async Task<IActionResult> Download(Guid id)
    {
        var file = await service.OpenAsync(id);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpDelete("expenses/{id:guid}/invoice")]
    public async Task<IActionResult> Remove(Guid id)
    {
        await service.RemoveAsync(id);
        return NoContent();
    }

    /// <summary>All invoice files of the period (and a list of its expenses) as one ZIP.</summary>
    [HttpGet("invoices/export")]
    public async Task<FileContentResult> Export(DateOnly? from, DateOnly? to) =>
        File(await service.ExportZipAsync(from, to), "application/zip", "invoices.zip");
}
