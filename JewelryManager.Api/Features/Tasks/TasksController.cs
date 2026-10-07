using JewelryManager.Api.Data.Entities;
using JewelryManager.Api.Features.Tasks.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace JewelryManager.Api.Features.Tasks;

[ApiController]
[Route("tasks")]
public class TasksController(TasksService service) : ControllerBase
{
    /// <param name="status">Only tasks in this status; all of them when omitted.</param>
    /// <param name="orderId">Only tasks attached to this order.</param>
    [HttpGet]
    public Task<List<TaskResponse>> GetTasks(WorkTaskStatus? status, Guid? orderId) =>
        service.GetTasksAsync(status, orderId);

    [HttpGet("{id:guid}")]
    public Task<TaskResponse> GetTask(Guid id) => service.GetTaskAsync(id);

    [HttpPost]
    public Task<TaskResponse> CreateTask(SaveTaskDto dto) => service.CreateTaskAsync(dto);

    [HttpPut("{id:guid}")]
    public Task<TaskResponse> UpdateTask(Guid id, SaveTaskDto dto) => service.UpdateTaskAsync(id, dto);

    [HttpPatch("{id:guid}/status")]
    public Task<TaskResponse> UpdateStatus(Guid id, UpdateTaskStatusDto dto) => service.UpdateStatusAsync(id, dto.Status);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTask(Guid id)
    {
        await service.DeleteTaskAsync(id);
        return NoContent();
    }
}
