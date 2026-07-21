using Forge.Core;
using Forge.Infrastructure;
using UglyToad.PdfPig;

namespace Forge.Core.Tests;

public sealed class CorrectionWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly CorrectionLimits CorrectionLimits = new();
    private static readonly VerificationLimits VerificationLimits = new();

    [Theory]
    [InlineData(FailureClassification.ImplementationDefect, WorkflowStatus.AwaitingCorrectionApproval, true)]
    [InlineData(FailureClassification.ApprovedPlanDefect, WorkflowStatus.AwaitingFailureResolution, false)]
    [InlineData(FailureClassification.ApprovedRequirementDefect, WorkflowStatus.AwaitingFailureResolution, false)]
    [InlineData(FailureClassification.EnvironmentOrSetupIssue, WorkflowStatus.AwaitingFailureResolution, false)]
    [InlineData(FailureClassification.InsufficientEvidence, WorkflowStatus.AwaitingFailureResolution, false)]
    public async Task All_classifications_persist_and_only_implementation_defect_creates_proposal(
        FailureClassification classification, WorkflowStatus expectedStatus, bool expectsProposal)
    {
        var task = await FailedTask();
        var command = AnalysisCommand(task);
        task.BeginFailureAnalysis(command, Now.AddMinutes(10));
        var context = CorrectionWorkflowService.CreateAnalysisContext(task, Now.AddMinutes(10));
        var fake = (await new FakeFailureAnalysisEngine().GenerateAsync(context,
            new NoopObserver())).Candidate;
        var candidate = classification == FailureClassification.ImplementationDefect
            ? fake
            : fake with
            {
                Classification = classification,
                AffectedApprovedOperations = [],
                CorrectionStrategy = string.Empty
            };
        var analysis = CorrectionValidator.FinalizeAnalysis(context, candidate, 1, Guid.NewGuid(),
            command.CommandId, [], CorrectionLimits);
        var proposal = expectsProposal
            ? CorrectionValidator.CreateProposal(task.Id, analysis, task.ImplementationRevisions[0], 1,
                Now.AddMinutes(11), CorrectionLimits)
            : null;

        task.StoreFailureAnalysis(analysis, proposal, Now.AddMinutes(11));

        Assert.Equal(expectedStatus, task.Status);
        Assert.Equal(expectsProposal ? 1 : 0, task.CorrectionProposals.Count);
        Assert.Equal(classification, Assert.Single(task.FailureAnalyses).Classification);
    }

    [Fact]
    public async Task Proposal_rejects_scope_expansion_changed_action_and_sensitive_or_delivery_content()
    {
        var task = await FailedTask();
        var context = CorrectionWorkflowTestExtensions.CreateAnalysisContextAfterBegin(task, Now, out var command);
        var candidate = (await new FakeFailureAnalysisEngine().GenerateAsync(context, new NoopObserver())).Candidate;

        Assert.Throws<CorrectionException>(() => CorrectionValidator.ValidateCandidate(context,
            candidate with { AffectedApprovedOperations = [new ApprovedOperationReference("outside.cs", ImplementationOperationAction.Modify)] },
            CorrectionLimits));
        Assert.Throws<CorrectionException>(() => CorrectionValidator.ValidateCandidate(context,
            candidate with { AffectedApprovedOperations = [candidate.AffectedApprovedOperations[0] with { Action = ImplementationOperationAction.Delete }] },
            CorrectionLimits));
        Assert.Throws<CorrectionException>(() => CorrectionValidator.ValidateCandidate(context,
            candidate with { CorrectionStrategy = "commit and push the correction" }, CorrectionLimits));
        Assert.Throws<CorrectionException>(() => CorrectionValidator.ValidateCandidate(context,
            candidate with { Rationale = SyntheticSensitiveValues.BearerAuthorization() }, CorrectionLimits));
        Assert.NotEqual(Guid.Empty, command.CommandId);
    }

    [Fact]
    public async Task Revision_two_preserves_revision_one_and_replacement_plan_covers_every_failed_result()
    {
        var task = await FailedTask();
        var analysisCommand = AnalysisCommand(task);
        task.BeginFailureAnalysis(analysisCommand, Now.AddMinutes(10));
        var analysisContext = CorrectionWorkflowService.CreateAnalysisContext(task, Now.AddMinutes(10));
        var candidate = (await new FakeFailureAnalysisEngine().GenerateAsync(analysisContext,
            new NoopObserver())).Candidate;
        var analysis = CorrectionValidator.FinalizeAnalysis(analysisContext, candidate, 1, Guid.NewGuid(),
            analysisCommand.CommandId, [], CorrectionLimits);
        var revisionOne = task.ImplementationRevisions[0];
        var proposal = CorrectionValidator.CreateProposal(task.Id, analysis, revisionOne, 1,
            Now.AddMinutes(11), CorrectionLimits);
        task.StoreFailureAnalysis(analysis, proposal, Now.AddMinutes(11));
        task.ApproveCorrectionProposal(new ApproveCorrectionProposalCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, proposal.ProposalId, proposal.ProposalFingerprint,
            analysis.AnalysisId, analysis.AnalysisFingerprint, proposal.FailedAttemptId,
            proposal.FailedAttemptFingerprint, revisionOne.RevisionId, revisionOne.ResultFingerprint!,
            proposal.ApprovedRequirementFingerprint, proposal.ApprovedPlanFingerprint,
            proposal.OriginalBaseCommitSha), Now.AddMinutes(12));

        const string token = "cccccccccccccccccccccccccccccccc";
        var workspace = new ImplementationWorkspace(token, $"forge/task-{task.Id:N}-revision-2",
            revisionOne.BaseCommitSha, ImplementationWorkspacePhase.Reserved, Now.AddMinutes(13),
            Now.AddMinutes(13), false, new string('5', 64), new string('6', 64),
            $"refs/forge/tasks/{task.Id:N}-revision-2", RevisionNumber: 2, TaskToken: task.Id.ToString("N"));
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddMinutes(13), Now.AddMinutes(13), Now.AddMinutes(18));
        var correctionCommand = new GenerateCorrectionCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            proposal.ProposalId, proposal.ProposalFingerprint, revisionOne.RevisionId,
            revisionOne.ResultFingerprint!);
        task.BeginCorrection(correctionCommand, workspace, lease, Now.AddMinutes(13));
        var correctedResult = revisionOne.Result! with
        {
            Branch = workspace.Branch,
            Summary = "Deterministic correction revision.",
            ChangedFiles = revisionOne.Result.ChangedFiles.Select(file => file with
            {
                NewContentSha256 = new string('7', 64), Additions = file.Additions + 1
            }).ToArray(),
            CompletedAt = Now.AddMinutes(14)
        };
        task.StoreImplementationResult(correctedResult, lease.AttemptId, lease.OwnerId, Now.AddMinutes(14));
        var revisionTwo = task.ImplementationRevisions[1];
        task.CompleteCorrectionAttempt(correctionCommand.CommandId, revisionTwo.RevisionId, Now.AddMinutes(14));
        task.ApproveImplementation(Guid.NewGuid(), task.RowVersion, revisionTwo.RevisionId,
            revisionTwo.ResultFingerprint!, Now.AddMinutes(15));

        Assert.Equal(ImplementationReviewState.HistoricallyApproved, task.ImplementationRevisions[0].ReviewState);
        Assert.Equal(revisionOne.ResultFingerprint, task.ImplementationRevisions[0].ResultFingerprint);
        Assert.Equal(revisionTwo.RevisionId, task.ApprovedImplementationRevisionId);
        Assert.Throws<WorkflowException>(() => task.BeginCorrection(correctionCommand, workspace, lease, Now.AddMinutes(16)));

        var generation = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revisionTwo.RevisionId, revisionTwo.ResultFingerprint!);
        task.BeginVerificationPlanGeneration(generation, Now.AddMinutes(16));
        var replacementContext = VerificationWorkflowService.CreateContext(task, Now.AddMinutes(16));
        var replacementCandidate = (await new FakeVerificationPlanEngine().GenerateAsync(replacementContext)).Candidate;
        var regressionIndex = replacementCandidate.TestCases.ToList().FindIndex(item => item.RegressionFailureReportIds.Count > 0);
        Assert.True(regressionIndex >= 0);
        var regression = replacementCandidate.TestCases[regressionIndex];
        foreach (var allowed in new[]
        {
            "Repeat the previously failed user-reported manual review.",
            $"This regression case covers failure result {regression.RegressionFailureReportIds[0]:D} from attempt 1.",
            "The prior manual attempt reported the greeting behavior as failed."
        })
        {
            var cases = replacementCandidate.TestCases.ToArray();
            cases[regressionIndex] = regression with { Objective = allowed };
            VerificationValidator.ValidateCandidate(replacementContext,
                replacementCandidate with { TestCases = cases }, VerificationLimits);
        }
        foreach (var rejected in new[]
        {
            "Manual verification has already passed.",
            "The corrected implementation was verified.",
            "This test was executed successfully.",
            "Forge confirmed the fix."
        })
        {
            var cases = replacementCandidate.TestCases.ToArray();
            cases[regressionIndex] = regression with { Objective = rejected };
            Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(replacementContext,
                replacementCandidate with { TestCases = cases }, VerificationLimits));
        }
        var foreignFailureCases = replacementCandidate.TestCases.ToArray();
        foreignFailureCases[regressionIndex] = regression with
        {
            Objective = $"Repeat prior failed result {Guid.NewGuid():D} from attempt 1."
        };
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(replacementContext,
            replacementCandidate with { TestCases = foreignFailureCases }, VerificationLimits));
        var withoutOrigin = replacementCandidate.TestCases.ToArray();
        withoutOrigin[regressionIndex] = regression with { OriginTestCaseId = null };
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(replacementContext,
            replacementCandidate with { TestCases = withoutOrigin }, VerificationLimits));
        var wrongOrigin = replacementCandidate.TestCases.ToArray();
        wrongOrigin[regressionIndex] = regression with
        { OriginTestCaseId = replacementContext.PreviousPlan!.TestCases.First(item => item.TestCaseId != regression.OriginTestCaseId).TestCaseId };
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(replacementContext,
            replacementCandidate with { TestCases = wrongOrigin }, VerificationLimits));
        var wrongFailedResult = replacementCandidate.TestCases.ToArray();
        wrongFailedResult[regressionIndex] = regression with { RegressionFailureReportIds = [Guid.NewGuid()] };
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(replacementContext,
            replacementCandidate with { TestCases = wrongFailedResult }, VerificationLimits));
        var replacement = VerificationValidator.FinalizeCandidate(replacementContext, replacementCandidate, 2,
            Guid.NewGuid(), [], VerificationLimits);

        var expectedFailures = replacementContext.PreviousFailureEvidence!.Select(item => item.ResultRevisionId).ToHashSet();
        var covered = replacement.TestCases.Where(item => item.IsRequired)
            .SelectMany(item => item.RegressionFailureReportIds).ToHashSet();
        Assert.Equal(replacementContext.PreviousPlan!.PlanId, replacement.SupersedesPlanId);
        Assert.True(expectedFailures.SetEquals(covered));
        task.StoreVerificationPlan(generation.CommandId, replacement, Now.AddMinutes(17));
        var attemptTwo = new ManualVerificationAttempt(Guid.NewGuid(), 2, replacement.PlanId,
            replacement.PlanFingerprint, revisionTwo.RevisionId, revisionTwo.ResultFingerprint!,
            Now.AddMinutes(18), null, ManualVerificationAttemptStatus.InProgress, [], null, null,
            null, null, null, Guid.NewGuid(), null);
        task.StartManualVerification(attemptTwo, Now.AddMinutes(18));
        foreach (var testCase in replacement.TestCases.Where(item => item.IsRequired))
        {
            var current = task.ManualVerificationAttempts.Single(item => item.AttemptId == attemptTwo.AttemptId);
            var update = new UpdateManualVerificationCaseCommand(Guid.NewGuid(), task.Id, current.AttemptId,
                testCase.TestCaseId, task.RowVersion, replacement.PlanId, replacement.PlanFingerprint,
                revisionTwo.RevisionId, revisionTwo.ResultFingerprint!, ManualVerificationCaseResult.Passed,
                "Observed manually.", "Corrected behavior observed.",
                testCase.EvidenceRequirements.Count > 0 ? ["Safe user-reported observation."] : [], null, null);
            task.AppendManualCaseResult(VerificationValidator.CreateCaseResult(update, current, replacement,
                Now.AddMinutes(19), VerificationLimits), Now.AddMinutes(19));
        }
        task.CompleteManualVerification(attemptTwo.AttemptId, Guid.NewGuid(), true, true,
            "Correction verified manually.", Now.AddMinutes(20));
        Assert.Equal(WorkflowStatus.ReadyForDelivery, task.Status);
        Assert.Equal(ManualVerificationAttemptStatus.CompletedFailed, task.ManualVerificationAttempts[0].Status);
        Assert.Equal(ManualVerificationAttemptStatus.CompletedPassed, task.ManualVerificationAttempts[1].Status);

        var exporter = new TaskPdfExporter(new ModelCostResolver(new ModelCostCalculator(
            new Dictionary<string, ModelPricing>())), TimeProvider.System);
        using var pdf = PdfDocument.Open(exporter.Export(task));
        var text = string.Join('\n', pdf.GetPages().Select(page => page.Text));
        Assert.Contains("Failure analysis and correction chronology", text, StringComparison.Ordinal);
        Assert.Contains("FORGE GENERATED", text, StringComparison.Ordinal);
        Assert.Contains("HISTORICAL", text, StringComparison.Ordinal);
        Assert.Contains("Regression case", text, StringComparison.Ordinal);
        Assert.Contains("ReadyForDelivery", text, StringComparison.Ordinal);
    }

    internal static async Task<EngineeringTask> FailedTask()
    {
        var task = VerificationWorkflowTests.ApprovedImplementation();
        var revision = task.ImplementationRevisions.Single(item => item.IsApprovedFor(task));
        var generation = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        task.BeginVerificationPlanGeneration(generation, Now.AddMinutes(1));
        var context = VerificationWorkflowService.CreateContext(task, Now.AddMinutes(1));
        var planCandidate = (await new FakeVerificationPlanEngine().GenerateAsync(context)).Candidate;
        var plan = VerificationValidator.FinalizeCandidate(context, planCandidate, 1, Guid.NewGuid(), [], VerificationLimits);
        task.StoreVerificationPlan(generation.CommandId, plan, Now.AddMinutes(2));
        var attempt = new ManualVerificationAttempt(Guid.NewGuid(), 1, plan.PlanId, plan.PlanFingerprint,
            revision.RevisionId, revision.ResultFingerprint!, Now.AddMinutes(3), null,
            ManualVerificationAttemptStatus.InProgress, [], null, null, null, null, null, Guid.NewGuid(), null);
        task.StartManualVerification(attempt, Now.AddMinutes(3));
        var testCase = plan.TestCases[0];
        var failure = new VerificationFailureDetails("Observed mismatch in src/App.cs", testCase.ExpectedResult,
            "The behavior associated with src/App.cs still differs.", ["Repeat the exact manual case."], ["Disposable environment."],
            null, ["Safe local observation."], VerificationFailureSeverity.High);
        var update = new UpdateManualVerificationCaseCommand(Guid.NewGuid(), task.Id, attempt.AttemptId,
            testCase.TestCaseId, task.RowVersion, plan.PlanId, plan.PlanFingerprint, revision.RevisionId,
            revision.ResultFingerprint!, ManualVerificationCaseResult.Failed, null, failure.ActualResult,
            failure.EvidenceDescriptions, null, failure);
        task.AppendManualCaseResult(VerificationValidator.CreateCaseResult(update, attempt, plan,
            Now.AddMinutes(4), VerificationLimits), Now.AddMinutes(4));
        task.CompleteManualVerification(attempt.AttemptId, Guid.NewGuid(), false, true,
            "Failure confirmed by a human.", Now.AddMinutes(5));
        return task;
    }

    internal static GenerateFailureAnalysisCommand AnalysisCommand(EngineeringTask task)
    {
        var attempt = task.ManualVerificationAttempts.Single(item => item.Status == ManualVerificationAttemptStatus.CompletedFailed);
        return new GenerateFailureAnalysisCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            attempt.AttemptId, attempt.AttemptFingerprint!);
    }

    private sealed class NoopObserver : IVerificationGenerationObserver
    {
        public Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

internal static class CorrectionWorkflowTestExtensions
{
    internal static FailureAnalysisContext CreateAnalysisContextAfterBegin(
        EngineeringTask task, DateTimeOffset now, out GenerateFailureAnalysisCommand command)
    {
        var attempt = task.ManualVerificationAttempts.Single(item => item.Status == ManualVerificationAttemptStatus.CompletedFailed);
        command = new GenerateFailureAnalysisCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            attempt.AttemptId, attempt.AttemptFingerprint!);
        task.BeginFailureAnalysis(command, now);
        return CorrectionWorkflowService.CreateAnalysisContext(task, now);
    }
}
