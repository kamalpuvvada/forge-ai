namespace Forge.Api.Contracts;

public sealed record PrepareDeliveryRequest(
    Guid CommandId, long ExpectedRowVersion,
    Guid RevisionId, string ResultFingerprint,
    Guid VerificationPlanId, string VerificationPlanFingerprint,
    Guid ManualAttemptId, string ManualAttemptFingerprint);

public sealed record ApproveDeliveryRequest(
    Guid CommandId, long ExpectedRowVersion, string ProposalFingerprint,
    Guid RevisionId, string ResultFingerprint,
    Guid VerificationPlanId, string VerificationPlanFingerprint,
    Guid ManualAttemptId, string ManualAttemptFingerprint,
    bool ConfirmedByHuman);

public sealed record ExecuteDeliveryRequest(
    Guid CommandId, long ExpectedRowVersion, Guid ProposalId, string ProposalFingerprint);

public sealed record ReconcileDeliveryRequest(
    Guid CommandId, long ExpectedRowVersion, Guid ProposalId, string ProposalFingerprint);
