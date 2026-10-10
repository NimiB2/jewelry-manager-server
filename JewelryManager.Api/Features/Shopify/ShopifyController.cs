using JewelryManager.Api.Features.Shopify.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Shopify;

/// <summary>The signed-in owner's side of the store connection: look at the store's products and tie them to the catalog.</summary>
[ApiController]
[Route("shopify")]
public class ShopifyController(ShopifyImportService service) : ControllerBase
{
    [HttpGet("status")]
    public ShopifyStatusResponse GetStatus() => service.GetStatus();

    [HttpGet("products")]
    public Task<List<StoreProductPreviewDto>> GetProducts() => service.PreviewAsync();

    [HttpPost("products/link")]
    public async Task<IActionResult> Link(LinkStoreProductDto dto)
    {
        await service.LinkAsync(dto);
        return NoContent();
    }

    [HttpPost("products/create")]
    public Task<CreateFromStoreResponse> Create(CreateFromStoreDto dto) => service.CreateAsync(dto);
}
