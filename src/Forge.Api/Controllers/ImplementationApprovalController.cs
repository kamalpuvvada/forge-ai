using Forge.Api.Contracts;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/tasks/{id:guid}/implementation-approval")]
public sealed class ImplementationApprovalController(
    ImplementationApprovalService approvalService,
    ModelCostResolver costResolver) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<EngineeringTaskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<EngineeringTaskResponse>> Approve(
        Guid id,
        ApproveImplementationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CommandId == Guid.Empty || request.ExpectedRevisionId == Guid.Empty)
            throw new ArgumentException("A valid implementation approval command and revision are required.");
        var task = await approvalService.ApproveAsync(
            id,
            request.CommandId,
            request.ExpectedRowVersion,
            request.ExpectedRevisionId,
            request.ExpectedResultFingerprint,
            cancellationToken);
        return Ok(EngineeringTaskResponse.FromDomain(task, costResolver));
    }
}
