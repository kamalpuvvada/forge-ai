namespace Forge.Api.Contracts;

public sealed record GenerateFailureAnalysisRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ExpectedFailedAttemptId,
    string ExpectedFailedAttemptFingerprint);

public sealed record ApproveCorrectionProposalRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    string ProposalFingerprint,
    Guid AnalysisId,
    string AnalysisFingerprint,
    Guid FailedAttemptId,
    string FailedAttemptFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string OriginalBaseCommitSha);

public sealed record GenerateImplementationCorrectionRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ProposalId,
    string ProposalFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint);

public sealed record ReconcileFailureAnalysisRequest(Guid CommandId, long ExpectedRowVersion);

public sealed record ReconcileImplementationCorrectionRequest(
    Guid CommandId,
    long ExpectedRowVersion,
    Guid ProposalId,
    string ProposalFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint,
    Guid RevisionId);
