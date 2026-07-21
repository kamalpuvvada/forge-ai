using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class VerificationWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly VerificationLimits Limits = new();

    [Fact]
    public async Task Approved_implementation_transitions_to_manual_verification_without_execution()
    {
        var task = ApprovedImplementation();
        var (plan, command) = await GeneratePlan(task);

        Assert.Equal(WorkflowStatus.AwaitingManualVerification, task.Status);
        Assert.Equal(plan.PlanId, task.CurrentVerificationPlanId);
        Assert.Equal(VerificationPlanSource.DeterministicFake, plan.Source);
        Assert.Contains("mechanical", plan.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.All(plan.TestCases.SelectMany(testCase => testCase.OrderedSteps),
            step => Assert.Contains("manually", step.Instruction, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(VerificationGenerationAttemptStatus.Completed,
            task.VerificationPlanGenerationAttempts.Single(attempt => attempt.CommandId == command.CommandId).Status);
    }

    [Fact]
    public async Task Required_cases_and_explicit_confirmation_gate_pass_completion()
    {
        var task = ApprovedImplementation();
        var (plan, _) = await GeneratePlan(task);
        var attempt = StartAttempt(task, plan);

        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCompletion(
            Complete(task, plan, attempt, passed: true, confirmed: false), attempt, plan, Limits));
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCompletion(
            Complete(task, plan, attempt, passed: true, confirmed: true), attempt, plan, Limits));

        foreach (var testCase in plan.TestCases.Where(item => item.IsRequired))
        {
            var result = VerificationValidator.CreateCaseResult(Update(task, plan, attempt, testCase,
                ManualVerificationCaseResult.Passed), attempt, plan, Now.AddMinutes(4), Limits);
            task.AppendManualCaseResult(result, Now.AddMinutes(4));
            attempt = task.ManualVerificationAttempts.Single(item => item.AttemptId == attempt.AttemptId);
        }
        var complete = Complete(task, plan, attempt, passed: true, confirmed: true);
        VerificationValidator.ValidateCompletion(complete, attempt, plan, Limits);
        task.CompleteManualVerification(attempt.AttemptId, complete.CommandId, true, true, "Accepted manually.", Now.AddMinutes(5));

        Assert.Equal(WorkflowStatus.ReadyForDelivery, task.Status);
        var completed = task.ManualVerificationAttempts.Single();
        Assert.Equal(ManualVerificationAttemptStatus.CompletedPassed, completed.Status);
        Assert.Matches("^[0-9a-f]{64}$", completed.AttemptFingerprint!);
    }

    [Theory]
    [InlineData(ManualVerificationCaseResult.NotApplicable)]
    [InlineData(ManualVerificationCaseResult.Blocked)]
    [InlineData(ManualVerificationCaseResult.Failed)]
    [InlineData(ManualVerificationCaseResult.NotStarted)]
    public async Task Regression_case_must_be_exactly_passed_for_successful_completion(
        ManualVerificationCaseResult regressionResult)
    {
        var task = ApprovedImplementation();
        var (original, _) = await GeneratePlan(task);
        var target = original.TestCases[0];
        var plan = original with
        {
            TestCases = original.TestCases.Select(item => item.TestCaseId == target.TestCaseId
                ? item with { OriginTestCaseId = Guid.NewGuid(), RegressionFailureReportIds = [Guid.NewGuid()] }
                : item).ToArray()
        };
        var attempt = StartAttempt(task, original) with { VerificationPlanId = plan.PlanId };
        var revisions = new List<ManualCaseResultRevision>();
        foreach (var testCase in plan.TestCases.Where(item => item.IsRequired))
        {
            var result = testCase.TestCaseId == target.TestCaseId ? regressionResult : ManualVerificationCaseResult.Passed;
            if (result == ManualVerificationCaseResult.NotStarted) continue;
            var failure = result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked
                ? new VerificationFailureDetails("Regression mismatch", testCase.ExpectedResult, "Observed mismatch",
                    ["Repeat manually."], ["Disposable test environment."], null, [], VerificationFailureSeverity.High)
                : null;
            revisions.Add(new ManualCaseResultRevision(Guid.NewGuid(), 1, attempt.AttemptId, testCase.TestCaseId,
                result, Now.AddMinutes(4), null, null, [],
                result == ManualVerificationCaseResult.NotApplicable ? "Not used." : null,
                failure, null, Guid.NewGuid()));
        }
        attempt = attempt with { ResultRevisions = revisions };

        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCompletion(
            Complete(task, plan, attempt, true, true), attempt, plan, Limits));
    }

    [Fact]
    public async Task Passed_regression_case_allows_successful_completion()
    {
        var task = ApprovedImplementation();
        var (original, _) = await GeneratePlan(task);
        var target = original.TestCases[0];
        var plan = original with
        {
            TestCases = original.TestCases.Select(item => item.TestCaseId == target.TestCaseId
                ? item with { OriginTestCaseId = Guid.NewGuid(), RegressionFailureReportIds = [Guid.NewGuid()] }
                : item).ToArray()
        };
        var attempt = StartAttempt(task, original);
        var revisions = plan.TestCases.Where(item => item.IsRequired).Select(testCase =>
            new ManualCaseResultRevision(Guid.NewGuid(), 1, attempt.AttemptId, testCase.TestCaseId,
                ManualVerificationCaseResult.Passed, Now.AddMinutes(4), null, null,
                testCase.EvidenceRequirements.Count > 0 ? ["Safe user-reported evidence."] : [], null, null, null,
                Guid.NewGuid())).ToArray();
        attempt = attempt with { ResultRevisions = revisions };

        VerificationValidator.ValidateCompletion(Complete(task, plan, attempt, true, true), attempt, plan, Limits);
    }

    [Fact]
    public async Task Failed_result_requires_details_and_completes_only_as_failed()
    {
        var task = ApprovedImplementation();
        var (plan, _) = await GeneratePlan(task);
        var attempt = StartAttempt(task, plan);
        var testCase = plan.TestCases[0];
        Assert.Throws<VerificationException>(() => VerificationValidator.CreateCaseResult(
            Update(task, plan, attempt, testCase, ManualVerificationCaseResult.Failed), attempt, plan, Now, Limits));

        var failure = new VerificationFailureDetails("Mismatch", testCase.ExpectedResult, "Observed mismatch",
            ["Perform the manual step."], ["Disposable test environment"], "safe error", [], VerificationFailureSeverity.Medium);
        var failedCommand = Update(task, plan, attempt, testCase, ManualVerificationCaseResult.Failed) with
            { FailureDetails = failure, ActualResult = failure.ActualResult };
        var result = VerificationValidator.CreateCaseResult(failedCommand, attempt, plan, Now.AddMinutes(4), Limits);
        task.AppendManualCaseResult(result, Now.AddMinutes(4));
        attempt = task.ManualVerificationAttempts.Single();
        var complete = Complete(task, plan, attempt, passed: false, confirmed: true);
        VerificationValidator.ValidateCompletion(complete, attempt, plan, Limits);
        task.CompleteManualVerification(attempt.AttemptId, complete.CommandId, false, true, "Failure confirmed.", Now.AddMinutes(5));

        Assert.Equal(WorkflowStatus.ManualVerificationFailed, task.Status);
        Assert.Equal(ManualVerificationAttemptStatus.CompletedFailed, task.ManualVerificationAttempts.Single().Status);
    }

    [Fact]
    public async Task Case_updates_are_append_only_and_supersede_the_previous_revision()
    {
        var task = ApprovedImplementation();
        var (plan, _) = await GeneratePlan(task);
        var attempt = StartAttempt(task, plan);
        var testCase = plan.TestCases[0];
        var first = VerificationValidator.CreateCaseResult(Update(task, plan, attempt, testCase,
            ManualVerificationCaseResult.Passed), attempt, plan, Now.AddMinutes(4), Limits);
        task.AppendManualCaseResult(first, Now.AddMinutes(4));
        attempt = task.ManualVerificationAttempts.Single();
        var second = VerificationValidator.CreateCaseResult(Update(task, plan, attempt, testCase,
            ManualVerificationCaseResult.NotApplicable) with { NotApplicableReason = "Not used in this configuration." },
            attempt, plan, Now.AddMinutes(5), Limits);
        task.AppendManualCaseResult(second, Now.AddMinutes(5));

        var revisions = task.ManualVerificationAttempts.Single().ResultRevisions;
        Assert.Equal(2, revisions.Count);
        Assert.Equal(first.ResultRevisionId, second.SupersedesResultRevisionId);
        Assert.Equal(2, second.RevisionNumber);
        Assert.Equal(second.ResultRevisionId,
            VerificationFingerprint.CurrentResults(task.ManualVerificationAttempts.Single()).Single().ResultRevisionId);
    }

    [Fact]
    public async Task Not_applicable_requires_a_reason_and_secrets_are_rejected()
    {
        var task = ApprovedImplementation();
        var (plan, _) = await GeneratePlan(task);
        var attempt = StartAttempt(task, plan);
        var testCase = plan.TestCases[0];
        Assert.Throws<VerificationException>(() => VerificationValidator.CreateCaseResult(
            Update(task, plan, attempt, testCase, ManualVerificationCaseResult.NotApplicable),
            attempt, plan, Now, Limits));
        Assert.Throws<VerificationException>(() => VerificationValidator.CreateCaseResult(
            Update(task, plan, attempt, testCase, ManualVerificationCaseResult.Passed) with
                { Notes = SyntheticSensitiveValues.BearerAuthorization() },
            attempt, plan, Now, Limits));
    }

    [Fact]
    public async Task Jwt_and_signed_credentials_are_rejected_from_every_manual_result_surface()
    {
        var jwt = SyntheticSensitiveValues.Jwt();
        var sas = SyntheticSensitiveValues.SasQuery();
        var task = ApprovedImplementation();
        var (plan, _) = await GeneratePlan(task);
        var attempt = StartAttempt(task, plan);
        var testCase = plan.TestCases[0];
        var safe = Update(task, plan, attempt, testCase, ManualVerificationCaseResult.Passed);
        var commands = new[]
        {
            safe with { Notes = jwt },
            safe with { ActualResult = sas },
            safe with { EvidenceDescriptions = [jwt] },
            Update(task, plan, attempt, testCase, ManualVerificationCaseResult.Failed) with
            {
                FailureDetails = new VerificationFailureDetails("Mismatch", "Expected", "Actual",
                    ["Repeat manually."], ["Disposable environment."], sas, [], VerificationFailureSeverity.High)
            }
        };

        Assert.All(commands, command => Assert.Throws<VerificationException>(() =>
            VerificationValidator.CreateCaseResult(command, attempt, plan, Now, Limits)));
    }

    [Fact]
    public async Task Completed_attempt_is_immutable()
    {
        var task = ApprovedImplementation();
        var (plan, _) = await GeneratePlan(task);
        var attempt = StartAttempt(task, plan);
        foreach (var testCase in plan.TestCases.Where(item => item.IsRequired))
        {
            var result = VerificationValidator.CreateCaseResult(Update(task, plan, attempt, testCase,
                ManualVerificationCaseResult.Passed), attempt, plan, Now.AddMinutes(4), Limits);
            task.AppendManualCaseResult(result, Now.AddMinutes(4));
            attempt = task.ManualVerificationAttempts.Single();
        }
        var complete = Complete(task, plan, attempt, true, true);
        task.CompleteManualVerification(attempt.AttemptId, complete.CommandId, true, true, null, Now.AddMinutes(5));
        Assert.Throws<WorkflowException>(() => task.AppendManualCaseResult(
            task.ManualVerificationAttempts.Single().ResultRevisions[0] with { ResultRevisionId = Guid.NewGuid() }, Now.AddMinutes(6)));
    }

    [Fact]
    public void Prepared_attempt_may_expire_before_dispatch_but_dispatch_intent_never_expires_into_retry()
    {
        var prepared = ApprovedImplementation();
        var first = GenerationCommand(prepared);
        prepared.BeginVerificationPlanGeneration(first, Now);
        prepared.BeginVerificationPlanGeneration(GenerationCommand(prepared), Now.AddMinutes(6));
        Assert.Equal(VerificationGenerationAttemptStatus.InterruptedBeforeDispatch,
            prepared.VerificationPlanGenerationAttempts[0].Status);

        var dispatched = ApprovedImplementation();
        var dispatchedCommand = GenerationCommand(dispatched);
        dispatched.BeginVerificationPlanGeneration(dispatchedCommand, Now);
        dispatched.RecordVerificationGenerationCheckpoint(dispatchedCommand.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, Guid.NewGuid(), Now.AddSeconds(1));
        Assert.Throws<WorkflowException>(() => dispatched.BeginVerificationPlanGeneration(
            GenerationCommand(dispatched), Now.AddHours(1)));
        Assert.Equal(VerificationGenerationAttemptStatus.DispatchMayHaveStarted,
            dispatched.VerificationPlanGenerationAttempts.Single().Status);
    }

    [Fact]
    public void Response_received_and_ambiguous_phases_block_replay_and_new_commands_after_restart_window()
    {
        foreach (var finalCheckpoint in new[]
                 { VerificationDispatchCheckpoint.ResponseReceived, VerificationDispatchCheckpoint.AmbiguousAfterDispatch })
        {
            var task = ApprovedImplementation();
            var command = GenerationCommand(task);
            var physicalCall = Guid.NewGuid();
            task.BeginVerificationPlanGeneration(command, Now);
            task.RecordVerificationGenerationCheckpoint(command.CommandId,
                VerificationDispatchCheckpoint.DispatchMayHaveStarted, physicalCall, Now.AddSeconds(1));
            if (finalCheckpoint == VerificationDispatchCheckpoint.ResponseReceived)
                task.RecordVerificationProviderResponse(command.CommandId,
                    new VerificationProviderResponseTelemetry(physicalCall, Now.AddSeconds(1), Now.AddSeconds(2), "response-id",
                        "request-id", VerificationProviderResponseStatus.Completed, null, false,
                        null, null, null, null, 200, VerificationCallDispatchDisposition.ResponseReceived),
                    Now.AddSeconds(2));
            else
                task.RecordVerificationGenerationCheckpoint(command.CommandId, finalCheckpoint, physicalCall,
                    Now.AddSeconds(2));
            Assert.Throws<WorkflowException>(() => task.BeginVerificationPlanGeneration(command, Now.AddHours(1)));
            Assert.Throws<WorkflowException>(() => task.BeginVerificationPlanGeneration(
                GenerationCommand(task), Now.AddHours(1)));
        }
    }

    [Theory]
    [InlineData(VerificationDispatchCheckpoint.FailedBeforeDispatch)]
    [InlineData(VerificationDispatchCheckpoint.RetryableProviderResponse)]
    public void Only_durably_safe_terminal_provider_phases_allow_an_explicit_new_command(
        VerificationDispatchCheckpoint checkpoint)
    {
        var task = ApprovedImplementation();
        var first = GenerationCommand(task);
        var physicalCall = Guid.NewGuid();
        task.BeginVerificationPlanGeneration(first, Now);
        task.RecordVerificationGenerationCheckpoint(first.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, physicalCall, Now.AddSeconds(1));
        task.RecordVerificationGenerationCheckpoint(first.CommandId, checkpoint, physicalCall, Now.AddSeconds(2));

        task.BeginVerificationPlanGeneration(GenerationCommand(task), Now.AddSeconds(3));

        Assert.Equal(2, task.VerificationPlanGenerationAttempts.Count);
        Assert.Equal(VerificationGenerationAttemptStatus.Prepared, task.VerificationPlanGenerationAttempts[1].Status);
    }

    [Fact]
    public async Task Fake_plan_bounds_long_changed_file_sets_and_reports_every_omission()
    {
        var task = ApprovedImplementation();
        task.BeginVerificationPlanGeneration(GenerationCommand(task), Now);
        var original = VerificationWorkflowService.CreateContext(task, Now);
        var changed = Enumerable.Range(1, 12).Select(index =>
        {
            var path = $"docs/{new string((char)('a' + index), 250)}-{index}.md";
            return new ChangedFileReview(path,
                index % 3 == 0 ? ImplementationOperationAction.Create :
                index % 3 == 1 ? ImplementationOperationAction.Modify : ImplementationOperationAction.Delete,
                new string('3', 64), new string('4', 64), 10, 20, 1, 2, 1, 0,
                "bounded diff", 12, 12, index <= 2, 100, 12);
        }).ToArray();
        var affected = changed.Select(file => new PlannedFileChange(file.Path,
            file.Action switch
            {
                ImplementationOperationAction.Create => PlannedFileAction.Create,
                ImplementationOperationAction.Modify => PlannedFileAction.Modify,
                _ => PlannedFileAction.Delete
            }, "Approved bounded change.", [], .9m)).ToArray();
        var plan = original.ApprovedPlan with { AffectedFiles = affected, ProposedValidationCommands = [] };
        var context = original with
        {
            ApprovedPlan = plan,
            ImplementationResult = original.ImplementationResult with
            { ChangedFiles = changed, DiffTruncated = true },
            ApprovedValidationCommands = [],
            RepositoryEvidenceFilesInspected = 7,
            RepositoryEvidenceFilesSelected = 1
        };
        context = context with { ContextFingerprint = VerificationFingerprint.ComputeContext(context with
            { ContextFingerprint = string.Empty }) };

        var candidate = (await new FakeVerificationPlanEngine().GenerateAsync(context)).Candidate;
        VerificationValidator.ValidateCandidate(context, candidate, Limits);

        Assert.All(candidate.TestCases.SelectMany(item => item.OrderedSteps), step =>
            Assert.True(step.Instruction.Length <= Limits.MaximumListItemCharacters));
        Assert.Contains(candidate.Limitations, item => item.Contains("3 approved changed file(s)", StringComparison.Ordinal));
        Assert.Contains(candidate.Limitations, item => item.Contains("2 approved changed file(s)", StringComparison.Ordinal));
        Assert.Contains(candidate.Limitations, item => item.Contains("6 inspected repository file(s)", StringComparison.Ordinal));
        Assert.Contains(candidate.TestCases.SelectMany(item => item.OrderedSteps), step =>
            step.Instruction.Contains("omitted", StringComparison.Ordinal));
    }

    private static async Task<(VerificationPlan Plan, VerificationPlanGenerationCommand Command)> GeneratePlan(EngineeringTask task)
    {
        var revision = task.ImplementationRevisions.Single(item => item.IsApprovedFor(task));
        var command = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        task.BeginVerificationPlanGeneration(command, Now.AddMinutes(3));
        var context = VerificationWorkflowService.CreateContext(task, Now.AddMinutes(3));
        var candidate = (await new FakeVerificationPlanEngine().GenerateAsync(context)).Candidate;
        var plan = VerificationValidator.FinalizeCandidate(context, candidate, 1, Guid.NewGuid(), [], Limits);
        task.StoreVerificationPlan(command.CommandId, plan, Now.AddMinutes(3));
        return (plan, command);
    }

    private static VerificationPlanGenerationCommand GenerationCommand(EngineeringTask task)
    {
        var revision = task.ImplementationRevisions.Single(item => item.IsApprovedFor(task));
        return new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
    }

    private static ManualVerificationAttempt StartAttempt(EngineeringTask task, VerificationPlan plan)
    {
        var attempt = new ManualVerificationAttempt(Guid.NewGuid(), 1, plan.PlanId, plan.PlanFingerprint,
            plan.ImplementationRevisionId, plan.ImplementationResultFingerprint, Now.AddMinutes(3), null,
            ManualVerificationAttemptStatus.InProgress, [], null, null, null, null, null, Guid.NewGuid(), null);
        task.StartManualVerification(attempt, Now.AddMinutes(3));
        return attempt;
    }

    private static UpdateManualVerificationCaseCommand Update(EngineeringTask task, VerificationPlan plan,
        ManualVerificationAttempt attempt, VerificationTestCase testCase, ManualVerificationCaseResult result) => new(
        Guid.NewGuid(), task.Id, attempt.AttemptId, testCase.TestCaseId, task.RowVersion, plan.PlanId,
        plan.PlanFingerprint, plan.ImplementationRevisionId, plan.ImplementationResultFingerprint,
        result, null, null,
        result == ManualVerificationCaseResult.Passed && testCase.EvidenceRequirements.Count > 0
            ? ["Observed the expected manual outcome."]
            : [],
        null, null);

    private static CompleteManualVerificationCommand Complete(EngineeringTask task, VerificationPlan plan,
        ManualVerificationAttempt attempt, bool passed, bool confirmed) => new(
        Guid.NewGuid(), task.Id, attempt.AttemptId, task.RowVersion, plan.PlanId, plan.PlanFingerprint,
        plan.ImplementationRevisionId, plan.ImplementationResultFingerprint, confirmed, null, passed);

    internal static EngineeringTask ApprovedImplementation(bool approve = true)
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now) with { IsGitRepository = true, Branch = "main", ShortHeadSha = "aaaaaaaa", FullHeadSha = new string('a', 40), WorkingTreeStatus = "clean" };
        var evidence = PlanningWorkflowTests.Evidence();
        task.BeginRepositoryAnalysis(Now); task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), Now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(Now);
        var workspace = new ImplementationWorkspace(new string('a', 32), "forge/task-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('a', 40), ImplementationWorkspacePhase.Reserved, Now, Now, false,
            new string('1', 64), new string('2', 64), $"refs/forge/tasks/{new string('a', 32)}");
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, Now, Now.AddMinutes(5));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        task.StoreImplementationResult(new ImplementationResult(ImplementationSource.DeterministicFake, null,
            workspace.BaseCommitSha, workspace.Branch, "Summary", ["Mechanical"],
            [new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify, new string('3', 64),
                new string('4', 64), 10, 20, 1, 2, 1, 0,
                "diff --git a/src/App.cs b/src/App.cs", 36, 36, false, 36, 36)],
            36, 36, false, Now.AddMinutes(2), 36, 36, ActiveCheckoutVerified: true),
            lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        var revision = task.ImplementationRevisions.Single();
        if (approve)
            task.ApproveImplementation(Guid.NewGuid(), task.RowVersion, revision.RevisionId,
                revision.ResultFingerprint!, Now.AddMinutes(2));
        return task;
    }
}

internal static class VerificationRevisionTestExtensions
{
    internal static bool IsApprovedFor(this ImplementationRevision revision, EngineeringTask task) =>
        task.ApprovedImplementationRevisionId == revision.RevisionId;
}
