using JewelryManager.Api.Features.Products.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Products;

[ApiController]
[Route("products")]
public class ProductsController(ProductsService service) : ControllerBase
{
    [HttpGet]
    public Task<ProductsListResponse> GetProducts() => service.GetProductsAsync();

    [HttpGet("{id:guid}")]
    public Task<ProductResponse> GetProduct(Guid id) => service.GetProductAsync(id);

    [HttpPost]
    public Task<ProductResponse> CreateProduct(SaveProductDto dto) => service.CreateProductAsync(dto);

    [HttpPut("{id:guid}")]
    public Task<ProductResponse> UpdateProduct(Guid id, SaveProductDto dto) => service.UpdateProductAsync(id, dto);

    [HttpPatch("{id:guid}/site-price")]
    public Task<ProductResponse> UpdateSitePrice(Guid id, UpdateSitePriceDto dto) =>
        service.UpdateSitePriceAsync(id, dto);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteProduct(Guid id)
    {
        await service.DeleteProductAsync(id);
        return NoContent();
    }
}
