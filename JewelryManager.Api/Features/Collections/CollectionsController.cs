using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Collections.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Collections;

/// <summary>HTTP layer only — one service call per action.</summary>
[ApiController]
[Route("collections")]
public class CollectionsController(CollectionsService service) : ControllerBase
{
    [HttpGet]
    public Task<List<Collection>> GetCollections() => service.GetCollectionsAsync();

    [HttpPost]
    public Task<Collection> CreateCollection(CreateCollectionDto dto) =>
        service.CreateCollectionAsync(dto);

    [HttpPatch("{id:guid}")]
    public Task<Collection> RenameCollection(Guid id, UpdateCollectionDto dto) =>
        service.RenameCollectionAsync(id, dto);

    [HttpDelete("{id:guid}")]
    public Task<Collection> DeleteCollection(Guid id) => service.DeleteCollectionAsync(id);
}
