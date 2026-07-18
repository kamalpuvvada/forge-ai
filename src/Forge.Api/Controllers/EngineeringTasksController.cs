using Forge.Api.Contracts;
using Forge.Core;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public sealed class EngineeringTasksController(EngineeringTaskService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EngineeringTaskResponse>> Create(
        CreateEngineeringTaskRequest request,
        CancellationToken cancellationToken)
    {
        var task = await service.CreateAsync(request.Repository, request.Requirement, cancellationToken);
        var response = EngineeringTaskResponse.FromDomain(task);
        return CreatedAtAction(nameof(Get), new { id = task.Id }, response);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EngineeringTaskResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.GetAsync(id, cancellationToken);
        return task is null ? NotFoundProblem(id) : Ok(EngineeringTaskResponse.FromDomain(task));
    }

    [HttpPost("{id:guid}/answers")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> Answer(
        Guid id,
        AnswerClarificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = await service.AnswerAsync(id, request.Answer, cancellationToken);
            return Ok(EngineeringTaskResponse.FromDomain(task));
        }
        catch (KeyNotFoundException) { return NotFoundProblem(id); }
        catch (WorkflowException exception) { return ConflictProblem(exception.Message); }
    }

    [HttpPost("{id:guid}/requirement-approval")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> ApproveRequirement(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = await service.ApproveRequirementAsync(id, cancellationToken);
            return Ok(EngineeringTaskResponse.FromDomain(task));
        }
        catch (KeyNotFoundException) { return NotFoundProblem(id); }
        catch (WorkflowException exception) { return ConflictProblem(exception.Message); }
    }

    private ObjectResult NotFoundProblem(Guid id) => Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Engineering task not found",
        detail: $"No engineering task exists with ID '{id}'.");

    private ObjectResult ConflictProblem(string detail) => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Invalid workflow action",
        detail: detail);
}
