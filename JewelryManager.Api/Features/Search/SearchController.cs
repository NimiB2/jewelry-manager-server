using JewelryManager.Api.Features.Search.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Search;

[ApiController]
[Route("search")]
public class SearchController(SearchService service) : ControllerBase
{
    /// <param name="q">At least two characters; anything shorter returns nothing.</param>
    [HttpGet]
    public Task<SearchResponse> Search(string? q) => service.SearchAsync(q);
}
