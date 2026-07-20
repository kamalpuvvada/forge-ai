using Forge.Api.Contracts;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public sealed class EngineeringTasksController(
    EngineeringTaskService service,
    ModelCostResolver costResolver,
    IEngineeringTaskPdfExporter pdfExporter,
    IImplementationPlanPdfExporter planPdfExporter) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EngineeringTaskSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EngineeringTaskSummaryResponse>>> ListRecent(
        CancellationToken cancellationToken)
    {
        var summaries = await service.ListRecentAsync(cancellationToken);
        return Ok(summaries.Select(EngineeringTaskSummaryResponse.FromDomain).ToArray());
    }

    [HttpPost]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EngineeringTaskResponse>> Create(
        CreateEngineeringTaskRequest request,
        CancellationToken cancellationToken)
    {
        var task = await service.CreateAsync(request.Repository, request.Requirement, cancellationToken);
        var response = EngineeringTaskResponse.FromDomain(task, costResolver);
        return CreatedAtAction(nameof(Get), new { id = task.Id }, response);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EngineeringTaskResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.GetRequiredAsync(id, cancellationToken);
        var runtime = await service.GetImplementationRuntimeStatusAsync(task, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver, runtime));
    }

    [HttpGet("{id:guid}/export/pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportPdf(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.GetRequiredAsync(id, cancellationToken);
        var runtime = await service.GetImplementationReportRuntimeStatusAsync(task, cancellationToken);
        var bytes = pdfExporter.Export(task, runtime);
        return File(bytes, "application/pdf", $"forge-task-{task.Id:D}.pdf");
    }

    [HttpGet("{id:guid}/export/plan-pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExportPlanPdf(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.GetRequiredAsync(id, cancellationToken);
        var bytes = planPdfExporter.Export(task);
        return File(bytes, "application/pdf", $"forge-plan-{task.Id:D}.pdf");
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
        var task = await service.AnswerAsync(id, request.Answer, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/requirement-revision")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> RequestRevision(
        Guid id,
        RequestRequirementRevisionRequest request,
        CancellationToken cancellationToken)
    {
        var task = await service.RequestRevisionAsync(id, request.Correction, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/requirement-approval")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> ApproveRequirement(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.ApproveRequirementAsync(id, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/repository-analysis")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> AnalyzeRepository(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.AnalyzeRepositoryAsync(id, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/plan")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> CreatePlan(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.CreatePlanAsync(id, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/evidence-refresh")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<EngineeringTaskResponse>> RefreshEvidence(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.RefreshEvidenceAsync(id, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/plan-revision")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<EngineeringTaskResponse>> RequestPlanRevision(
        Guid id,
        RequestPlanRevisionRequest request,
        CancellationToken cancellationToken)
    {
        var task = await service.RequestPlanRevisionAsync(id, request.Correction, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/plan-approval")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EngineeringTaskResponse>> ApprovePlan(Guid id, CancellationToken cancellationToken)
    {
        var task = await service.ApprovePlanAsync(id, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("{id:guid}/implementation")]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<EngineeringTaskResponse>> GenerateImplementation(
        Guid id,
        CancellationToken cancellationToken)
    {
        var task = await service.GenerateImplementationAsync(id, cancellationToken);
        var runtime = await service.GetImplementationRuntimeStatusAsync(task, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver, runtime));
    }

}
