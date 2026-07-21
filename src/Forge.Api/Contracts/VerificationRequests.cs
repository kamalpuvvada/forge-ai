using Forge.Core;

namespace Forge.Api.Contracts;

public sealed record GenerateVerificationPlanRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint);

public sealed record StartVerificationAttemptRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ExpectedVerificationPlanId,
    string ExpectedVerificationPlanFingerprint,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint);

public sealed record UpdateVerificationCaseRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ExpectedVerificationPlanId,
    string ExpectedVerificationPlanFingerprint,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint,
    ManualVerificationCaseResult Result,
    string? Notes,
    string? ActualResult,
    IReadOnlyList<string>? EvidenceDescriptions,
    string? NotApplicableReason,
    VerificationFailureDetails? FailureDetails);

public sealed record CompleteVerificationAttemptRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ExpectedVerificationPlanId,
    string ExpectedVerificationPlanFingerprint,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint,
    bool ConfirmedByHuman,
    string? Summary);
