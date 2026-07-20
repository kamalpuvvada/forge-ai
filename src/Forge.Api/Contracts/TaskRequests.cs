using System.ComponentModel.DataAnnotations;

namespace Forge.Api.Contracts;

public sealed record CreateEngineeringTaskRequest(
    [Required, StringLength(1000)] string Repository,
    [Required, StringLength(10000)] string Requirement);

public sealed record AnswerClarificationRequest(
    [Required, StringLength(5000)] string Answer);

public sealed record RequestRequirementRevisionRequest(
    [Required, StringLength(5000)] string Correction);

public sealed record RequestPlanRevisionRequest(
    [Required, StringLength(5000)] string Correction);

public sealed record ApproveImplementationRequest(
    Guid CommandId,
    [Range(0, long.MaxValue - 1)] long ExpectedRowVersion,
    Guid ExpectedRevisionId,
    [Required, RegularExpression("^[0-9a-f]{64}$")] string ExpectedResultFingerprint);
