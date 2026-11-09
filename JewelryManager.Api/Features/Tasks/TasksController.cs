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

    /// <summary>Creates several tasks at once (the steps of a breakdown, a checklist).</summary>
    [HttpPost("batch")]
    public Task<List<TaskResponse>> CreateBatch(BatchTasksDto dto) => service.CreateBatchAsync(dto);

    /// <summary>Takes back a batch just created by the agent.</summary>
    [HttpPost("batch-delete")]
    public async Task<IActionResult> DeleteBatch(BatchDeleteTasksDto dto)
    {
        await service.DeleteBatchAsync(dto);
        return NoContent();
    }

    [HttpPut("{id:guid}")]
    public Task<TaskResponse> UpdateTask(Guid id, SaveTaskDto dto) => service.UpdateTaskAsync(id, dto);

    [HttpPatch("{id:guid}/status")]
    public Task<TaskResponse> UpdateStatus(Guid id, UpdateTaskStatusDto dto) => service.UpdateStatusAsync(id, dto.Status);

    /// <summary>Snooze, park, unpark or clear the date of a task.</summary>
    [HttpPatch("{id:guid}/coach")]
    public Task<TaskResponse> Coach(Guid id, TaskCoachActionDto dto) => service.CoachActionAsync(id, dto);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTask(Guid id)
    {
        await service.DeleteTaskAsync(id);
        return NoContent();
    }
}
