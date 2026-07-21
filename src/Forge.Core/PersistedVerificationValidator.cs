using System.Diagnostics.CodeAnalysis;

namespace Forge.Core;

public static class PersistedVerificationValidator
{
    public static void Validate(EngineeringTask task, VerificationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateCollections(task);
        ValidateFormatBoundary(task);
        var currentFormat = task.VerificationDataFormatVersion == VerificationDataFormatVersions.Current;
        ValidateGlobalModelCalls(task.ModelCalls, currentFormat);
        var modelCallsById = task.ModelCalls.ToDictionary(call => call.Id);
        if (task.VerificationPlans.Count > limits.MaximumPlansPerTask ||
            task.ManualVerificationAttempts.Count > limits.MaximumAttemptsPerTask)
            Corrupt();
        if (!task.VerificationPlans.Select(plan => plan.PlanNumber)
                .SequenceEqual(Enumerable.Range(1, task.VerificationPlans.Count)) ||
            task.VerificationPlans.Select(plan => plan.PlanId).Distinct().Count() != task.VerificationPlans.Count)
            Corrupt();
        if (!task.ManualVerificationAttempts.Select(attempt => attempt.AttemptNumber)
                .SequenceEqual(Enumerable.Range(1, task.ManualVerificationAttempts.Count)) ||
            task.ManualVerificationAttempts.Select(attempt => attempt.AttemptId).Distinct().Count() != task.ManualVerificationAttempts.Count)
            Corrupt();
        if (task.VerificationPlanGenerationAttempts.Select(attempt => attempt.CommandId).Distinct().Count() !=
            task.VerificationPlanGenerationAttempts.Count) Corrupt();
        foreach (var generation in task.VerificationPlanGenerationAttempts)
        {
            var calls = generation.ModelCallIds.Select(id => modelCallsById.GetValueOrDefault(id)).ToArray();
            var explicitResponseCalls = calls.Count(call => call?.VerificationDispatchDisposition ==
                VerificationCallDispatchDisposition.ResponseReceived && call.ProviderHttpStatusCode is >= 100 and <= 599 &&
                generation.ProviderResponses.All(response => response.LogicalCallId != call.Id));
            if (generation.CommandId == Guid.Empty || generation.TaskId != task.Id ||
                generation.ExpectedImplementationRevisionId == Guid.Empty || generation.StartedAt.Offset != TimeSpan.Zero ||
                generation.LeaseExpiresAt.Offset != TimeSpan.Zero || generation.LeaseExpiresAt <= generation.StartedAt ||
                generation.LeaseExpiresAt - generation.StartedAt > TimeSpan.FromMinutes(10) ||
                generation.CompletedAt is { } completed && completed.Offset != TimeSpan.Zero ||
                generation.LogicalCallCount is < 0 or > 2 || generation.PhysicalRequestCount is < 0 or > 2 ||
                generation.PossiblyDispatchedRequestCount is < 0 or > 2 ||
                generation.PhysicalRequestCount + generation.PossiblyDispatchedRequestCount > generation.LogicalCallCount ||
                generation.LogicalCalls is null || generation.LogicalCalls.Count != generation.LogicalCallCount ||
                generation.LogicalCalls.Select(call => call.LogicalCallId).Distinct().Count() !=
                    generation.LogicalCalls.Count || generation.LogicalCalls.Any(call => call.LogicalCallId == Guid.Empty ||
                    call.StartedAt == default || call.StartedAt.Offset != TimeSpan.Zero ||
                    call.StartedAt < generation.StartedAt || call.StartedAt > generation.LeaseExpiresAt) ||
                generation.ModelCallIds.Distinct().Count() != generation.ModelCallIds.Count ||
                generation.ModelCallIds.Count > generation.LogicalCallCount || calls.Any(call => call is null ||
                    call.Stage != ModelCallStage.VerificationPlanning || call.VerificationDispatchDisposition is null) ||
                generation.ProviderResponses.Select(response => response.LogicalCallId).Distinct().Count() !=
                    generation.ProviderResponses.Count || generation.ProviderResponses.Count + explicitResponseCalls !=
                    generation.PhysicalRequestCount) Corrupt();
            if (generation.ProviderResponses.Any(response => generation.LogicalCalls.All(call =>
                    call.LogicalCallId != response.LogicalCallId))) Corrupt();
            foreach (var response in generation.ProviderResponses)
            {
                var classifiedUsage = VerificationUsage.Classify(response.InputTokens, response.CachedInputTokens,
                    response.OutputTokens, response.ReasoningTokens);
                if (response.LogicalCallId == Guid.Empty || response.StartedAt == default ||
                    response.StartedAt.Offset != TimeSpan.Zero || response.ReceivedAt.Offset != TimeSpan.Zero ||
                    response.StartedAt < generation.StartedAt || response.ReceivedAt < response.StartedAt ||
                    !Enum.IsDefined(response.Status) ||
                    response.DispatchDisposition != VerificationCallDispatchDisposition.ResponseReceived ||
                    response.HttpStatusCode is < 200 or >= 300 ||
                    currentFormat && response.FormatVersion != VerificationDataFormatVersions.Current ||
                    !currentFormat && response.FormatVersion is not null ||
                    currentFormat && response.UsageAvailability is null ||
                    response.UsageAvailability is { } storedAvailability &&
                        (!Enum.IsDefined(storedAvailability) || storedAvailability != classifiedUsage) ||
                    !VerificationUsage.IsInternallyConsistent(response.InputTokens, response.CachedInputTokens,
                        response.OutputTokens, response.ReasoningTokens) ||
                    currentFormat && response.UsageAvailable is null ||
                    !currentFormat && response.UsageAvailable == true &&
                        classifiedUsage == VerificationUsageAvailability.Unavailable ||
                    response.UsageAvailability is not null &&
                        (response.UsageAvailable is null || !VerificationUsage.LegacyBooleanMatches(
                            response.UsageAvailable.Value, response.EffectiveUsageAvailability)) ||
                    !SafeIdentifier(response.ProviderResponseId) || !SafeIdentifier(response.ProviderRequestId) ||
                    response.IncompleteReason is { Length: > 80 }) Corrupt();
                if (currentFormat && response.TelemetryFingerprint is null) Corrupt();
                if (!currentFormat && (response.UsageAvailability is not null ||
                    response.TelemetryFingerprint is not null)) Corrupt();
                if (response.TelemetryFingerprint is { } responseFingerprint &&
                    (!IsLowerSha256(responseFingerprint) || !string.Equals(responseFingerprint,
                        VerificationFingerprint.ComputeProviderResponse(task.Id, generation.CommandId,
                            response with { TelemetryFingerprint = null }), StringComparison.Ordinal))) Corrupt();
                var projectedCall = calls.SingleOrDefault(call => call?.Id == response.LogicalCallId);
                if (projectedCall is not null && (projectedCall.StartedAt != response.StartedAt ||
                    projectedCall.CompletedAt < response.ReceivedAt ||
                    projectedCall.VerificationDispatchDisposition != response.DispatchDisposition ||
                    projectedCall.ProviderHttpStatusCode != response.HttpStatusCode ||
                    projectedCall.ProviderResponseId != response.ProviderResponseId ||
                    projectedCall.ProviderRequestId != response.ProviderRequestId ||
                    projectedCall.InputTokens != response.InputTokens ||
                    projectedCall.CachedInputTokens != response.CachedInputTokens ||
                    projectedCall.OutputTokens != response.OutputTokens ||
                    projectedCall.ReasoningTokens != response.ReasoningTokens ||
                    projectedCall.ProviderUsageAvailability is { } modelAvailability &&
                        modelAvailability != response.EffectiveUsageAvailability ||
                    projectedCall.ProviderUsageAvailable is { } modelLegacyUsage &&
                        !VerificationUsage.LegacyBooleanMatches(modelLegacyUsage,
                            response.EffectiveUsageAvailability))) Corrupt();
            }
            var active = generation.Status is VerificationGenerationAttemptStatus.Prepared or
                VerificationGenerationAttemptStatus.DispatchMayHaveStarted or VerificationGenerationAttemptStatus.ResponseReceived;
            var terminalFailure = generation.Status is VerificationGenerationAttemptStatus.FailedBeforeDispatch or
                VerificationGenerationAttemptStatus.RetryableProviderResponse or
                VerificationGenerationAttemptStatus.AmbiguousAfterDispatch or
                VerificationGenerationAttemptStatus.InterruptedBeforeDispatch;
            if (active && generation.CompletedAt is not null || terminalFailure &&
                (generation.CompletedAt is null || string.IsNullOrWhiteSpace(generation.FailureCategory) ||
                 string.IsNullOrWhiteSpace(generation.FailureMessage)) ||
                generation.Status == VerificationGenerationAttemptStatus.Completed &&
                (generation.CompletedAt is null || generation.ResultPlanId is null) ||
                generation.Status == VerificationGenerationAttemptStatus.Prepared &&
                (generation.LogicalCallCount != 0 || generation.PhysicalRequestCount != 0 ||
                 generation.PossiblyDispatchedRequestCount != 0 || generation.LastLogicalCallId is not null) ||
                (generation.Status is VerificationGenerationAttemptStatus.DispatchMayHaveStarted or
                    VerificationGenerationAttemptStatus.ResponseReceived or
                    VerificationGenerationAttemptStatus.RetryableProviderResponse or
                    VerificationGenerationAttemptStatus.AmbiguousAfterDispatch) &&
                (generation.LogicalCallCount < 1 || generation.LastLogicalCallId is null) ||
                generation.Status == VerificationGenerationAttemptStatus.ResponseReceived &&
                generation.ProviderResponses.All(response => response.LogicalCallId != generation.LastLogicalCallId)) Corrupt();
        }
        if (task.VerificationPlanGenerationAttempts.Count(attempt => attempt.Status is
                VerificationGenerationAttemptStatus.Prepared or VerificationGenerationAttemptStatus.DispatchMayHaveStarted or
                VerificationGenerationAttemptStatus.ResponseReceived) > 1) Corrupt();

        var approved = task.ApprovedImplementationRevisionId is { } approvedId
            ? task.ImplementationRevisions.SingleOrDefault(revision => revision.RevisionId == approvedId)
            : null;
        foreach (var plan in task.VerificationPlans)
        {
            if (approved?.Result is null || approved.ResultFingerprint is null ||
                plan.ImplementationRevisionId != approved.RevisionId ||
                !string.Equals(plan.ImplementationResultFingerprint, approved.ResultFingerprint, StringComparison.Ordinal) ||
                !string.Equals(plan.ApprovedRequirementFingerprint, VerificationFingerprint.ComputeApprovedRequirement(task), StringComparison.Ordinal) ||
                 task.ImplementationPlan is null || !string.Equals(plan.ApprovedPlanFingerprint,
                    ImplementationReviewFingerprint.ComputePlan(task.ImplementationPlan), StringComparison.Ordinal)) Corrupt();
            if (plan.ModelCallIds.Distinct().Count() != plan.ModelCallIds.Count || plan.ModelCallIds.Any(id =>
                    !modelCallsById.TryGetValue(id, out var call) || call.Stage != ModelCallStage.VerificationPlanning))
                Corrupt();
            var commands = task.ImplementationPlan.ProposedValidationCommands.Select((command, index) =>
                new ApprovedValidationCommand($"V{index + 1}", command)).ToArray();
            var provisional = new VerificationPlanContext(
                task.Id, task.RequirementSummary ?? string.Empty, task.ImplementationPlan, approved.RevisionId,
                approved.ResultFingerprint, approved.Result, task.EvidenceItems, commands,
                plan.ApprovedRequirementFingerprint, plan.ApprovedPlanFingerprint, plan.GenerationContextFingerprint,
                plan.GeneratedAt, task.EvidenceFilesInspected, task.EvidenceFilesSelected);
            if (!string.Equals(plan.GenerationContextFingerprint, VerificationFingerprint.ComputeContext(provisional with
                { ContextFingerprint = string.Empty }), StringComparison.Ordinal)) Corrupt();
            try { VerificationValidator.ValidatePersistedPlan(task.Id, plan, provisional, limits); }
            catch (VerificationException) { Corrupt(); }
        }

        var currentPlan = task.CurrentVerificationPlanId is { } planId
            ? task.VerificationPlans.SingleOrDefault(plan => plan.PlanId == planId)
            : null;
        if ((task.CurrentVerificationPlanId is null) != (currentPlan is null)) Corrupt();
        if (currentPlan is not null && currentPlan.Status is not (VerificationPlanStatus.Current or VerificationPlanStatus.Completed)) Corrupt();

        var globalResultRevisionIds = new HashSet<Guid>();
        foreach (var attempt in task.ManualVerificationAttempts)
        {
            var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == attempt.VerificationPlanId);
            if (plan is null || attempt.AttemptId == Guid.Empty || attempt.StartedByCommandId == Guid.Empty ||
                attempt.ImplementationRevisionId != plan.ImplementationRevisionId ||
                !string.Equals(attempt.VerificationPlanFingerprint, plan.PlanFingerprint, StringComparison.Ordinal) ||
                !string.Equals(attempt.ImplementationResultFingerprint, plan.ImplementationResultFingerprint, StringComparison.Ordinal) ||
                attempt.StartedAt.Offset != TimeSpan.Zero ||
                attempt.CompletedAt is { } completedAt && completedAt.Offset != TimeSpan.Zero) Corrupt();
            if (task.ManualVerificationAttempts.Count(item => item.VerificationPlanId == plan.PlanId) > limits.MaximumAttemptsPerPlan) Corrupt();
            var ids = new HashSet<Guid>();
            foreach (var result in attempt.ResultRevisions)
            {
                if (result.ResultRevisionId == Guid.Empty || !ids.Add(result.ResultRevisionId) ||
                    !globalResultRevisionIds.Add(result.ResultRevisionId) ||
                    result.AttemptId != attempt.AttemptId || !plan.TestCases.Any(test => test.TestCaseId == result.TestCaseId) ||
                    result.RecordedAt.Offset != TimeSpan.Zero || result.UpdatedByCommandId == Guid.Empty ||
                    !Enum.IsDefined(result.Result)) Corrupt();
                var ordered = attempt.ResultRevisions.Where(item => item.TestCaseId == result.TestCaseId)
                    .OrderBy(item => item.RevisionNumber).ToArray();
                if (!ordered.Select(item => item.RevisionNumber).SequenceEqual(Enumerable.Range(1, ordered.Length)) ||
                    result.RevisionNumber > limits.MaximumResultRevisionsPerCase ||
                    ordered[0].SupersedesResultRevisionId is not null ||
                    ordered.Skip(1).Where((item, index) => item.SupersedesResultRevisionId !=
                        ordered[index].ResultRevisionId).Any()) Corrupt();
                try { VerificationValidator.ValidatePersistedCaseResult(result, limits); }
                catch (VerificationException) { Corrupt(); }
            }
            if (attempt.Status == ManualVerificationAttemptStatus.InProgress)
            {
                if (attempt.CompletedAt is not null || attempt.AttemptFingerprint is not null ||
                    attempt.CompletedByCommandId is not null || attempt.CompletionConfirmation is not null ||
                    attempt.PassedAt is not null || attempt.FailedAt is not null) Corrupt();
            }
            else
            {
                if (attempt.CompletedAt is null || attempt.CompletedByCommandId is null ||
                    attempt.CompletionConfirmation != true || attempt.AttemptFingerprint is null ||
                    !string.Equals(attempt.AttemptFingerprint,
                        VerificationFingerprint.ComputeAttempt(task.Id, attempt with { AttemptFingerprint = null }),
                        StringComparison.Ordinal)) Corrupt();
                if (attempt.Status == ManualVerificationAttemptStatus.CompletedPassed &&
                    (attempt.PassedAt is null || attempt.FailedAt is not null) ||
                    attempt.Status == ManualVerificationAttemptStatus.CompletedFailed &&
                    (attempt.FailedAt is null || attempt.PassedAt is not null)) Corrupt();
            }
        }

