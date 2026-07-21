using Forge.Api.Contracts;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/tasks/{id:guid}")]
public sealed class VerificationController(
    VerificationWorkflowService workflow,
    ModelCostResolver costResolver,
    IEngineeringTaskRepository repository,
    IVerificationPlanPdfExporter pdfExporter) : ControllerBase
{
    [HttpPost("verification-plans")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> GeneratePlan(
        Guid id, GenerateVerificationPlanRequest request, CancellationToken cancellationToken)
    {
        var task = await workflow.GeneratePlanAsync(new VerificationPlanGenerationCommand(
            request.CommandId, id, request.ExpectedRowVersion,
            request.ExpectedImplementationRevisionId, request.ExpectedImplementationResultFingerprint),
            cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("verification-attempts")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> StartAttempt(
        Guid id, StartVerificationAttemptRequest request, CancellationToken cancellationToken)
    {
        var task = await workflow.StartAttemptAsync(new StartManualVerificationCommand(
            request.CommandId, id, request.ExpectedRowVersion, request.ExpectedVerificationPlanId,
            request.ExpectedVerificationPlanFingerprint, request.ExpectedImplementationRevisionId,
            request.ExpectedImplementationResultFingerprint), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPut("verification-attempts/{attemptId:guid}/cases/{caseId:guid}")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> UpdateCase(
        Guid id, Guid attemptId, Guid caseId, UpdateVerificationCaseRequest request,
        CancellationToken cancellationToken)
    {
        var task = await workflow.UpdateCaseAsync(new UpdateManualVerificationCaseCommand(
            request.CommandId, id, attemptId, caseId, request.ExpectedRowVersion,
            request.ExpectedVerificationPlanId, request.ExpectedVerificationPlanFingerprint,
            request.ExpectedImplementationRevisionId, request.ExpectedImplementationResultFingerprint,
            request.Result, request.Notes, request.ActualResult, request.EvidenceDescriptions ?? [],
            request.NotApplicableReason, request.FailureDetails), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("verification-attempts/{attemptId:guid}/complete-passed")]
    [RequestSizeLimit(8 * 1024)]
    public Task<ActionResult<EngineeringTaskResponse>> CompletePassed(
        Guid id, Guid attemptId, CompleteVerificationAttemptRequest request,
        CancellationToken cancellationToken) => Complete(id, attemptId, request, true, cancellationToken);

    [HttpPost("verification-attempts/{attemptId:guid}/complete-failed")]
    [RequestSizeLimit(8 * 1024)]
    public Task<ActionResult<EngineeringTaskResponse>> CompleteFailed(
        Guid id, Guid attemptId, CompleteVerificationAttemptRequest request,
        CancellationToken cancellationToken) => Complete(id, attemptId, request, false, cancellationToken);

    private async Task<ActionResult<EngineeringTaskResponse>> Complete(
        Guid id, Guid attemptId, CompleteVerificationAttemptRequest request, bool passed,
        CancellationToken cancellationToken)
    {
        var task = await workflow.CompleteAttemptAsync(new CompleteManualVerificationCommand(
            request.CommandId, id, attemptId, request.ExpectedRowVersion,
            request.ExpectedVerificationPlanId, request.ExpectedVerificationPlanFingerprint,
            request.ExpectedImplementationRevisionId, request.ExpectedImplementationResultFingerprint,
            request.ConfirmedByHuman, request.Summary, passed), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpGet("verification-plans/{planId:guid}/export/pdf")]
    [Produces("application/pdf")]
    public async Task<IActionResult> ExportPlan(Guid id, Guid planId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(id, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        return File(pdfExporter.Export(task, planId), "application/pdf",
            $"forge-verification-plan-{planId:D}.pdf");
    }
}
