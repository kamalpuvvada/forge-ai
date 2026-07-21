using Forge.Api.Contracts;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/tasks/{id:guid}")]
public sealed class CorrectionController(
    CorrectionWorkflowService workflow,
    CorrectionImplementationService implementation,
    EngineeringTaskService taskService,
    ModelCostResolver costResolver) : ControllerBase
{
    [HttpPost("failure-analysis")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> GenerateAnalysis(
        Guid id, GenerateFailureAnalysisRequest request, CancellationToken cancellationToken)
    {
        var task = await workflow.GenerateFailureAnalysisAsync(new GenerateFailureAnalysisCommand(
            request.CommandId, id, request.ExpectedRowVersion,
            request.ExpectedFailedAttemptId, request.ExpectedFailedAttemptFingerprint), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("correction-proposals/{proposalId:guid}/approve")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> ApproveProposal(
        Guid id, Guid proposalId, ApproveCorrectionProposalRequest request, CancellationToken cancellationToken)
    {
        var task = await workflow.ApproveCorrectionAsync(new ApproveCorrectionProposalCommand(
            request.CommandId, id, request.ExpectedRowVersion, proposalId, request.ProposalFingerprint,
            request.AnalysisId, request.AnalysisFingerprint, request.FailedAttemptId,
            request.FailedAttemptFingerprint, request.PreviousRevisionId, request.PreviousResultFingerprint,
            request.ApprovedRequirementFingerprint, request.ApprovedPlanFingerprint,
            request.OriginalBaseCommitSha), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("implementation-corrections")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> GenerateCorrection(
        Guid id, GenerateImplementationCorrectionRequest request, CancellationToken cancellationToken)
    {
        var task = await implementation.GenerateAsync(new GenerateCorrectionCommand(
            request.CommandId, id, request.ExpectedRowVersion, request.ProposalId,
            request.ProposalFingerprint, request.PreviousRevisionId,
            request.PreviousResultFingerprint), cancellationToken);
        var runtime = await taskService.GetImplementationRuntimeStatusAsync(task, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver, runtime));
    }

    [HttpPost("failure-analysis-attempts/{attemptId:guid}/reconcile")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> ReconcileAnalysis(
        Guid id, Guid attemptId, ReconcileFailureAnalysisRequest request, CancellationToken cancellationToken)
    {
        var task = await workflow.ReconcileFailureAnalysisAsync(new ReconcileFailureAnalysisCommand(
            request.CommandId, id, request.ExpectedRowVersion, attemptId), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }

    [HttpPost("implementation-corrections/{attemptId:guid}/reconcile")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> ReconcileCorrection(
        Guid id, Guid attemptId, ReconcileImplementationCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        var task = await workflow.ReconcileCorrectionAsync(new ReconcileCorrectionCommand(
            request.CommandId, id, request.ExpectedRowVersion, attemptId, request.ProposalId,
            request.ProposalFingerprint, request.PreviousRevisionId, request.PreviousResultFingerprint,
            request.RevisionId), cancellationToken);
        var runtime = await taskService.GetImplementationRuntimeStatusAsync(task, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver, runtime));
    }
}
