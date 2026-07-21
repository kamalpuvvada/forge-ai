namespace Forge.Core;

public static class PersistedCorrectionValidator
{
    public static void Validate(EngineeringTask task, CorrectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(limits);
        var correctionState = task.Status is WorkflowStatus.FailureAnalysisPending or
            WorkflowStatus.FailureAnalysisRecoveryRequired or
            WorkflowStatus.AwaitingFailureResolution or WorkflowStatus.AwaitingCorrectionApproval or
            WorkflowStatus.CorrectionApproved or WorkflowStatus.ImplementingCorrection or
            WorkflowStatus.CorrectionRecoveryRequired ||
            task.ImplementationRevisions.Count == 2;
        var artifacts = task.FailureAnalyses.Count > 0 || task.CorrectionProposals.Count > 0 ||
            task.FailureAnalysisGenerationAttempts.Count > 0 || task.CorrectionGenerationAttempts.Count > 0 ||
            task.CurrentFailureAnalysisId is not null || task.CurrentCorrectionProposalId is not null || correctionState;
        if (task.CorrectionDataFormatVersion == 0)
        {
            if (artifacts) Corrupt();
            return;
        }
        if (task.CorrectionDataFormatVersion != 1 || !artifacts ||
            task.FailureAnalyses.Count > limits.MaximumAnalysesPerTask || task.CorrectionProposals.Count > 1)
            Corrupt();
        if (!task.FailureAnalyses.Select(item => item.AnalysisNumber)
                .SequenceEqual(Enumerable.Range(1, task.FailureAnalyses.Count)) ||
            task.FailureAnalyses.Select(item => item.AnalysisId).Distinct().Count() != task.FailureAnalyses.Count ||
            !task.CorrectionProposals.Select(item => item.ProposalNumber)
                .SequenceEqual(Enumerable.Range(1, task.CorrectionProposals.Count))) Corrupt();
        foreach (var analysis in task.FailureAnalyses)
        {
            if (analysis.AnalysisId == Guid.Empty || analysis.GenerationCommandId == Guid.Empty ||
                analysis.Status != FailureAnalysisStatus.Completed || analysis.CreatedAt.Offset != TimeSpan.Zero ||
                analysis.FailureResultRevisionIds.Count == 0 || analysis.FailureResultRevisionIds.Distinct().Count() !=
                    analysis.FailureResultRevisionIds.Count || analysis.ConfidencePercent is < 0 or > 100 ||
                !IsSha(analysis.ContextFingerprint) || !IsSha(analysis.FailedAttemptFingerprint) ||
                !IsSha(analysis.VerificationPlanFingerprint) || !IsSha(analysis.ImplementationResultFingerprint) ||
                !IsSha(analysis.ApprovedRequirementFingerprint) || !IsSha(analysis.ApprovedPlanFingerprint) ||
                !IsSha(analysis.AnalysisFingerprint) ||
                !string.Equals(analysis.AnalysisFingerprint,
                    CorrectionFingerprint.ComputeAnalysis(task.Id, analysis with { AnalysisFingerprint = string.Empty }),
                    StringComparison.Ordinal)) Corrupt();
            var context = ReconstructContext(task, analysis);
            if (!string.Equals(context.ContextFingerprint, analysis.ContextFingerprint, StringComparison.Ordinal) ||
                context.FailedAttemptId != analysis.FailedAttemptId ||
                !context.FailedResultRevisionIds.SequenceEqual(analysis.FailureResultRevisionIds) ||
                context.VerificationPlanId != analysis.VerificationPlanId ||
                !string.Equals(context.VerificationPlanFingerprint, analysis.VerificationPlanFingerprint, StringComparison.Ordinal) ||
                context.ImplementationRevisionId != analysis.ImplementationRevisionId ||
                !string.Equals(context.ImplementationResultFingerprint, analysis.ImplementationResultFingerprint, StringComparison.Ordinal) ||
                !string.Equals(context.ApprovedRequirementFingerprint, analysis.ApprovedRequirementFingerprint, StringComparison.Ordinal) ||
                !string.Equals(context.ApprovedPlanFingerprint, analysis.ApprovedPlanFingerprint, StringComparison.Ordinal) ||
                !string.Equals(context.OriginalBaseCommitSha, analysis.OriginalBaseCommitSha, StringComparison.Ordinal)) Corrupt();
            try
            {
                CorrectionValidator.ValidateCandidate(context, new FailureAnalysisCandidate(
                    analysis.ContextFingerprint, analysis.Classification, analysis.ConfidencePercent,
                    analysis.RootCauseSummary, analysis.Rationale, analysis.EvidenceReferences,
                    analysis.AffectedApprovedOperations, analysis.CorrectionStrategy, analysis.ExpectedBehavior,
                    analysis.VerificationImpact, analysis.Risks, analysis.Source, analysis.Model,
                    analysis.ReasoningEffort), limits);
            }
            catch (CorrectionException) { Corrupt(); }
            ValidateAnalysisProvenance(task, analysis);
        }
        var current = task.CurrentFailureAnalysisId is { } analysisId
            ? task.FailureAnalyses.SingleOrDefault(item => item.AnalysisId == analysisId)
            : null;
        if ((task.CurrentFailureAnalysisId is null) != (current is null)) Corrupt();
        foreach (var proposal in task.CorrectionProposals)
        {
            if (proposal.ProposalId == Guid.Empty || !IsSha(proposal.ProposalFingerprint) ||
                proposal.AffectedApprovedOperations.Count == 0 ||
                !string.Equals(proposal.ProposalFingerprint,
                    CorrectionFingerprint.ComputeProposal(task.Id, proposal with { ProposalFingerprint = string.Empty }),
                    StringComparison.Ordinal) || task.FailureAnalyses.All(item => item.AnalysisId != proposal.AnalysisId)) Corrupt();
            var source = task.FailureAnalyses.Single(item => item.AnalysisId == proposal.AnalysisId);
            var previous = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == proposal.PreviousApprovedRevisionId);
            if (previous?.ResultFingerprint is null ||
                !string.Equals(source.AnalysisFingerprint, proposal.AnalysisFingerprint, StringComparison.Ordinal) ||
                source.FailedAttemptId != proposal.FailedAttemptId ||
                !string.Equals(source.FailedAttemptFingerprint, proposal.FailedAttemptFingerprint, StringComparison.Ordinal) ||
                !source.FailureResultRevisionIds.SequenceEqual(proposal.FailureResultRevisionIds) ||
                !string.Equals(previous.ResultFingerprint, proposal.PreviousResultFingerprint, StringComparison.Ordinal) ||
                !string.Equals(source.ApprovedRequirementFingerprint, proposal.ApprovedRequirementFingerprint, StringComparison.Ordinal) ||
                !string.Equals(source.ApprovedPlanFingerprint, proposal.ApprovedPlanFingerprint, StringComparison.Ordinal) ||
                !string.Equals(source.OriginalBaseCommitSha, proposal.OriginalBaseCommitSha, StringComparison.Ordinal) ||
                !source.AffectedApprovedOperations.SequenceEqual(proposal.AffectedApprovedOperations) ||
                !string.Equals(source.RootCauseSummary, proposal.RootCauseSummary, StringComparison.Ordinal) ||
                !string.Equals(source.CorrectionStrategy, proposal.CorrectionStrategy, StringComparison.Ordinal) ||
                !string.Equals(source.ExpectedBehavior, proposal.ExpectedBehavior, StringComparison.Ordinal) ||
                !string.Equals(source.VerificationImpact, proposal.VerificationImpact, StringComparison.Ordinal) ||
                !source.Risks.SequenceEqual(proposal.Risks)) Corrupt();
            try { CorrectionValidator.ValidateProposal(proposal, limits); }
            catch (CorrectionException) { Corrupt(); }
            if (proposal.Status == CorrectionProposalStatus.Approved)
            {
                if (proposal.ApprovedAt is null || proposal.ApprovalCommandId is null || proposal.ApprovalExpectedRowVersion is null) Corrupt();
            }
            else if (proposal.ApprovedAt is not null || proposal.ApprovalCommandId is not null || proposal.ApprovalExpectedRowVersion is not null) Corrupt();
        }
        ValidateApprovalCommands(task);
        var currentProposal = task.CurrentCorrectionProposalId is { } proposalId
            ? task.CorrectionProposals.SingleOrDefault(item => item.ProposalId == proposalId)
            : null;
        if ((task.CurrentCorrectionProposalId is null) != (currentProposal is null)) Corrupt();
        ValidateFailureAttempts(task);
        ValidateCorrectionAttempts(task);
        switch (task.Status)
        {
            case WorkflowStatus.FailureAnalysisPending:
                if (task.FailureAnalysisGenerationAttempts.LastOrDefault()?.Status is not
                    (FailureAnalysisAttemptStatus.Prepared or FailureAnalysisAttemptStatus.DispatchMayHaveStarted or
                     FailureAnalysisAttemptStatus.ResponseReceived)) Corrupt();
                break;
            case WorkflowStatus.FailureAnalysisRecoveryRequired:
                if (current is not null || task.FailureAnalysisGenerationAttempts.LastOrDefault()?.RecoveryRequired != true) Corrupt();
                break;
            case WorkflowStatus.AwaitingFailureResolution:
                if (current is null || current.Classification == FailureClassification.ImplementationDefect || currentProposal is not null) Corrupt();
                break;
            case WorkflowStatus.AwaitingCorrectionApproval:
                if (current?.Classification != FailureClassification.ImplementationDefect || currentProposal?.Status != CorrectionProposalStatus.AwaitingApproval) Corrupt();
                break;
            case WorkflowStatus.CorrectionApproved:
                if (current?.Classification != FailureClassification.ImplementationDefect || currentProposal?.Status != CorrectionProposalStatus.Approved) Corrupt();
                break;
            case WorkflowStatus.ImplementingCorrection:
                if (current?.Classification != FailureClassification.ImplementationDefect ||
                    currentProposal?.Status != CorrectionProposalStatus.Approved ||
                    task.CorrectionGenerationAttempts.LastOrDefault()?.Status is not
                        (CorrectionGenerationAttemptStatus.Prepared or CorrectionGenerationAttemptStatus.DispatchMayHaveStarted or
                         CorrectionGenerationAttemptStatus.ResponseReceived or CorrectionGenerationAttemptStatus.OutputAccepted or
                         CorrectionGenerationAttemptStatus.CheckoutVerified or CorrectionGenerationAttemptStatus.RevisionReserved or
                         CorrectionGenerationAttemptStatus.WorkspacePreparing or CorrectionGenerationAttemptStatus.WorkspacePrepared or
                         CorrectionGenerationAttemptStatus.MutationStarted or CorrectionGenerationAttemptStatus.ApplyCompleted or
                         CorrectionGenerationAttemptStatus.ResultPersisted)) Corrupt();
                break;
            case WorkflowStatus.CorrectionRecoveryRequired:
                if (current?.Classification != FailureClassification.ImplementationDefect ||
                    currentProposal?.Status != CorrectionProposalStatus.Approved ||
                    task.CorrectionGenerationAttempts.LastOrDefault()?.RecoveryRequired != true) Corrupt();
                break;
            case WorkflowStatus.AwaitingImplementationReview when task.ImplementationRevisions.Count == 2:
                if (current?.Classification != FailureClassification.ImplementationDefect ||
                    currentProposal?.Status != CorrectionProposalStatus.Approved ||
                    task.CorrectionGenerationAttempts.LastOrDefault()?.Status is not
                        (CorrectionGenerationAttemptStatus.ResultPersisted or CorrectionGenerationAttemptStatus.Completed)) Corrupt();
                break;
        }
        if (task.ImplementationRevisions.Count == 2)
        {
            var correction = task.ImplementationRevisions[1];
            if (currentProposal is null || correction.CorrectionProposalId != currentProposal.ProposalId ||
                !string.Equals(correction.CorrectionProposalFingerprint, currentProposal.ProposalFingerprint, StringComparison.Ordinal)) Corrupt();
        }
    }

    private static FailureAnalysisContext ReconstructContext(EngineeringTask task, FailureAnalysis analysis)
    {
        var attempt = task.ManualVerificationAttempts.SingleOrDefault(item => item.AttemptId == analysis.FailedAttemptId);
        var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == analysis.VerificationPlanId);
        var revision = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == analysis.ImplementationRevisionId);
        if (attempt is not { Status: ManualVerificationAttemptStatus.CompletedFailed } || plan is null ||
            revision?.ResultFingerprint is null || task.ImplementationPlan is null ||
            !string.Equals(attempt.AttemptFingerprint, analysis.FailedAttemptFingerprint, StringComparison.Ordinal))
            throw new TaskDataCorruptException("Stored correction data is invalid or incomplete. The task cannot be resumed automatically.");
        var results = VerificationFingerprint.CurrentResults(attempt)
            .Where(item => item.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked)
            .OrderBy(item => item.ResultRevisionId).ToArray();
        if (results.Length == 0 || results.Any(item => item.FailureDetails is null)) Corrupt();
        var cases = plan.TestCases.ToDictionary(item => item.TestCaseId);
        var evidence = results.Select(item => new FailureAnalysisResultEvidence(item.ResultRevisionId,
            item.TestCaseId, cases.TryGetValue(item.TestCaseId, out var testCase) ? testCase.Title : "Recorded verification case",
            item.Result, item.FailureDetails!)).ToArray();
        var operations = task.ImplementationPlan.AffectedFiles.Where(item => item.Action != PlannedFileAction.Inspect)
            .Select(item => new ApprovedOperationReference(RepositoryPathRules.Normalize(item.Path), item.Action switch
            {
                PlannedFileAction.Create => ImplementationOperationAction.Create,
                PlannedFileAction.Modify => ImplementationOperationAction.Modify,
                PlannedFileAction.Delete => ImplementationOperationAction.Delete,
                _ => throw new InvalidOperationException()
            })).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var context = new FailureAnalysisContext(task.Id, attempt.AttemptId, attempt.AttemptFingerprint!,
            results.Select(item => item.ResultRevisionId).ToArray(), plan.PlanId, plan.PlanFingerprint,
            revision.RevisionId, revision.ResultFingerprint, VerificationFingerprint.ComputeApprovedRequirement(task),
            ImplementationReviewFingerprint.ComputePlan(task.ImplementationPlan), revision.BaseCommitSha,
            evidence, operations, string.Empty, analysis.CreatedAt);
        return context with { ContextFingerprint = CorrectionFingerprint.ComputeContext(context) };
    }

    private static void ValidateFailureAttempts(EngineeringTask task)
    {
        if (task.FailureAnalysisGenerationAttempts.Select(item => item.CommandId).Distinct().Count() !=
            task.FailureAnalysisGenerationAttempts.Count) Corrupt();
        foreach (var attempt in task.FailureAnalysisGenerationAttempts)
        {
            var lease = attempt.LeaseExpiresAt;
            var leaseValue = lease.GetValueOrDefault();
            if (attempt.CommandId == Guid.Empty || attempt.TaskId != task.Id || attempt.StartedAt.Offset != TimeSpan.Zero ||
                attempt.UpdatedAt.Offset != TimeSpan.Zero || attempt.UpdatedAt < attempt.StartedAt || !Enum.IsDefined(attempt.Status) ||
                lease is null || leaseValue.Offset != TimeSpan.Zero || leaseValue <= attempt.StartedAt ||
                attempt.RetryEligible != (attempt.Status is FailureAnalysisAttemptStatus.FailedBeforeDispatch or
                    FailureAnalysisAttemptStatus.ExpiredBeforeDispatch or FailureAnalysisAttemptStatus.RetryableProviderResponse or
                    FailureAnalysisAttemptStatus.RejectedProviderOutput) ||
                attempt.RecoveryRequired != (attempt.Status is FailureAnalysisAttemptStatus.AmbiguousAfterDispatch or
                    FailureAnalysisAttemptStatus.InterruptedAfterResponse)) Corrupt();
            var terminal = attempt.Status is FailureAnalysisAttemptStatus.Completed or FailureAnalysisAttemptStatus.FailedBeforeDispatch or
                FailureAnalysisAttemptStatus.ExpiredBeforeDispatch or FailureAnalysisAttemptStatus.RetryableProviderResponse or
                FailureAnalysisAttemptStatus.RejectedProviderOutput or FailureAnalysisAttemptStatus.AmbiguousAfterDispatch or
                FailureAnalysisAttemptStatus.InterruptedAfterResponse;
            CorrectionTelemetryValidator.Validate(task, attempt.LogicalCalls, attempt.ModelCallIds, attempt.ProviderResponses,
                attempt.LogicalCallCount, attempt.PhysicalRequestCount, attempt.PossiblyDispatchedRequestCount,
                attempt.DefinitelyUndispatchedRequestCount, attempt.ActiveRequestCount, attempt.StartedAt, leaseValue,
                attempt.UpdatedAt, attempt.CompletedAt, ModelCallStage.FailureAnalysis, terminal);
            if (attempt.Status == FailureAnalysisAttemptStatus.Completed &&
                (attempt.ResultAnalysisId is null || task.FailureAnalyses.All(item => item.AnalysisId != attempt.ResultAnalysisId ||
                    item.GenerationCommandId != attempt.CommandId || !item.ModelCallIds.SequenceEqual(attempt.ModelCallIds)))) Corrupt();
            if (attempt.Status != FailureAnalysisAttemptStatus.Completed && attempt.ResultAnalysisId is not null) Corrupt();
            if (attempt.Status == FailureAnalysisAttemptStatus.Prepared &&
                (attempt.LogicalCallCount != 0 || attempt.PhysicalRequestCount != 0) ||
                attempt.Status == FailureAnalysisAttemptStatus.ResponseReceived && attempt.ProviderResponses.Count == 0 ||
                attempt.Status == FailureAnalysisAttemptStatus.RejectedProviderOutput && attempt.ProviderResponses.Count == 0 ||
                attempt.Status == FailureAnalysisAttemptStatus.AmbiguousAfterDispatch && attempt.LogicalCallCount == 0 ||
                attempt.Status == FailureAnalysisAttemptStatus.InterruptedAfterResponse && attempt.ProviderResponses.Count == 0)
                Corrupt();
        }
        if (task.FailureAnalysisGenerationAttempts.Count(attempt => attempt.Status is
                FailureAnalysisAttemptStatus.Prepared or FailureAnalysisAttemptStatus.DispatchMayHaveStarted or
                FailureAnalysisAttemptStatus.ResponseReceived) > 1) Corrupt();
    }

    private static void ValidateApprovalCommands(EngineeringTask task)
    {
        if (task.CorrectionApprovalCommands.Select(item => item.CommandId).Distinct().Count() !=
            task.CorrectionApprovalCommands.Count) Corrupt();
        foreach (var binding in task.CorrectionApprovalCommands)
        {
            var proposal = task.CorrectionProposals.SingleOrDefault(item => item.ProposalId == binding.ProposalId);
            if (binding.CommandId == Guid.Empty || binding.TaskId != task.Id || proposal is null ||
                proposal.Status != CorrectionProposalStatus.Approved || proposal.ApprovalCommandId != binding.CommandId ||
                proposal.ApprovalExpectedRowVersion != binding.ExpectedRowVersion ||
                !string.Equals(proposal.ProposalFingerprint, binding.ProposalFingerprint, StringComparison.Ordinal) ||
                !string.Equals(binding.SemanticFingerprint,
                    CorrectionFingerprint.ComputeApprovalCommandSemantic(task.Id, binding.ExpectedRowVersion, proposal),
                    StringComparison.Ordinal) || binding.CompletedRowVersion != binding.ExpectedRowVersion + 1 ||
                binding.CompletedRowVersion > task.RowVersion + 1 || binding.CreatedAt.Offset != TimeSpan.Zero ||
                binding.CompletedAt.Offset != TimeSpan.Zero || binding.CompletedAt < binding.CreatedAt ||
                proposal.ApprovedAt != binding.CompletedAt || !string.Equals(binding.Result, "Approved", StringComparison.Ordinal)) Corrupt();
        }
        foreach (var proposal in task.CorrectionProposals)
        {
            var matches = task.CorrectionApprovalCommands.Count(item => item.ProposalId == proposal.ProposalId);
            if (proposal.Status == CorrectionProposalStatus.Approved ? matches != 1 : matches != 0) Corrupt();
        }
    }

    private static void ValidateCorrectionAttempts(EngineeringTask task)
    {
        if (task.CorrectionGenerationAttempts.Select(item => item.CommandId).Distinct().Count() !=
            task.CorrectionGenerationAttempts.Count) Corrupt();
        foreach (var attempt in task.CorrectionGenerationAttempts)
        {
            var lease = attempt.LeaseExpiresAt;
            var leaseValue = lease.GetValueOrDefault();
            if (attempt.AttemptId == Guid.Empty || attempt.CommandId == Guid.Empty || attempt.TaskId != task.Id ||
                !Enum.IsDefined(attempt.Status) || attempt.StartedAt.Offset != TimeSpan.Zero || attempt.UpdatedAt.Offset != TimeSpan.Zero ||
                attempt.UpdatedAt < attempt.StartedAt || lease is null || leaseValue.Offset != TimeSpan.Zero ||
                leaseValue <= attempt.StartedAt || attempt.RetryEligible != (attempt.Status is
                    CorrectionGenerationAttemptStatus.FailedBeforeDispatch or CorrectionGenerationAttemptStatus.FailedBeforeMutation) ||
                attempt.RecoveryRequired != (attempt.Status is CorrectionGenerationAttemptStatus.AmbiguousAfterDispatch or
                    CorrectionGenerationAttemptStatus.InterruptedAfterResponse or CorrectionGenerationAttemptStatus.RecoveryRequired) ||
                (attempt.Status is CorrectionGenerationAttemptStatus.OutputAccepted or CorrectionGenerationAttemptStatus.CheckoutVerified or
                    CorrectionGenerationAttemptStatus.RevisionReserved or CorrectionGenerationAttemptStatus.WorkspacePreparing or
                    CorrectionGenerationAttemptStatus.WorkspacePrepared or CorrectionGenerationAttemptStatus.MutationStarted or
                    CorrectionGenerationAttemptStatus.ApplyCompleted or CorrectionGenerationAttemptStatus.ResultPersisted or
                    CorrectionGenerationAttemptStatus.Completed) && !IsSha(attempt.AcceptedOutputFingerprint)) Corrupt();
            var terminal = attempt.Status is CorrectionGenerationAttemptStatus.FailedBeforeDispatch or
                CorrectionGenerationAttemptStatus.FailedBeforeMutation or CorrectionGenerationAttemptStatus.AmbiguousAfterDispatch or
                CorrectionGenerationAttemptStatus.InterruptedAfterResponse or CorrectionGenerationAttemptStatus.RecoveryRequired or
                CorrectionGenerationAttemptStatus.Completed;
            CorrectionTelemetryValidator.Validate(task, attempt.LogicalCalls, attempt.ModelCallIds, attempt.ProviderResponses,
                attempt.LogicalCallCount, attempt.PhysicalRequestCount, attempt.PossiblyDispatchedRequestCount,
                attempt.DefinitelyUndispatchedRequestCount, attempt.ActiveRequestCount, attempt.StartedAt, leaseValue,
                attempt.UpdatedAt, attempt.CompletedAt, ModelCallStage.Implementation, terminal);
            var calls = attempt.ModelCallIds.Select(id => task.ModelCalls.Single(call => call.Id == id)).ToArray();
            if (calls.Select(call => call.Provider).Distinct(StringComparer.Ordinal).Count() > 1 ||
                calls.Select(call => call.Model).Distinct(StringComparer.Ordinal).Count() > 1 ||
                calls.Select(call => call.ReasoningEffort).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) Corrupt();
            if (attempt.Status is CorrectionGenerationAttemptStatus.ResultPersisted or CorrectionGenerationAttemptStatus.Completed)
            {
                var revision = task.ImplementationRevisions.Single(item => item.RevisionId == attempt.RevisionId);
                var result = revision.Result ?? throw new TaskDataCorruptException(
                    "Stored correction data is invalid or incomplete. The task cannot be resumed automatically.");
                if (result.Source == ImplementationSource.DeterministicFake &&
                    (result.Model is not null || calls.Length != 0 || attempt.ProviderResponses.Count != 0) ||
                    result.Source == ImplementationSource.OpenAI &&
                    (string.IsNullOrWhiteSpace(result.Model) || calls.Length == 0 || calls.Any(call =>
                        !string.Equals(call.Provider, "OpenAI", StringComparison.Ordinal) ||
                        !string.Equals(call.Model, result.Model, StringComparison.Ordinal) ||
                        call.VerificationDispatchDisposition != VerificationCallDispatchDisposition.ResponseReceived ||
                        attempt.ProviderResponses.All(response => response.LogicalCallId != call.Id)))) Corrupt();
            }
            if (attempt.Status == CorrectionGenerationAttemptStatus.InterruptedAfterResponse && attempt.ProviderResponses.Count == 0 ||
                attempt.Status == CorrectionGenerationAttemptStatus.ResultPersisted &&
                    (attempt.RevisionId is null || task.ImplementationRevisions.All(revision =>
                        revision.RevisionId != attempt.RevisionId || revision.Result is null || revision.ResultFingerprint is null))) Corrupt();
        }
        if (task.CorrectionGenerationAttempts.Count(attempt => attempt.Status is
                CorrectionGenerationAttemptStatus.Prepared or CorrectionGenerationAttemptStatus.DispatchMayHaveStarted or
                CorrectionGenerationAttemptStatus.ResponseReceived or CorrectionGenerationAttemptStatus.OutputAccepted or
                CorrectionGenerationAttemptStatus.CheckoutVerified or CorrectionGenerationAttemptStatus.RevisionReserved or
                CorrectionGenerationAttemptStatus.WorkspacePreparing or CorrectionGenerationAttemptStatus.WorkspacePrepared or
                CorrectionGenerationAttemptStatus.MutationStarted or CorrectionGenerationAttemptStatus.ApplyCompleted or
                CorrectionGenerationAttemptStatus.ResultPersisted) > 1) Corrupt();
    }

    private static void ValidateAnalysisProvenance(EngineeringTask task, FailureAnalysis analysis)
    {
        var calls = analysis.ModelCallIds.Select(id => task.ModelCalls.SingleOrDefault(call => call.Id == id)).ToArray();
        var generation = task.FailureAnalysisGenerationAttempts.SingleOrDefault(item =>
            item.CommandId == analysis.GenerationCommandId && item.Status == FailureAnalysisAttemptStatus.Completed);
        if (analysis.ModelCallIds.Distinct().Count() != analysis.ModelCallIds.Count || calls.Any(call => call is null)) Corrupt();
        if (analysis.Source == FailureAnalysisSource.DeterministicFake)
        {
            if (analysis.Model is not null || analysis.ReasoningEffort is not null || analysis.ModelCallIds.Count != 0 ||
                generation is null || generation.LogicalCallCount != 0 || generation.ProviderResponses.Count != 0 ||
                generation.ModelCallIds.Count != 0 || generation.PhysicalRequestCount != 0 ||
                generation.PossiblyDispatchedRequestCount != 0 || generation.DefinitelyUndispatchedRequestCount != 0) Corrupt();
            return;
        }
        var allowedReasoning = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "none", "minimal", "low", "medium", "high", "xhigh", "max" };
        if (analysis.Source != FailureAnalysisSource.OpenAI || string.IsNullOrWhiteSpace(analysis.Model) ||
            analysis.Model.Length > 200 || string.IsNullOrWhiteSpace(analysis.ReasoningEffort) ||
            !allowedReasoning.Contains(analysis.ReasoningEffort) || calls.Length == 0 || calls.Any(call =>
                call!.Stage != ModelCallStage.FailureAnalysis || !string.Equals(call.Provider, "OpenAI", StringComparison.Ordinal) ||
                !string.Equals(call.Model, analysis.Model, StringComparison.Ordinal) ||
                !string.Equals(call.ReasoningEffort, analysis.ReasoningEffort, StringComparison.OrdinalIgnoreCase) ||
                call.VerificationDispatchDisposition != VerificationCallDispatchDisposition.ResponseReceived)) Corrupt();
        if (generation is null || !generation.ModelCallIds.SequenceEqual(analysis.ModelCallIds) ||
            generation.ProviderResponses.Count == 0 || calls.Any(call => generation.ProviderResponses.All(response =>
                response.LogicalCallId != call!.Id))) Corrupt();
    }

    private static bool IsSha(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Corrupt() => throw new TaskDataCorruptException(
        "Stored correction data is invalid or incomplete. The task cannot be resumed automatically.");
}
