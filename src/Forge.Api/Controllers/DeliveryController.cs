using Forge.Api.Contracts;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/tasks/{id:guid}")]
public sealed class DeliveryController(
    DeliveryService delivery,
    EngineeringTaskService tasks,
    ModelCostResolver costResolver) : ControllerBase
{
    [HttpPost("delivery-proposals")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> Prepare(
        Guid id, PrepareDeliveryRequest request, CancellationToken cancellationToken)
    {
        var task = await tasks.GetRequiredAsync(id, cancellationToken);
        var result = await delivery.PrepareProposalAsync(new PrepareDeliveryCommand(
            request.CommandId, id, request.ExpectedRowVersion, request.RevisionId, request.ResultFingerprint,
            request.VerificationPlanId, request.VerificationPlanFingerprint,
            request.ManualAttemptId, request.ManualAttemptFingerprint), task, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(result, costResolver));
    }

    [HttpPost("delivery-proposals/{proposalId:guid}/approve")]
    [RequestSizeLimit(8 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> Approve(
        Guid id, Guid proposalId, ApproveDeliveryRequest request, CancellationToken cancellationToken)
    {
        if (!request.ConfirmedByHuman)
            throw new DeliveryException("delivery_not_eligible", "Explicit human delivery approval is required.");
        var result = await delivery.ApproveAsync(new ApproveDeliveryCommand(
            request.CommandId, id, request.ExpectedRowVersion, proposalId, request.ProposalFingerprint,
            request.RevisionId, request.ResultFingerprint, request.VerificationPlanId,
            request.VerificationPlanFingerprint, request.ManualAttemptId,
            request.ManualAttemptFingerprint), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(result, costResolver));
    }

    [HttpPost("deliveries")]
    [RequestSizeLimit(4 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> Execute(
        Guid id, ExecuteDeliveryRequest request, CancellationToken cancellationToken)
    {
        var result = await delivery.ExecuteAsync(new ExecuteDeliveryCommand(
            request.CommandId, id, request.ExpectedRowVersion,
            request.ProposalId, request.ProposalFingerprint), cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(result, costResolver));
    }

    [HttpPost("delivery-attempts/{attemptId:guid}/reconcile")]
    [RequestSizeLimit(4 * 1024)]
    public async Task<ActionResult<EngineeringTaskResponse>> Reconcile(
        Guid id, Guid attemptId, ReconcileDeliveryRequest request, CancellationToken cancellationToken)
    {
        var task = await tasks.GetRequiredAsync(id, cancellationToken);
        var result = await delivery.ReconcileExistingAsync(new ReconcileDeliveryCommand(
            request.CommandId, id, request.ExpectedRowVersion, attemptId,
            request.ProposalId, request.ProposalFingerprint), task, cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(result, costResolver));
    }
}
