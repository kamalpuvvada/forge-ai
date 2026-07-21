using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class CorrectionDurabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"forge-correction-durability-{Guid.NewGuid():N}");
    private string ConnectionString => $"Data Source={Path.Combine(root, "forge.db")};Pooling=False";
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Definitely_undispatched_failure_is_restart_safe_and_same_command_never_redispatches()
    {
        var (repository, task) = await RepositoryWithFailedTaskAsync();
        var command = CorrectionWorkflowTests.AnalysisCommand(task);
        await repository.BeginFailureAnalysisAsync(command, Now);
        await repository.FailFailureAnalysisAsync(task.Id, command.CommandId, "failure_analysis_configuration",
            "Failure analysis is not configured.", [], FailureAnalysisStatus.FailedBeforeDispatch, Now.AddSeconds(1));

        var restarted = new SqliteEngineeringTaskRepository(ConnectionString);
        var loaded = (await restarted.GetAsync(task.Id))!;
        Assert.Equal(WorkflowStatus.ManualVerificationFailed, loaded.Status);
        Assert.Equal(FailureAnalysisAttemptStatus.FailedBeforeDispatch,
            Assert.Single(loaded.FailureAnalysisGenerationAttempts).Status);
        Assert.True((await restarted.BeginFailureAnalysisAsync(command, Now.AddMinutes(1))).Replayed);
        var explicitRetry = CorrectionWorkflowTests.AnalysisCommand(loaded);
        Assert.False((await restarted.BeginFailureAnalysisAsync(explicitRetry, Now.AddMinutes(2))).Replayed);
    }

    [Fact]
    public async Task Ambiguous_dispatch_survives_restart_and_blocks_every_redispatch()
    {
        var (repository, task) = await RepositoryWithFailedTaskAsync();
        var command = CorrectionWorkflowTests.AnalysisCommand(task);
        var callId = Guid.NewGuid();
        await repository.BeginFailureAnalysisAsync(command, Now);
        await repository.RecordFailureAnalysisCheckpointAsync(task.Id, command.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, callId, Now.AddSeconds(1), Now.AddSeconds(1));
        await repository.FailFailureAnalysisAsync(task.Id, command.CommandId, "failure_analysis_timeout",
            "The request may have been dispatched.", [], FailureAnalysisStatus.AmbiguousAfterDispatch, Now.AddSeconds(2));

        var restarted = new SqliteEngineeringTaskRepository(ConnectionString);
        var loaded = (await restarted.GetAsync(task.Id))!;
        Assert.Equal(WorkflowStatus.FailureAnalysisRecoveryRequired, loaded.Status);
        Assert.Equal(FailureAnalysisAttemptStatus.AmbiguousAfterDispatch,
            Assert.Single(loaded.FailureAnalysisGenerationAttempts).Status);
        Assert.False(loaded.FailureAnalysisGenerationAttempts[0].RetryEligible);
        Assert.True(loaded.FailureAnalysisGenerationAttempts[0].RecoveryRequired);
        Assert.True((await restarted.BeginFailureAnalysisAsync(command, Now.AddMinutes(1))).Replayed);
        var different = command with { CommandId = Guid.NewGuid() };
        await Assert.ThrowsAsync<WorkflowException>(() => restarted.BeginFailureAnalysisAsync(different, Now.AddMinutes(2)));
    }

    [Fact]
    public async Task Expired_pre_dispatch_failure_analysis_reconciles_once_and_allows_explicit_new_command()
    {
        var (repository, task) = await RepositoryWithFailedTaskAsync();
        var generation = CorrectionWorkflowTests.AnalysisCommand(task);
        var begun = await repository.BeginFailureAnalysisAsync(generation, Now);
        var reconcile = new ReconcileFailureAnalysisCommand(Guid.NewGuid(), task.Id,
            begun.Task.RowVersion, generation.CommandId);

        var recovered = await repository.ReconcileFailureAnalysisAsync(reconcile, Now.AddMinutes(6));

        Assert.Equal(WorkflowStatus.ManualVerificationFailed, recovered.Status);
        var attempt = Assert.Single(recovered.FailureAnalysisGenerationAttempts);
        Assert.Equal(FailureAnalysisAttemptStatus.ExpiredBeforeDispatch, attempt.Status);
        Assert.True(attempt.RetryEligible);
        Assert.False(attempt.RecoveryRequired);
        Assert.Equal(recovered.RowVersion, (await repository.ReconcileFailureAnalysisAsync(
            reconcile, Now.AddMinutes(7))).RowVersion);
        var retry = CorrectionWorkflowTests.AnalysisCommand(recovered);
        Assert.False((await repository.BeginFailureAnalysisAsync(retry, Now.AddMinutes(8))).Replayed);
    }

    [Fact]
    public async Task Configured_generation_lease_is_authoritative_for_failure_analysis_and_correction_claims()
    {
        var (_, failed) = await RepositoryWithFailedTaskAsync();
        var limits = new CorrectionLimits { GenerationLeaseSeconds = 7 };
        var repository = new SqliteEngineeringTaskRepository(ConnectionString, correctionLimits: limits);
        var command = CorrectionWorkflowTests.AnalysisCommand(failed);
        var begun = await repository.BeginFailureAnalysisAsync(command, Now);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(begun.Task.FailureAnalysisGenerationAttempts)
            .LeaseExpiresAt - Now);

        var context = CorrectionWorkflowService.CreateAnalysisContext(begun.Task, Now);
        var candidate = (await new FakeFailureAnalysisEngine().GenerateAsync(context, new NoopObserver())).Candidate with
        {
            Classification = FailureClassification.ImplementationDefect,
            AffectedApprovedOperations = [context.ApprovedOperations[0]],
            CorrectionStrategy = "Adjust the exact approved operation without expanding scope."
        };
        var analysis = CorrectionValidator.FinalizeAnalysis(context, candidate, 1, Guid.NewGuid(), command.CommandId,
            [], limits);
        var previous = begun.Task.ImplementationRevisions[0];
        var proposal = CorrectionValidator.CreateProposal(begun.Task.Id, analysis, previous, 1,
            Now.AddSeconds(1), limits);
        var analyzed = await repository.CompleteFailureAnalysisAsync(begun.Task.Id, command.CommandId,
            analysis, proposal, [], Now.AddSeconds(1));
        var approved = await repository.ApproveCorrectionProposalAsync(new ApproveCorrectionProposalCommand(
            Guid.NewGuid(), analyzed.Id, analyzed.RowVersion, proposal.ProposalId, proposal.ProposalFingerprint,
            analysis.AnalysisId, analysis.AnalysisFingerprint, proposal.FailedAttemptId,
            proposal.FailedAttemptFingerprint, previous.RevisionId, previous.ResultFingerprint!,
            proposal.ApprovedRequirementFingerprint, proposal.ApprovedPlanFingerprint,
            proposal.OriginalBaseCommitSha), Now.AddSeconds(2));
        var correction = await repository.BeginCorrectionGenerationAsync(new GenerateCorrectionCommand(
            Guid.NewGuid(), approved.Id, approved.RowVersion, proposal.ProposalId, proposal.ProposalFingerprint,
            previous.RevisionId, previous.ResultFingerprint!), Now.AddSeconds(3));
        var correctionAttempt = Assert.Single(correction.Task.CorrectionGenerationAttempts);
        Assert.Equal(TimeSpan.FromSeconds(7), correctionAttempt.LeaseExpiresAt - correctionAttempt.StartedAt);
    }

    [Theory]
    [InlineData(false, FailureAnalysisAttemptStatus.AmbiguousAfterDispatch)]
    [InlineData(true, FailureAnalysisAttemptStatus.InterruptedAfterResponse)]
    public async Task Expired_dispatched_failure_analysis_reconciles_to_safe_recovery(
        bool responseReceived, FailureAnalysisAttemptStatus expected)
    {
        var (repository, task) = await RepositoryWithFailedTaskAsync();
        var generation = CorrectionWorkflowTests.AnalysisCommand(task);
        var begun = await repository.BeginFailureAnalysisAsync(generation, Now);
        var callId = Guid.NewGuid();
        await repository.RecordFailureAnalysisCheckpointAsync(task.Id, generation.CommandId,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted, callId, Now.AddSeconds(1), Now.AddSeconds(1));
        if (responseReceived)
            await repository.RecordFailureAnalysisResponseAsync(task.Id, generation.CommandId,
                Response(callId), Now.AddSeconds(2));
        var active = (await repository.GetAsync(task.Id))!;

        var recovered = await repository.ReconcileFailureAnalysisAsync(new ReconcileFailureAnalysisCommand(
            Guid.NewGuid(), task.Id, active.RowVersion, generation.CommandId), Now.AddMinutes(6));

        Assert.Equal(WorkflowStatus.FailureAnalysisRecoveryRequired, recovered.Status);
        var attempt = Assert.Single(recovered.FailureAnalysisGenerationAttempts);
        Assert.Equal(expected, attempt.Status);
        Assert.True(attempt.RecoveryRequired);
        Assert.False(attempt.RetryEligible);
        Assert.Equal(responseReceived ? 1 : 0, attempt.PhysicalRequestCount);
        Assert.Equal(responseReceived ? 0 : 1, attempt.PossiblyDispatchedRequestCount);
    }

    [Fact]
    public async Task Expired_pre_dispatch_correction_reconciles_to_revision_one_and_allows_new_command()
    {
        var (repository, approved) = await RepositoryWithApprovedCorrectionAsync();
        var proposal = Assert.Single(approved.CorrectionProposals);
        var previous = approved.ImplementationRevisions[0];
        var generation = new GenerateCorrectionCommand(Guid.NewGuid(), approved.Id, approved.RowVersion,
            proposal.ProposalId, proposal.ProposalFingerprint, previous.RevisionId, previous.ResultFingerprint!);
        var begun = await repository.BeginCorrectionGenerationAsync(generation, Now);
        var attempt = Assert.Single(begun.Task.CorrectionGenerationAttempts);
        var reconcile = new ReconcileCorrectionCommand(Guid.NewGuid(), approved.Id, begun.Task.RowVersion,
            attempt.AttemptId, proposal.ProposalId, proposal.ProposalFingerprint, previous.RevisionId,
            previous.ResultFingerprint!, attempt.RevisionId!.Value);

        var recovered = await repository.ReconcileCorrectionGenerationAsync(reconcile, Now.AddMinutes(6));

        Assert.Equal(WorkflowStatus.CorrectionApproved, recovered.Status);
        Assert.Single(recovered.ImplementationRevisions);
        Assert.Equal(previous.RevisionId, recovered.ActiveImplementationRevisionId);
        Assert.True(Assert.Single(recovered.CorrectionGenerationAttempts).RetryEligible);
        Assert.Equal(recovered.RowVersion, (await repository.ReconcileCorrectionGenerationAsync(
            reconcile, Now.AddMinutes(7))).RowVersion);
    }

    [Theory]
    [InlineData(CorrectionGenerationAttemptStatus.DispatchMayHaveStarted)]
    [InlineData(CorrectionGenerationAttemptStatus.ResponseReceived)]
    [InlineData(CorrectionGenerationAttemptStatus.OutputAccepted)]
    [InlineData(CorrectionGenerationAttemptStatus.CheckoutVerified)]
    [InlineData(CorrectionGenerationAttemptStatus.RevisionReserved)]
    [InlineData(CorrectionGenerationAttemptStatus.WorkspacePreparing)]
    [InlineData(CorrectionGenerationAttemptStatus.WorkspacePrepared)]
    [InlineData(CorrectionGenerationAttemptStatus.MutationStarted)]
    [InlineData(CorrectionGenerationAttemptStatus.ApplyCompleted)]
    public async Task Expired_post_dispatch_correction_phase_never_redispatches_or_retries(
        CorrectionGenerationAttemptStatus phase)
    {
        var (repository, approved) = await RepositoryWithApprovedCorrectionAsync();
        var proposal = Assert.Single(approved.CorrectionProposals);
        var previous = approved.ImplementationRevisions[0];
        var generation = new GenerateCorrectionCommand(Guid.NewGuid(), approved.Id, approved.RowVersion,
            proposal.ProposalId, proposal.ProposalFingerprint, previous.RevisionId, previous.ResultFingerprint!);
        var begun = await repository.BeginCorrectionGenerationAsync(generation, Now);
        var attempt = Assert.Single(begun.Task.CorrectionGenerationAttempts);
        var callId = Guid.NewGuid();
        await repository.RecordCorrectionCheckpointAsync(approved.Id, generation.CommandId,
            CorrectionGenerationAttemptStatus.DispatchMayHaveStarted, callId, Now.AddSeconds(1), Now.AddSeconds(1));
        if (phase >= CorrectionGenerationAttemptStatus.ResponseReceived)
            await repository.RecordCorrectionResponseAsync(approved.Id, generation.CommandId,
                Response(callId), Now.AddSeconds(2));
        if (phase >= CorrectionGenerationAttemptStatus.OutputAccepted)
            await repository.RecordCorrectionOutputAcceptedAsync(approved.Id, generation.CommandId,
                new string('a', 64), Now.AddSeconds(3));
        foreach (var checkpoint in new[] { CorrectionGenerationAttemptStatus.CheckoutVerified,
                     CorrectionGenerationAttemptStatus.RevisionReserved, CorrectionGenerationAttemptStatus.WorkspacePreparing,
                     CorrectionGenerationAttemptStatus.WorkspacePrepared, CorrectionGenerationAttemptStatus.MutationStarted,
                     CorrectionGenerationAttemptStatus.ApplyCompleted }.Where(item => item <= phase))
            await repository.RecordCorrectionCheckpointAsync(approved.Id, generation.CommandId,
                checkpoint, null, Now.AddSeconds(4 + (int)checkpoint));
        var active = (await repository.GetAsync(approved.Id))!;

        var recovered = await repository.ReconcileCorrectionGenerationAsync(new ReconcileCorrectionCommand(
            Guid.NewGuid(), approved.Id, active.RowVersion, attempt.AttemptId, proposal.ProposalId,
            proposal.ProposalFingerprint, previous.RevisionId, previous.ResultFingerprint!, attempt.RevisionId!.Value),
            Now.AddMinutes(6));

        Assert.Equal(WorkflowStatus.CorrectionRecoveryRequired, recovered.Status);
        Assert.Equal(previous.RevisionId, recovered.ActiveImplementationRevisionId);
        Assert.Equal(previous.RevisionId, recovered.ApprovedImplementationRevisionId);
        Assert.Equal(attempt.RevisionId, recovered.PendingImplementationRevisionId);
        var terminal = Assert.Single(recovered.CorrectionGenerationAttempts);
        Assert.True(terminal.RecoveryRequired);
        Assert.False(terminal.RetryEligible);
    }

    [Fact]
    public async Task Expired_result_persisted_correction_reconciles_to_completed_revision_two_without_external_work()
    {
        var (repository, approved) = await RepositoryWithApprovedCorrectionAsync();
        var proposal = Assert.Single(approved.CorrectionProposals);
        var previous = approved.ImplementationRevisions[0];
        var generation = new GenerateCorrectionCommand(Guid.NewGuid(), approved.Id, approved.RowVersion,
            proposal.ProposalId, proposal.ProposalFingerprint, previous.RevisionId, previous.ResultFingerprint!);
        var begun = await repository.BeginCorrectionGenerationAsync(generation, Now);
        var attempt = Assert.Single(begun.Task.CorrectionGenerationAttempts);
        await repository.RecordCorrectionOutputAcceptedAsync(approved.Id, generation.CommandId,
            new string('a', 64), Now.AddSeconds(1));
        var active = (await repository.GetAsync(approved.Id))!;
        var taskToken = approved.Id.ToString("N");
        var workspace = previous.Workspace! with
        {
            Token = new string('b', 32),
            Branch = $"forge/task-{taskToken}-revision-2",
            Phase = ImplementationWorkspacePhase.Reserved,
            CreatedAt = Now.AddSeconds(2),
            UpdatedAt = Now.AddSeconds(2),
            IsAvailable = true,
            OwnershipReference = $"refs/forge/tasks/{taskToken}-revision-2",
            RevisionNumber = 2,
            TaskToken = taskToken
        };
        var ownerId = Guid.NewGuid();
        var lease = new ImplementationLease(Guid.NewGuid(), generation.CommandId, ownerId,
            Now.AddSeconds(2), Now.AddSeconds(2), Now.AddMinutes(5).AddSeconds(2), 300);
        active.BeginCorrection(generation, workspace, lease, Now.AddSeconds(2));
        await repository.PersistCorrectionPhaseAsync(active, generation.CommandId,
            CorrectionGenerationAttemptStatus.WorkspacePreparing, Now.AddSeconds(2));
        active = (await repository.GetAsync(approved.Id))!;
        var result = previous.Result! with
        {
            Branch = workspace.Branch,
            Summary = "The bounded correction result was durably persisted for review.",
            CompletedAt = Now.AddSeconds(3)
        };
        active.StoreImplementationResult(result, generation.CommandId, ownerId, Now.AddSeconds(3));
        await repository.PersistCorrectionPhaseAsync(active, generation.CommandId,
            CorrectionGenerationAttemptStatus.ResultPersisted, Now.AddSeconds(3));
        var persisted = (await repository.GetAsync(approved.Id))!;

        Assert.Equal(WorkflowStatus.ImplementingCorrection, persisted.Status);
        Assert.Equal(previous.RevisionId, persisted.ActiveImplementationRevisionId);
        Assert.Equal(previous.RevisionId, persisted.ApprovedImplementationRevisionId);
        Assert.Equal(attempt.RevisionId, persisted.PendingImplementationRevisionId);
        Assert.Equal(CorrectionGenerationAttemptStatus.ResultPersisted,
            Assert.Single(persisted.CorrectionGenerationAttempts).Status);
        Assert.NotNull(persisted.ImplementationRevisions.Single(item => item.RevisionNumber == 2).Result);
        var reconcile = new ReconcileCorrectionCommand(Guid.NewGuid(), approved.Id, persisted.RowVersion,
            attempt.AttemptId, proposal.ProposalId, proposal.ProposalFingerprint, previous.RevisionId,
            previous.ResultFingerprint!, attempt.RevisionId!.Value);

        var recovered = await repository.ReconcileCorrectionGenerationAsync(reconcile, Now.AddMinutes(6));

        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, recovered.Status);
        Assert.Equal(attempt.RevisionId, recovered.ActiveImplementationRevisionId);
        Assert.Equal(previous.RevisionId, recovered.ApprovedImplementationRevisionId);
        Assert.Null(recovered.PendingImplementationRevisionId);
        Assert.Equal(CorrectionGenerationAttemptStatus.Completed,
            Assert.Single(recovered.CorrectionGenerationAttempts).Status);
        Assert.Equal(recovered.RowVersion, (await repository.ReconcileCorrectionGenerationAsync(
            reconcile, Now.AddMinutes(7))).RowVersion);
    }

    [Fact]
    public async Task Two_repository_instances_atomically_allow_only_one_correction_claim()
    {
        var (repository, failed) = await RepositoryWithFailedTaskAsync();
        var analysisCommand = CorrectionWorkflowTests.AnalysisCommand(failed);
        var begun = await repository.BeginFailureAnalysisAsync(analysisCommand, Now);
        var context = CorrectionWorkflowService.CreateAnalysisContext(begun.Task, Now);
        var generated = (await new FakeFailureAnalysisEngine().GenerateAsync(context, new NoopObserver())).Candidate;
        var candidate = generated with
        {
            Classification = FailureClassification.ImplementationDefect,
            AffectedApprovedOperations = [context.ApprovedOperations[0]],
            CorrectionStrategy = "Adjust the exact approved operation without expanding scope."
        };
        var analysis = CorrectionValidator.FinalizeAnalysis(context, candidate, 1, Guid.NewGuid(),
            analysisCommand.CommandId, [], new CorrectionLimits());
        var previous = begun.Task.ImplementationRevisions[0];
        var proposed = CorrectionValidator.CreateProposal(begun.Task.Id, analysis, previous, 1,
            Now.AddSeconds(1), new CorrectionLimits());
        var analyzed = await repository.CompleteFailureAnalysisAsync(begun.Task.Id, analysisCommand.CommandId,
            analysis, proposed, [], Now.AddSeconds(1));
        var loaded = await repository.ApproveCorrectionProposalAsync(new ApproveCorrectionProposalCommand(
            Guid.NewGuid(), analyzed.Id, analyzed.RowVersion, proposed.ProposalId, proposed.ProposalFingerprint,
            analysis.AnalysisId, analysis.AnalysisFingerprint, proposed.FailedAttemptId,
            proposed.FailedAttemptFingerprint, previous.RevisionId, previous.ResultFingerprint!,
            proposed.ApprovedRequirementFingerprint, proposed.ApprovedPlanFingerprint,
            proposed.OriginalBaseCommitSha), Now.AddSeconds(2));
        var proposal = Assert.Single(loaded.CorrectionProposals);
        var effectivePrevious = loaded.ImplementationRevisions[0];
        GenerateCorrectionCommand Command() => new(Guid.NewGuid(), loaded.Id, loaded.RowVersion,
            proposal.ProposalId, proposal.ProposalFingerprint, effectivePrevious.RevisionId, effectivePrevious.ResultFingerprint!);
        var firstRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var secondRepository = new SqliteEngineeringTaskRepository(ConnectionString);

        var outcomes = await Task.WhenAll(
            Capture(() => firstRepository.BeginCorrectionGenerationAsync(Command(), Now)),
            Capture(() => secondRepository.BeginCorrectionGenerationAsync(Command(), Now)));

        Assert.Single(outcomes, item => item.Result is not null);
        Assert.Single(outcomes, item => item.Error is CorrectionException or TaskConcurrencyException);
        var winner = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(loaded.Id))!;
        Assert.Equal(WorkflowStatus.ImplementingCorrection, winner.Status);
        Assert.Equal(2, winner.ImplementationRevisions.Count);
        Assert.Single(winner.CorrectionGenerationAttempts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("semantic")]
    [InlineData("proposal")]
    [InlineData("row-version")]
    [InlineData("timestamp")]
    [InlineData("duplicate")]
    public async Task Approval_command_ledger_corruption_fails_every_ordinary_read(string corruption)
    {
        var (repository, approved) = await RepositoryWithApprovedCorrectionAsync();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = corruption switch
        {
            "missing" => "DELETE FROM CorrectionApprovalCommands;",
            "semantic" => "UPDATE CorrectionApprovalCommands SET SemanticFingerprint=lower(hex(randomblob(32)));",
            "proposal" => $"UPDATE CorrectionApprovalCommands SET ProposalId='{Guid.NewGuid():D}';",
            "row-version" => "UPDATE CorrectionApprovalCommands SET CompletedRowVersion=ExpectedRowVersion+2;",
            "timestamp" => "UPDATE CorrectionApprovalCommands SET CompletedAt='2020-01-01T00:00:00.0000000+00:00';",
            "duplicate" => $"INSERT INTO CorrectionApprovalCommands(CommandId,TaskId,SemanticFingerprint,ProposalId,ProposalFingerprint,ExpectedRowVersion,CreatedAt,CompletedRowVersion,CompletedAt,Result) SELECT '{Guid.NewGuid():D}',TaskId,SemanticFingerprint,ProposalId,ProposalFingerprint,ExpectedRowVersion,CreatedAt,CompletedRowVersion,CompletedAt,Result FROM CorrectionApprovalCommands LIMIT 1;",
            _ => throw new InvalidOperationException()
        };
        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<TaskDataCorruptException>(() => repository.GetAsync(approved.Id));
    }

    private Guid seedId;

    private async Task<(SqliteEngineeringTaskRepository Repository, EngineeringTask Task)> RepositoryWithFailedTaskAsync()
    {
        Directory.CreateDirectory(root);
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
        await repository.SaveAsync(task);
        var initial = task.ImplementationRevisions.Single();
        task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, initial.RevisionId, initial.ResultFingerprint!), Now.AddMinutes(-5));
        var workflow = new VerificationWorkflowService(repository, new FakeVerificationPlanEngine(),
            new ImplementationOperationCoordinator(), new VerificationLimits(), new FixedTimeProvider(Now.AddMinutes(-4)));
        var approved = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        task = await workflow.GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, approved.RevisionId, approved.ResultFingerprint!));
        var plan = task.VerificationPlans.Single();
        task = await workflow.StartAttemptAsync(new StartManualVerificationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, plan.PlanId, plan.PlanFingerprint, approved.RevisionId, approved.ResultFingerprint!));
        var attempt = task.ManualVerificationAttempts.Single();
        var testCase = plan.TestCases[0];
        var failure = new VerificationFailureDetails("Observed mismatch", testCase.ExpectedResult,
            "The approved behavior was not observed.", ["Repeat the manual step."],
            ["Disposable test environment."], null, [], VerificationFailureSeverity.High);
        task = await workflow.UpdateCaseAsync(new UpdateManualVerificationCaseCommand(Guid.NewGuid(), task.Id,
            attempt.AttemptId, testCase.TestCaseId, task.RowVersion, plan.PlanId, plan.PlanFingerprint,
            approved.RevisionId, approved.ResultFingerprint!, ManualVerificationCaseResult.Failed, null,
            failure.ActualResult, [], null, failure));
        attempt = task.ManualVerificationAttempts.Single();
        task = await workflow.CompleteAttemptAsync(new CompleteManualVerificationCommand(Guid.NewGuid(), task.Id,
            attempt.AttemptId, task.RowVersion, plan.PlanId, plan.PlanFingerprint, approved.RevisionId,
            approved.ResultFingerprint!, true, "Failure confirmed.", false));
        seedId = task.Id;
        return (repository, task);
    }

    private async Task<(SqliteEngineeringTaskRepository Repository, EngineeringTask Task)> RepositoryWithApprovedCorrectionAsync()
    {
        var (repository, failed) = await RepositoryWithFailedTaskAsync();
        var command = CorrectionWorkflowTests.AnalysisCommand(failed);
        var begun = await repository.BeginFailureAnalysisAsync(command, Now);
        var context = CorrectionWorkflowService.CreateAnalysisContext(begun.Task, Now);
        var candidate = (await new FakeFailureAnalysisEngine().GenerateAsync(context, new NoopObserver())).Candidate with
        {
            Classification = FailureClassification.ImplementationDefect,
            AffectedApprovedOperations = [context.ApprovedOperations[0]],
            CorrectionStrategy = "Adjust the exact approved operation without expanding scope."
        };
        var analysis = CorrectionValidator.FinalizeAnalysis(context, candidate, 1, Guid.NewGuid(),
            command.CommandId, [], new CorrectionLimits());
        var previous = begun.Task.ImplementationRevisions[0];
        var proposal = CorrectionValidator.CreateProposal(begun.Task.Id, analysis, previous, 1,
            Now.AddSeconds(1), new CorrectionLimits());
        var analyzed = await repository.CompleteFailureAnalysisAsync(begun.Task.Id, command.CommandId,
            analysis, proposal, [], Now.AddSeconds(1));
        return (repository, await repository.ApproveCorrectionProposalAsync(new ApproveCorrectionProposalCommand(
            Guid.NewGuid(), analyzed.Id, analyzed.RowVersion, proposal.ProposalId, proposal.ProposalFingerprint,
            analysis.AnalysisId, analysis.AnalysisFingerprint, proposal.FailedAttemptId,
            proposal.FailedAttemptFingerprint, previous.RevisionId, previous.ResultFingerprint!,
            proposal.ApprovedRequirementFingerprint, proposal.ApprovedPlanFingerprint,
            proposal.OriginalBaseCommitSha), Now.AddSeconds(2)));
    }

    private static VerificationProviderResponseTelemetry Response(Guid callId) => new(callId,
        Now.AddSeconds(1), Now.AddSeconds(2), "response-test", "request-test",
        VerificationProviderResponseStatus.Completed, null, true, 10, 0, 5, 1, 200,
        VerificationCallDispatchDisposition.ResponseReceived, VerificationUsageAvailability.Complete);

    private static async Task<(CorrectionGenerationRepositoryCommandResult? Result, Exception? Error)> Capture(
        Func<Task<CorrectionGenerationRepositoryCommandResult>> action)
    {
        try { return (await action(), null); }
        catch (Exception exception) { return (null, exception); }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class NoopObserver : IVerificationGenerationObserver
    {
        public Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