        var currentAttempt = task.CurrentVerificationAttemptId is { } attemptId
            ? task.ManualVerificationAttempts.SingleOrDefault(attempt => attempt.AttemptId == attemptId)
            : null;
        if ((task.CurrentVerificationAttemptId is null) != (currentAttempt is null)) Corrupt();
        if (task.ManualVerificationAttempts.Count(attempt => attempt.Status == ManualVerificationAttemptStatus.InProgress) > 1) Corrupt();

        switch (task.Status)
        {
            case WorkflowStatus.ImplementationApproved:
                if (task.VerificationPlans.Count > 0 || task.CurrentVerificationPlanId is not null ||
                    task.CurrentVerificationAttemptId is not null) Corrupt();
                break;
            case WorkflowStatus.VerificationPlanning:
                if (task.CurrentVerificationAttemptId is not null ||
                    task.VerificationPlanGenerationAttempts.Count == 0) Corrupt();
                break;
            case WorkflowStatus.AwaitingManualVerification:
                if (currentPlan is not { Status: VerificationPlanStatus.Current } ||
                    currentAttempt is not null && currentAttempt.Status != ManualVerificationAttemptStatus.InProgress) Corrupt();
                break;
            case WorkflowStatus.ReadyForDelivery:
                if (currentPlan is not { Status: VerificationPlanStatus.Completed } ||
                    currentAttempt?.Status != ManualVerificationAttemptStatus.CompletedPassed) Corrupt();
                break;
            case WorkflowStatus.ManualVerificationFailed:
                if (currentPlan is not { Status: VerificationPlanStatus.Completed } ||
                    currentAttempt?.Status != ManualVerificationAttemptStatus.CompletedFailed) Corrupt();
                break;
            default:
                if (task.VerificationPlans.Count > 0 || task.VerificationPlanGenerationAttempts.Count > 0 ||
                    task.ManualVerificationAttempts.Count > 0 || task.CurrentVerificationPlanId is not null ||
                    task.CurrentVerificationAttemptId is not null) Corrupt();
                break;
        }
    }

    public static void ValidateFormatBoundary(
        EngineeringTask task,
        bool hasPersistedVerificationRows = false,
        bool hasVerificationCommandBindings = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateFormatBoundary(
            task.VerificationDataFormatVersion,
            task.Status,
            task.CurrentVerificationPlanId is not null,
            task.CurrentVerificationAttemptId is not null,
            task.ModelCalls?.Any(call => call?.Stage == ModelCallStage.VerificationPlanning) == true,
            hasPersistedVerificationRows || task.VerificationPlans?.Count > 0 ||
            task.VerificationPlanGenerationAttempts?.Count > 0 || task.ManualVerificationAttempts?.Count > 0,
            hasVerificationCommandBindings);
    }

    public static void ValidateFormatBoundary(
        int formatVersion,
        WorkflowStatus status,
        bool hasCurrentPlanPointer,
        bool hasCurrentAttemptPointer,
        bool hasVerificationModelCalls,
        bool hasPersistedVerificationRows,
        bool hasVerificationCommandBindings)
    {
        if (formatVersion is not (VerificationDataFormatVersions.Legacy or
                VerificationDataFormatVersions.Current)) Corrupt();
        var verificationStatus = status is WorkflowStatus.VerificationPlanning or
            WorkflowStatus.AwaitingManualVerification or WorkflowStatus.ManualVerificationFailed or
            WorkflowStatus.ReadyForDelivery;
        var hasArtifacts = hasCurrentPlanPointer || hasCurrentAttemptPointer || hasVerificationModelCalls ||
            hasPersistedVerificationRows || hasVerificationCommandBindings || verificationStatus;
        if (formatVersion == VerificationDataFormatVersions.Legacy && hasArtifacts ||
            formatVersion == VerificationDataFormatVersions.Current && !hasArtifacts) Corrupt();
    }

    [DoesNotReturn]
    private static void Corrupt() => throw new TaskDataCorruptException(
        "Stored verification data is invalid or incomplete. The task cannot be resumed automatically.");

    private static bool SafeIdentifier(string? value) => value is null || value.Length is > 0 and <= 160 &&
        value.All(character => character is >= '!' and <= '~' && character is not '/' and not '\\') &&
        !SensitiveContentDetector.ContainsSensitiveValue(value);

    private static bool IsLowerSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateGlobalModelCalls(IReadOnlyList<ModelCallRecord> calls, bool currentFormat)
    {
        if (calls.Any(call => call is null) || calls.Any(call => call.Id == Guid.Empty) ||
            calls.Select(call => call.Id).Distinct().Count() != calls.Count) Corrupt();
        foreach (var call in calls.Where(call => call.Stage == ModelCallStage.VerificationPlanning))
        {
            var classified = VerificationUsage.Classify(call.InputTokens, call.CachedInputTokens,
                call.OutputTokens, call.ReasoningTokens);
            if (currentFormat && (call.ProviderUsageAvailability is null || call.ProviderUsageAvailable is null) ||
                !currentFormat && call.ProviderUsageAvailability is not null ||
                !currentFormat && call.ProviderUsageAvailable == true &&
                    classified == VerificationUsageAvailability.Unavailable ||
                !VerificationUsage.IsInternallyConsistent(call.InputTokens, call.CachedInputTokens,
                    call.OutputTokens, call.ReasoningTokens) ||
                call.ProviderUsageAvailability is { } availability &&
                    (!Enum.IsDefined(availability) || availability != classified ||
                     call.ProviderUsageAvailable is null || !VerificationUsage.LegacyBooleanMatches(
                         call.ProviderUsageAvailable.Value, availability))) Corrupt();
        }
    }

    private static void ValidateCollections(EngineeringTask task)
    {
        if (task.VerificationPlans is null || task.VerificationPlanGenerationAttempts is null ||
            task.ManualVerificationAttempts is null || task.ModelCalls is null) Corrupt();
        if (task.VerificationPlans.Any(item => item is null) ||
            task.VerificationPlanGenerationAttempts.Any(item => item is null) ||
            task.ManualVerificationAttempts.Any(item => item is null) || task.ModelCalls.Any(item => item is null)) Corrupt();
        foreach (var generation in task.VerificationPlanGenerationAttempts)
            if (generation.ModelCallIds is null || generation.ProviderResponses is null || generation.LogicalCalls is null ||
                generation.ProviderResponses.Any(item => item is null) || generation.LogicalCalls.Any(item => item is null) ||
                !Enum.IsDefined(generation.Status)) Corrupt();
        foreach (var plan in task.VerificationPlans)
        {
            if (plan.Preconditions is null || plan.TestCases is null || plan.Risks is null ||
                plan.Limitations is null || plan.EvidenceGuidance is null || plan.ModelCallIds is null ||
                plan.Preconditions.Any(item => item is null) || plan.TestCases.Any(item => item is null) ||
                plan.Risks.Any(item => item is null) || plan.Limitations.Any(item => item is null) ||
                plan.EvidenceGuidance.Any(item => item is null) ||
                !Enum.IsDefined(plan.Source) || !Enum.IsDefined(plan.Status)) Corrupt();
            foreach (var testCase in plan.TestCases)
            {
                if (testCase.Preconditions is null || testCase.TestData is null || testCase.OrderedSteps is null ||
                    testCase.NegativeOrEdgeCases is null || testCase.RegressionScope is null ||
                    testCase.EvidenceRequirements is null || testCase.SafetyNotes is null ||
                    testCase.RegressionFailureReportIds is null || testCase.Preconditions.Any(item => item is null) ||
                    testCase.TestData.Any(item => item is null) || testCase.OrderedSteps.Any(item => item is null) ||
                    testCase.NegativeOrEdgeCases.Any(item => item is null) ||
                    testCase.RegressionScope.Any(item => item is null) ||
                    testCase.EvidenceRequirements.Any(item => item is null) ||
                    testCase.SafetyNotes.Any(item => item is null) || !Enum.IsDefined(testCase.Category)) Corrupt();
            }
        }
        foreach (var attempt in task.ManualVerificationAttempts)
        {
            if (attempt.ResultRevisions is null || attempt.ResultRevisions.Any(item => item is null) ||
                !Enum.IsDefined(attempt.Status)) Corrupt();
            foreach (var result in attempt.ResultRevisions)
            {
                if (result.EvidenceDescriptions is null || result.EvidenceDescriptions.Any(item => item is null) ||
                    result.FailureDetails is { } failure &&
                    (failure.ReproductionSteps is null || failure.EnvironmentNotes is null ||
                     failure.EvidenceDescriptions is null || failure.ReproductionSteps.Any(item => item is null) ||
                     failure.EnvironmentNotes.Any(item => item is null) ||
                     failure.EvidenceDescriptions.Any(item => item is null) || !Enum.IsDefined(failure.Severity))) Corrupt();
            }
        }
    }
}
