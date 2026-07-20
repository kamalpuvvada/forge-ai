using Forge.Core;
using Forge.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Tests;

public sealed class ImplementationPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"forge-implementation-db-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "forge.db");
    private string ConnectionString => $"Data Source={DatabasePath}";
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Workspace_result_and_implementation_model_stage_round_trip()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        await repository.SaveAsync(task);
        task.RecordModelCall(new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Implementation, "OpenAI", "future-model", "medium",
            Now, Now, false, null, null, null, null, null, null, "implementation_rate_limit",
            new ModelPricingSnapshot(5m, .5m, 30m), null), Now);
        task.RecordModelCall(new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Implementation, "OpenAI", "future-model", "medium",
            Now, Now, true, "response", 1, 0, 1, 0, .01m, null,
            new ModelPricingSnapshot(5m, .5m, 30m), "provider-request"), Now);
        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        await repository.SaveAsync(task);

        var loaded = await repository.GetAsync(task.Id);

        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, loaded?.Status);
        Assert.Equal(workspace.Token, loaded?.ImplementationWorkspace?.Token);
        Assert.Equal(workspace.Branch, loaded?.ImplementationResult?.Branch);
        Assert.Equal("diff --git a/src/App.cs b/src/App.cs", Assert.Single(loaded!.ImplementationResult!.ChangedFiles).DiffPreview);
        Assert.Equal(2, loaded.ModelCalls.Count(call => call.Stage == ModelCallStage.Implementation));
        Assert.Contains(loaded.ModelCalls, call => call.FailureCategory == "implementation_rate_limit");
        Assert.Contains(loaded.ModelCalls, call => call.ProviderRequestId == "provider-request");
        var revision = Assert.Single(loaded.ImplementationRevisions);
        Assert.Equal(revision.RevisionId, loaded.ActiveImplementationRevisionId);
        Assert.Equal(ImplementationReviewState.Current, revision.ReviewState);
        Assert.Equal(revision.ResultFingerprint, ImplementationReviewFingerprint.ComputeResult(
            loaded.Id, revision.RevisionId, revision.RevisionNumber, revision.Kind,
            revision.PlanFingerprint, loaded.ImplementationResult));
    }

    [Fact]
    public async Task Recoverable_failure_round_trips_and_additive_columns_exist()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = ApprovedTask();
        var lease = Lease(Now);
        task.BeginImplementation(Workspace(), lease, Now);
        await repository.SaveAsync(task);
        task.RecordImplementationFailure(new ImplementationFailure("implementation_recovery_required", "Recovery is required.", true, Now),
            lease.AttemptId, lease.OwnerId, Now);
        await repository.SaveAsync(task);

        var loaded = await repository.GetAsync(task.Id);
        Assert.Equal(WorkflowStatus.Implementing, loaded?.Status);
        Assert.True(loaded?.LastImplementationFailure?.RecoveryRequired);
        Assert.Equal(ImplementationWorkspacePhase.RecoveryRequired, loaded?.ImplementationWorkspace?.Phase);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(EngineeringTasks);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("ImplementationWorkspace", columns);
        Assert.Contains("ImplementationResult", columns);
        Assert.Contains("LastImplementationFailure", columns);
        Assert.Contains("ImplementationStartedAt", columns);
        Assert.Contains("ImplementationCompletedAt", columns);
        Assert.Contains("ImplementationLease", columns);
        Assert.Contains("ImplementationRevisions", columns);
        Assert.Contains("ActiveImplementationRevisionId", columns);
        Assert.Contains("ApprovedImplementationRevisionId", columns);
        Assert.Contains("RowVersion", columns);

        await reader.DisposeAsync();
        command.CommandText = "PRAGMA table_info(ImplementationApprovalCommands);";
        await using var approvalReader = await command.ExecuteReaderAsync();
        var approvalColumns = new List<string>();
        while (await approvalReader.ReadAsync()) approvalColumns.Add(approvalReader.GetString(1));
        Assert.Equal(
            ["CommandId", "TaskId", "ExpectedRowVersion", "RevisionId", "ResultFingerprint", "ApprovedRowVersion", "ApprovalTimestamp"],
            approvalColumns);
    }

    [Fact]
    public async Task Exact_approval_and_revision_ledger_round_trip_atomically()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        await repository.SaveAsync(task);
        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        await repository.SaveAsync(task);
        var current = Assert.Single(task.ImplementationRevisions);
        var commandId = Guid.NewGuid();
        var expectedRowVersion = task.RowVersion;
        task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            commandId, task.Id, expectedRowVersion, current.RevisionId, current.ResultFingerprint!), Now.AddMinutes(3));

        var loaded = (await repository.GetAsync(task.Id))!;
        var approved = Assert.Single(loaded.ImplementationRevisions);
        Assert.Equal(WorkflowStatus.ImplementationApproved, loaded.Status);
        Assert.Equal(approved.RevisionId, loaded.ActiveImplementationRevisionId);
        Assert.Equal(approved.RevisionId, loaded.ApprovedImplementationRevisionId);
        Assert.Equal(ImplementationReviewState.Approved, approved.ReviewState);
        Assert.Equal(commandId, approved.ApprovalCommandId);
        Assert.Equal(Now.AddMinutes(3), approved.ApprovedAt);
        Assert.Equal(expectedRowVersion + 1, loaded.RowVersion);
    }

    [Fact]
    public async Task Concurrent_approval_cas_allows_only_one_persisted_decision()
    {
        await SeedReviewAsync();
        var firstRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var secondRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var review = (await firstRepository.GetAsync(_seedId))!;
        var revision = Assert.Single(review.ImplementationRevisions);
        var firstAttempt = firstRepository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), review.Id, review.RowVersion, revision.RevisionId, revision.ResultFingerprint!),
            Now.AddMinutes(3));
        var secondAttempt = secondRepository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), review.Id, review.RowVersion, revision.RevisionId, revision.ResultFingerprint!),
            Now.AddMinutes(4));

        try { await Task.WhenAll(firstAttempt, secondAttempt); } catch { /* One command must lose. */ }
        Assert.Equal(1, new[] { firstAttempt, secondAttempt }.Count(attempt => attempt.IsCompletedSuccessfully));
        Assert.Equal(1, new[] { firstAttempt, secondAttempt }.Count(attempt => attempt.IsFaulted));

        var persisted = (await firstRepository.GetAsync(_seedId))!;
        Assert.Equal(WorkflowStatus.ImplementationApproved, persisted.Status);
        Assert.Single(persisted.ImplementationRevisions,
            revision => revision.ReviewState == ImplementationReviewState.Approved);
    }

    [Fact]
    public async Task Approval_command_is_globally_bound_across_tasks()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var first = await CreateAndSaveReviewAsync(repository);
        var second = await CreateAndSaveReviewAsync(repository);
        var commandId = Guid.NewGuid();
        var firstRevision = Assert.Single(first.ImplementationRevisions);
        var secondRevision = Assert.Single(second.ImplementationRevisions);

        await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            commandId, first.Id, first.RowVersion, firstRevision.RevisionId, firstRevision.ResultFingerprint!),
            Now.AddMinutes(3));
        await Assert.ThrowsAsync<TaskConcurrencyException>(() => repository.ApproveImplementationAsync(
            new ImplementationApprovalCommand(commandId, second.Id, second.RowVersion,
                secondRevision.RevisionId, secondRevision.ResultFingerprint!), Now.AddMinutes(4)));

        Assert.Equal(1, await CountApprovalCommandsAsync());
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview,
            (await repository.GetAsync(second.Id))!.Status);
    }

    [Fact]
    public async Task Simultaneous_cross_task_reuse_of_one_command_id_has_one_winner()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var seedRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var first = await CreateAndSaveReviewAsync(seedRepository);
        var second = await CreateAndSaveReviewAsync(seedRepository);
        var firstRevision = Assert.Single(first.ImplementationRevisions);
        var secondRevision = Assert.Single(second.ImplementationRevisions);
        var commandId = Guid.NewGuid();
        var firstAttempt = new SqliteEngineeringTaskRepository(ConnectionString).ApproveImplementationAsync(
            new ImplementationApprovalCommand(commandId, first.Id, first.RowVersion,
                firstRevision.RevisionId, firstRevision.ResultFingerprint!), Now.AddMinutes(3));
        var secondAttempt = new SqliteEngineeringTaskRepository(ConnectionString).ApproveImplementationAsync(
            new ImplementationApprovalCommand(commandId, second.Id, second.RowVersion,
                secondRevision.RevisionId, secondRevision.ResultFingerprint!), Now.AddMinutes(3));

        try { await Task.WhenAll(firstAttempt, secondAttempt); } catch { /* Exactly one must conflict. */ }

        var attempts = new[] { firstAttempt, secondAttempt };
        _ = Assert.Single(attempts, attempt => attempt.IsCompletedSuccessfully);
        var losingAttempt = Assert.Single(attempts, attempt => attempt.IsFaulted);
        Assert.IsType<TaskConcurrencyException>(losingAttempt.Exception!.GetBaseException());
        Assert.Equal(1, await CountApprovalCommandsAsync());
        SqliteConnection.ClearAllPools();
        var reopenedRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var reopenedFirst = (await reopenedRepository.GetAsync(first.Id))!;
        var reopenedSecond = (await reopenedRepository.GetAsync(second.Id))!;
        Assert.Equal(1, new[] { reopenedFirst, reopenedSecond }.Count(task =>
            task.Status == WorkflowStatus.ImplementationApproved));
        var loser = reopenedFirst.Status == WorkflowStatus.ImplementationApproved ? reopenedSecond : reopenedFirst;
        var originalLoser = loser.Id == first.Id ? first : second;
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, loser.Status);
        Assert.Equal(originalLoser.RowVersion, loser.RowVersion);
        Assert.Null(loser.ApprovedImplementationRevisionId);
        Assert.Null(Assert.Single(loser.ImplementationRevisions).ApprovedAt);
    }

    [Fact]
    public async Task Exact_approval_replay_survives_restart_without_changing_row_or_timestamp()
    {
        await SeedReviewAsync();
        var firstRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var review = (await firstRepository.GetAsync(_seedId))!;
        var revision = Assert.Single(review.ImplementationRevisions);
        var command = new ImplementationApprovalCommand(Guid.NewGuid(), review.Id, review.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        var approved = await firstRepository.ApproveImplementationAsync(command, Now.AddMinutes(3));
        var approvedRevision = Assert.Single(approved.ImplementationRevisions);

        SqliteConnection.ClearAllPools();
        var restartedRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var replayed = await restartedRepository.ApproveImplementationAsync(command, Now.AddMinutes(9));

        Assert.Equal(approved.RowVersion, replayed.RowVersion);
        Assert.Equal(approvedRevision.ApprovedAt, Assert.Single(replayed.ImplementationRevisions).ApprovedAt);
        Assert.Equal(1, await CountApprovalCommandsAsync());
        await Assert.ThrowsAsync<TaskConcurrencyException>(() => restartedRepository.ApproveImplementationAsync(
            command with { ExpectedRowVersion = command.ExpectedRowVersion + 1 }, Now.AddMinutes(10)));
    }

    [Fact]
    public async Task Simultaneous_identical_approval_commands_return_one_immutable_approval()
    {
        await SeedReviewAsync();
        var review = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        var revision = Assert.Single(review.ImplementationRevisions);
        var command = new ImplementationApprovalCommand(Guid.NewGuid(), review.Id, review.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        var first = new SqliteEngineeringTaskRepository(ConnectionString)
            .ApproveImplementationAsync(command, Now.AddMinutes(3));
        var second = new SqliteEngineeringTaskRepository(ConnectionString)
            .ApproveImplementationAsync(command, Now.AddMinutes(4));

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(WorkflowStatus.ImplementationApproved, result.Status));
        Assert.Equal(results[0].RowVersion, results[1].RowVersion);
        Assert.Equal(Assert.Single(results[0].ImplementationRevisions).ApprovedAt,
            Assert.Single(results[1].ImplementationRevisions).ApprovedAt);
        Assert.Equal(1, await CountApprovalCommandsAsync());
    }

    [Fact]
    public async Task Failed_task_cas_rolls_back_approval_command_and_task_changes()
    {
        await SeedReviewAsync();
        var review = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        var revision = Assert.Single(review.ImplementationRevisions);
        var command = new ImplementationApprovalCommand(Guid.NewGuid(), review.Id, review.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        var repository = new SqliteEngineeringTaskRepository(
            ConnectionString,
            new ImplementationLimits(),
            null,
            _ => Task.CompletedTask,
            async (connection, transaction, cancellationToken) =>
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE EngineeringTasks SET RowVersion = RowVersion + 1 WHERE Id = $id;";
                update.Parameters.AddWithValue("$id", review.Id.ToString());
                Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
            });

        await Assert.ThrowsAsync<TaskConcurrencyException>(() =>
            repository.ApproveImplementationAsync(command, Now.AddMinutes(3)));

        Assert.Equal(0, await CountApprovalCommandsAsync());
        var unchanged = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(review.Id))!;
        Assert.Equal(review.RowVersion, unchanged.RowVersion);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, unchanged.Status);
        Assert.Null(unchanged.ApprovedImplementationRevisionId);
    }

    [Fact]
    public async Task Persisted_approved_task_with_uncertain_checkout_evidence_is_rejected()
    {
        await SeedReviewAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var review = (await repository.GetAsync(_seedId))!;
        var revision = Assert.Single(review.ImplementationRevisions);
        var approved = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), review.Id, review.RowVersion, revision.RevisionId, revision.ResultFingerprint!),
            Now.AddMinutes(3));
        var approvedRevision = Assert.Single(approved.ImplementationRevisions);
        var uncertainResult = approved.ImplementationResult! with { ActiveCheckoutVerified = false };
        var uncertainRevision = approvedRevision with
        {
            Result = uncertainResult,
            ResultFingerprint = ImplementationReviewFingerprint.ComputeResult(
                approved.Id, approvedRevision.RevisionId, approvedRevision.RevisionNumber,
                approvedRevision.Kind, approvedRevision.PlanFingerprint, uncertainResult)
        };

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE EngineeringTasks
                SET ImplementationResult = $result,
                    ImplementationRevisions = $revisions
                WHERE Id = $id;
                """;
            update.Parameters.AddWithValue("$result", JsonSerializer.Serialize(uncertainResult, JsonOptions));
            update.Parameters.AddWithValue("$revisions", JsonSerializer.Serialize(new[] { uncertainRevision }, JsonOptions));
            update.Parameters.AddWithValue("$id", approved.Id.ToString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(approved.Id));
    }

    [Fact]
    public async Task Malformed_persisted_approval_command_is_rejected_safely()
    {
        await SeedReviewAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var review = (await repository.GetAsync(_seedId))!;
        var revision = Assert.Single(review.ImplementationRevisions);
        var commandId = Guid.NewGuid();
        await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            commandId, review.Id, review.RowVersion, revision.RevisionId, revision.ResultFingerprint!),
            Now.AddMinutes(3));

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA ignore_check_constraints = ON;";
            await pragma.ExecuteNonQueryAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE ImplementationApprovalCommands
                SET ResultFingerprint = $fingerprint
                WHERE CommandId = $commandId;
                """;
            update.Parameters.AddWithValue("$fingerprint", new string('A', 64));
            update.Parameters.AddWithValue("$commandId", commandId.ToString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(review.Id));
    }

    [Fact]
    public async Task Legacy_completed_review_synthesizes_one_stable_initial_revision_across_reads_and_reopen()
    {
        await SeedReviewAsync();
        await UpdateRawAsync(
            "ImplementationRevisions = '[]', ActiveImplementationRevisionId = NULL, ApprovedImplementationRevisionId = NULL",
            ("$unused", null), ("$unused2", null));

        var first = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        var second = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        var firstRevision = Assert.Single(first.ImplementationRevisions);
        var secondRevision = Assert.Single(second.ImplementationRevisions);
        Assert.Equal(firstRevision.RevisionId, secondRevision.RevisionId);
        Assert.Equal(ImplementationRevisionKind.Initial, firstRevision.Kind);
        Assert.Equal(ImplementationReviewState.Current, firstRevision.ReviewState);
        Assert.Null(first.ApprovedImplementationRevisionId);

        await new SqliteEngineeringTaskRepository(ConnectionString).SaveAsync(first);
        SqliteConnection.ClearAllPools();
        var reopened = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        Assert.Equal(firstRevision.RevisionId, Assert.Single(reopened.ImplementationRevisions).RevisionId);
    }

    [Fact]
    public async Task Legacy_review_missing_checkout_certainty_rehydrates_uncertain_and_remains_ineligible()
    {
        await SeedReviewAsync();
        var original = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        var missingCertaintyJson = RemoveCheckoutCertainty(
            JsonSerializer.Serialize(original.ImplementationResult, JsonOptions));
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE EngineeringTasks
                SET ImplementationResult = $result,
                    ImplementationRevisions = '[]',
                    ActiveImplementationRevisionId = NULL,
                    ApprovedImplementationRevisionId = NULL
                WHERE Id = $id;
                """;
            update.Parameters.AddWithValue("$result", missingCertaintyJson);
            update.Parameters.AddWithValue("$id", _seedId.ToString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var first = (await repository.GetAsync(_seedId))!;
        var firstRevision = Assert.Single(first.ImplementationRevisions);
        Assert.False(first.ImplementationResult!.ActiveCheckoutVerified);
        Assert.False(firstRevision.Result!.ActiveCheckoutVerified);
        Assert.Equal(ImplementationReviewState.Current, firstRevision.ReviewState);
        var verifiedFingerprint = ImplementationReviewFingerprint.ComputeResult(
            first.Id, firstRevision.RevisionId, firstRevision.RevisionNumber, firstRevision.Kind,
            firstRevision.PlanFingerprint, firstRevision.Result with { ActiveCheckoutVerified = true });
        Assert.NotEqual(verifiedFingerprint, firstRevision.ResultFingerprint);
        var rowVersion = first.RowVersion;
        var commandsBefore = await CountApprovalCommandsAsync();

        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
                Guid.NewGuid(), first.Id, first.RowVersion, firstRevision.RevisionId,
                firstRevision.ResultFingerprint!), Now.AddMinutes(3)));

        Assert.Equal("implementation_review_ineligible", exception.Category);
        Assert.Equal(commandsBefore, await CountApprovalCommandsAsync());
        var second = (await repository.GetAsync(_seedId))!;
        Assert.Equal(rowVersion, second.RowVersion);
        Assert.Null(second.ApprovedImplementationRevisionId);
        Assert.Null(Assert.Single(second.ImplementationRevisions).ApprovedAt);
        Assert.Equal(firstRevision.RevisionId, Assert.Single(second.ImplementationRevisions).RevisionId);
        SqliteConnection.ClearAllPools();
        var reopened = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId))!;
        Assert.False(reopened.ImplementationResult!.ActiveCheckoutVerified);
        Assert.Equal(firstRevision.RevisionId, Assert.Single(reopened.ImplementationRevisions).RevisionId);
        Assert.Null(reopened.ApprovedImplementationRevisionId);
    }

    [Fact]
    public async Task Revision_result_missing_checkout_certainty_rehydrates_false_and_later_save_is_explicit()
    {
        await SeedReviewAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = (await repository.GetAsync(_seedId))!;
        var current = Assert.Single(task.ImplementationRevisions);
        var uncertainResult = task.ImplementationResult! with { ActiveCheckoutVerified = false };
        var uncertainRevision = current with
        {
            Result = uncertainResult,
            ResultFingerprint = ImplementationReviewFingerprint.ComputeResult(
                task.Id, current.RevisionId, current.RevisionNumber, current.Kind,
                current.PlanFingerprint, uncertainResult)
        };
        var ledgerJson = JsonSerializer.Serialize(new[] { uncertainRevision }, JsonOptions);
        ledgerJson = RemoveRevisionCheckoutCertainty(ledgerJson);
        await UpdateImplementationReviewJsonAsync(
            JsonSerializer.Serialize(uncertainResult, JsonOptions), ledgerJson);

        var loaded = (await repository.GetAsync(_seedId))!;
        var loadedRevision = Assert.Single(loaded.ImplementationRevisions);
        Assert.False(loaded.ImplementationResult!.ActiveCheckoutVerified);
        Assert.False(loadedRevision.Result!.ActiveCheckoutVerified);
        Assert.Equal(uncertainRevision.ResultFingerprint, loadedRevision.ResultFingerprint);
        var verifiedFingerprint = ImplementationReviewFingerprint.ComputeResult(
            loaded.Id, loadedRevision.RevisionId, loadedRevision.RevisionNumber, loadedRevision.Kind,
            loadedRevision.PlanFingerprint, loadedRevision.Result with { ActiveCheckoutVerified = true });
        Assert.NotEqual(verifiedFingerprint, loadedRevision.ResultFingerprint);

        await repository.SaveAsync(loaded);
        var (savedResult, savedLedger) = await ReadImplementationReviewJsonAsync(_seedId);
        Assert.False(JsonNode.Parse(savedResult)!["activeCheckoutVerified"]!.GetValue<bool>());
        Assert.False(JsonNode.Parse(savedLedger)!.AsArray()[0]!["result"]!["activeCheckoutVerified"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Approved_revision_missing_checkout_certainty_is_rejected_as_corrupt()
    {
        await SeedReviewAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var review = (await repository.GetAsync(_seedId))!;
        var current = Assert.Single(review.ImplementationRevisions);
        var approved = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(
            Guid.NewGuid(), review.Id, review.RowVersion, current.RevisionId, current.ResultFingerprint!),
            Now.AddMinutes(3));
        var approvedRevision = Assert.Single(approved.ImplementationRevisions);
        var uncertainResult = approved.ImplementationResult! with { ActiveCheckoutVerified = false };
        var uncertainRevision = approvedRevision with
        {
            Result = uncertainResult,
            ResultFingerprint = ImplementationReviewFingerprint.ComputeResult(
                approved.Id, approvedRevision.RevisionId, approvedRevision.RevisionNumber,
                approvedRevision.Kind, approvedRevision.PlanFingerprint, uncertainResult)
        };
        await UpdateImplementationReviewJsonAsync(
            RemoveCheckoutCertainty(JsonSerializer.Serialize(uncertainResult, JsonOptions)),
            RemoveRevisionCheckoutCertainty(JsonSerializer.Serialize(new[] { uncertainRevision }, JsonOptions)));

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));
    }

    [Fact]
    public async Task Legacy_in_progress_review_synthesizes_one_stable_generating_revision()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = ApprovedTask();
        _seedId = task.Id;
        var lease = Lease(Now);
        task.BeginImplementation(Workspace(), lease, Now);
        await repository.SaveAsync(task);
        await UpdateRawAsync(
            "ImplementationRevisions = '[]', ActiveImplementationRevisionId = NULL, ApprovedImplementationRevisionId = NULL",
            ("$unused", null), ("$unused2", null));

        var first = (await repository.GetAsync(task.Id))!;
        var second = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
        var firstRevision = Assert.Single(first.ImplementationRevisions);
        Assert.Equal(firstRevision.RevisionId, Assert.Single(second.ImplementationRevisions).RevisionId);
        Assert.Equal(ImplementationGenerationState.Generating, firstRevision.GenerationState);
        Assert.Equal(ImplementationReviewState.NotReviewable, firstRevision.ReviewState);
        Assert.Equal(firstRevision.RevisionId, first.ActiveImplementationRevisionId);
        Assert.Null(first.ApprovedImplementationRevisionId);
    }

    [Fact]
    public async Task Malformed_oversized_or_projection_mismatched_revision_history_fails_safely()
    {
        await SeedReviewAsync();
        await UpdateRawAsync("ImplementationRevisions = '{'", ("$unused", null), ("$unused2", null));
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));

        await SeedReviewAsync();
        var limits = new ImplementationLimits { MaximumPersistedImplementationRevisionJsonCharacters = 10 };
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString, limits).GetAsync(_seedId));
        var byteLimits = new ImplementationLimits
        {
            MaximumPersistedImplementationRevisionJsonCharacters = int.MaxValue,
            MaximumPersistedImplementationRevisionJsonBytes = 10
        };
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString, byteLimits).GetAsync(_seedId));

        await SeedReviewAsync();
        var resultJson = await ReadColumnAsync("ImplementationResult");
        var changed = JsonSerializer.Deserialize<ImplementationResult>(resultJson, JsonOptions)! with { Summary = "Changed projection" };
        await UpdateRawAsync("ImplementationResult = $value",
            ("$value", JsonSerializer.Serialize(changed, JsonOptions)), ("$unused", null));
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));
    }

    [Fact]
    public async Task Stale_GET_versus_completion_race_rejects_stale_save_and_preserves_completed_result()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var firstRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var secondRepository = new SqliteEngineeringTaskRepository(ConnectionString);
        var seed = ApprovedTask();
        await firstRepository.SaveAsync(seed);
        var winner = (await firstRepository.GetAsync(seed.Id))!;
        var stale = (await secondRepository.GetAsync(seed.Id))!;
        var workspace = Workspace();
        var winnerLease = Lease(Now.AddMinutes(1));
        winner.BeginImplementation(workspace, winnerLease, Now.AddMinutes(1));
        await firstRepository.SaveAsync(winner);
        winner.StoreImplementationResult(Result(workspace), winnerLease.AttemptId, winnerLease.OwnerId, Now.AddMinutes(2));
        await firstRepository.SaveAsync(winner);

        var staleLease = Lease(Now.AddMinutes(1));
        stale.BeginImplementation(workspace, staleLease, Now.AddMinutes(1));
        stale.RecordImplementationFailure(new ImplementationFailure(
            "implementation_failure", "A stale failure must not win.", true, Now.AddMinutes(2)),
            staleLease.AttemptId, staleLease.OwnerId, Now.AddMinutes(2));

        await Assert.ThrowsAsync<TaskConcurrencyException>(() => secondRepository.SaveAsync(stale));
        var persisted = await firstRepository.GetAsync(seed.Id);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, persisted?.Status);
        Assert.NotNull(persisted?.ImplementationResult);
        Assert.Null(persisted?.LastImplementationFailure);
        Assert.Equal(3, persisted?.RowVersion);
    }

    [Fact]
    public void Lease_owner_and_expiry_are_enforced_and_expired_reserved_work_can_be_released()
    {
        var task = ApprovedTask();
        var acquired = Now.AddMinutes(1);
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            acquired, acquired, acquired.AddSeconds(1));
        task.BeginImplementation(Workspace(), lease, acquired);
        Assert.Throws<ImplementationException>(() => task.UpdateImplementationWorkspace(
            task.ImplementationWorkspace!, lease.AttemptId, Guid.NewGuid(), acquired));

        var replacement = Lease(acquired.AddSeconds(2));
        task.ResumeImplementation(replacement, acquired.AddSeconds(2));
        Assert.Equal(replacement.LeaseId, task.ImplementationLease?.LeaseId);
    }

    [Fact]
    public void Lease_renewal_uses_one_fixed_duration_without_growth()
    {
        var task = ApprovedTask();
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now, Now, Now.AddSeconds(60), 60);
        task.BeginImplementation(Workspace(), lease, Now);

        task.UpdateImplementationWorkspace(task.ImplementationWorkspace!, lease.AttemptId, lease.OwnerId,
            Now.AddSeconds(10));
        Assert.Equal(Now.AddSeconds(70), task.ImplementationLease?.ExpiresAt);
        task.UpdateImplementationWorkspace(task.ImplementationWorkspace!, lease.AttemptId, lease.OwnerId,
            Now.AddSeconds(20));

        Assert.Equal(Now.AddSeconds(80), task.ImplementationLease?.ExpiresAt);
        Assert.Equal(60, task.ImplementationLease?.EffectiveDurationSeconds);
    }

    [Fact]
    public async Task Every_durable_implementation_phase_survives_a_real_sqlite_close_and_reopen()
    {
        Directory.CreateDirectory(_directory);
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var task = ApprovedTask();
        await new SqliteEngineeringTaskRepository(ConnectionString).SaveAsync(task);
        task = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
        var workspace = Workspace();
        var lease = Lease(Now);
        task.BeginImplementation(workspace, lease, Now);
        task = await SaveAndReopenAsync(task);
        Assert.Equal(ImplementationWorkspacePhase.Reserved, task.ImplementationWorkspace?.Phase);

        foreach (var phase in new[]
                 {
                     ImplementationWorkspacePhase.Ready,
                     ImplementationWorkspacePhase.WorkspacePreparing,
                     ImplementationWorkspacePhase.WorkspacePrepared,
                     ImplementationWorkspacePhase.MutationStarted,
                     ImplementationWorkspacePhase.ApplyCompleted
                 })
        {
            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with { Phase = phase },
                lease.AttemptId, lease.OwnerId, Now.AddSeconds((int)phase));
            task = await SaveAndReopenAsync(task);
            Assert.Equal(phase, task.ImplementationWorkspace?.Phase);
        }

        var result = Result(task.ImplementationWorkspace!);
        task.StoreImplementationResult(result, lease.AttemptId, lease.OwnerId, result.CompletedAt);
        task = await SaveAndReopenAsync(task);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, task.Status);
        Assert.Equal(ImplementationWorkspacePhase.ResultPersisted, task.ImplementationWorkspace?.Phase);
        Assert.NotNull(task.ImplementationResult);
    }

    [Theory]
    [InlineData(false, ImplementationWorkspacePhase.Interrupted)]
    [InlineData(true, ImplementationWorkspacePhase.RecoveryRequired)]
    public async Task Durable_failure_phases_survive_a_real_sqlite_close_and_reopen(
        bool recoveryRequired,
        ImplementationWorkspacePhase expectedPhase)
    {
        Directory.CreateDirectory(_directory);
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var task = ApprovedTask();
        var lease = Lease(Now);
        task.BeginImplementation(Workspace(), lease, Now);
        task.RecordImplementationFailure(new ImplementationFailure(
                recoveryRequired ? "implementation_recovery_required" : "implementation_interrupted",
                recoveryRequired ? "Recovery is required." : "Implementation was interrupted.",
                recoveryRequired,
                Now,
                SafeToResume: false),
            lease.AttemptId, lease.OwnerId, Now);

        task = await SaveAndReopenAsync(task);

        Assert.Equal(WorkflowStatus.Implementing, task.Status);
        Assert.Equal(expectedPhase, task.ImplementationWorkspace?.Phase);
        Assert.Null(task.ImplementationLease);
        Assert.Equal(recoveryRequired, task.LastImplementationFailure?.RecoveryRequired);
    }

    [Theory]
    [InlineData("ImplementationWorkspace", "{")]
    [InlineData("ImplementationWorkspace", "{}")]
    [InlineData("ImplementationLease", "{\"leaseId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task Malformed_or_missing_required_implementation_json_is_reported_safely(
        string column,
        string json)
    {
        await SeedApprovedAsync();
        await UpdateRawAsync($"{column} = $value", ("$value", json), ("$status", null));

        var exception = await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));

        Assert.DoesNotContain(json, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_implementation_json_is_rejected_before_deserialization()
    {
        await SeedApprovedAsync();
        var oversized = new string('x', new ImplementationLimits().MaximumPersistedImplementationJsonCharacters + 1);
        await UpdateRawAsync("ImplementationResult = $value", ("$value", oversized), ("$status", null));

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));
    }

    [Fact]
    public async Task Multibyte_implementation_json_is_rejected_by_blob_byte_length_before_materialization()
    {
        await SeedApprovedAsync();
        var limits = new ImplementationLimits();
        var multibyte = new string('€', limits.MaximumPersistedImplementationJsonCharacters);
        await UpdateRawAsync("ImplementationResult = $value", ("$value", multibyte), ("$status", null));

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString, limits).GetAsync(_seedId));
    }

    [Fact]
    public async Task Huge_sqlite_text_value_is_rejected_from_length_metadata_before_get_string()
    {
        await SeedApprovedAsync();
        var limits = new ImplementationLimits();
        await UpdateRawAsync("ImplementationResult = CAST(zeroblob($value) AS TEXT)",
            ("$value", limits.MaximumPersistedImplementationJsonBytes + 1), ("$status", null));

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString, limits).GetAsync(_seedId));
    }

    [Fact]
    public async Task Unknown_enum_and_inconsistent_review_state_are_rejected_safely()
    {
        await SeedApprovedAsync();
        var workspace = Workspace();
        var json = JsonSerializer.Serialize(workspace with { Phase = (ImplementationWorkspacePhase)999 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await UpdateRawAsync("Status = 'Implementing', ImplementationWorkspace = $value",
            ("$value", json), ("$status", null));
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));

        await UpdateRawAsync("Status = 'AwaitingImplementationReview', ImplementationWorkspace = NULL, ImplementationResult = NULL",
            ("$unused", null), ("$status", null));
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));
    }

    [Fact]
    public async Task Excessive_persisted_diff_preview_and_changed_file_count_are_rejected_safely()
    {
        var (_, result) = await SeedReviewAsync();
        var maximum = new ImplementationLimits().MaximumDiffPreviewCharactersPerFile;
        var preview = new string('d', maximum + 1);
        var originalFile = Assert.Single(result.ChangedFiles);
        var oversizedFile = originalFile with
        {
            DiffPreview = preview,
            FullDiffCharacters = preview.Length,
            DisplayedDiffCharacters = preview.Length,
            FullDiffUtf8Bytes = preview.Length,
            DisplayedDiffUtf8Bytes = preview.Length
        };
        var oversizedPreview = result with
        {
            ChangedFiles = [oversizedFile],
            FullDiffCharacters = preview.Length,
            DisplayedDiffCharacters = preview.Length,
            FullDiffUtf8Bytes = preview.Length,
            DisplayedDiffUtf8Bytes = preview.Length
        };
        await UpdateRawAsync("ImplementationResult = $value",
            ("$value", JsonSerializer.Serialize(oversizedPreview, JsonOptions)), ("$unused", null));

        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));

        var excessiveFiles = result with
        {
            ChangedFiles = Enumerable.Repeat(originalFile, new ImplementationLimits().MaximumApprovedOperations + 1).ToArray()
        };
        await UpdateRawAsync("ImplementationResult = $value",
            ("$value", JsonSerializer.Serialize(excessiveFiles, JsonOptions)), ("$unused", null));
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));
    }

    [Fact]
    public async Task Nullable_legacy_implementation_columns_remain_backward_compatible()
    {
        await SeedApprovedAsync();

        var loaded = await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId);

        Assert.Equal(WorkflowStatus.PlanApproved, loaded?.Status);
        Assert.Null(loaded?.ImplementationWorkspace);
        Assert.Null(loaded?.ImplementationResult);
        Assert.Null(loaded?.LastImplementationFailure);
        Assert.Null(loaded?.ImplementationLease);
    }

    [Fact]
    public async Task Real_sqlite_provider_errors_are_normalized_without_storage_details()
    {
        Directory.CreateDirectory(_directory);
        var invalidDataSource = _directory;
        var repository = new SqliteEngineeringTaskRepository($"Data Source={invalidDataSource};Default Timeout=1;Pooling=False");

        var read = await Assert.ThrowsAsync<TaskPersistenceException>(() => repository.GetAsync(Guid.NewGuid()));
        var list = await Assert.ThrowsAsync<TaskPersistenceException>(() => repository.ListRecentAsync(1));
        var write = await Assert.ThrowsAsync<TaskPersistenceException>(() => repository.SaveAsync(ApprovedTask()));

        foreach (var exception in new[] { read, list, write })
        {
            Assert.Equal("Task persistence is temporarily unavailable. Retry the request after storage access is restored.",
                exception.Message);
            Assert.DoesNotContain(invalidDataSource, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public async Task Implementation_bounds_and_values_are_read_from_one_sqlite_statement_snapshot()
    {
        await SeedReviewAsync();
        await using (var setup = new SqliteConnection(ConnectionString))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteScalarAsync();
        }
        var boundsRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new SqliteEngineeringTaskRepository(ConnectionString, new ImplementationLimits(), null,
            async cancellationToken =>
            {
                boundsRead.TrySetResult();
                await continueRead.Task.WaitAsync(cancellationToken);
            });

        var read = repository.GetAsync(_seedId);
        await boundsRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var writer = new SqliteConnection(ConnectionString);
            await writer.OpenAsync();
            await using var update = writer.CreateCommand();
            update.CommandText = "UPDATE EngineeringTasks SET ImplementationResult = '{' WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", _seedId.ToString());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        finally
        {
            continueRead.TrySetResult();
        }

        var snapshot = await read;

        Assert.NotNull(snapshot?.ImplementationResult);
        await Assert.ThrowsAsync<TaskDataCorruptException>(() =>
            new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(_seedId));
    }

    [Fact]
    public async Task Concurrent_get_is_read_only_and_cannot_overwrite_implementation_completion()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        await using (var setup = new SqliteConnection(ConnectionString))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteScalarAsync();
        }
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now);
        task.BeginImplementation(workspace, lease, Now);
        await repository.SaveAsync(task);
        var winner = (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerRepository = new SqliteEngineeringTaskRepository(ConnectionString, new ImplementationLimits(), null,
            async cancellationToken =>
            {
                readStarted.TrySetResult();
                await continueRead.Task.WaitAsync(cancellationToken);
            });

        var concurrentRead = readerRepository.GetAsync(task.Id);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var result = Result(workspace);
            winner.StoreImplementationResult(result, lease.AttemptId, lease.OwnerId, result.CompletedAt);
            await new SqliteEngineeringTaskRepository(ConnectionString).SaveAsync(winner);
        }
        finally
        {
            continueRead.TrySetResult();
        }
        var staleProjection = await concurrentRead;
        var completed = await repository.GetAsync(task.Id);

        Assert.Equal(WorkflowStatus.Implementing, staleProjection?.Status);
        Assert.Null(staleProjection?.ImplementationResult);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, completed?.Status);
        Assert.NotNull(completed?.ImplementationResult);
        Assert.Equal(2, completed?.RowVersion);
    }

    private Guid _seedId;

    private async Task SeedApprovedAsync()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var task = ApprovedTask();
        _seedId = task.Id;
        await new SqliteEngineeringTaskRepository(ConnectionString).SaveAsync(task);
    }

    private async Task<(ImplementationWorkspace Workspace, ImplementationResult Result)> SeedReviewAsync()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var task = ApprovedTask();
        _seedId = task.Id;
        var workspace = Workspace();
        var lease = Lease(Now);
        task.BeginImplementation(workspace, lease, Now);
        await repository.SaveAsync(task);
        var result = Result(workspace);
        task.StoreImplementationResult(result, lease.AttemptId, lease.OwnerId, result.CompletedAt);
        await repository.SaveAsync(task);
        return (workspace, result);
    }

    private static async Task<EngineeringTask> CreateAndSaveReviewAsync(SqliteEngineeringTaskRepository repository)
    {
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now);
        task.BeginImplementation(workspace, lease, Now);
        await repository.SaveAsync(task);
        var result = Result(workspace);
        task.StoreImplementationResult(result, lease.AttemptId, lease.OwnerId, result.CompletedAt);
        await repository.SaveAsync(task);
        return task;
    }

    private async Task<long> CountApprovalCommandsAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImplementationApprovalCommands;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task UpdateImplementationReviewJsonAsync(string resultJson, string revisionsJson)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE EngineeringTasks
            SET ImplementationResult = $result,
                ImplementationRevisions = $revisions
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$result", resultJson);
        command.Parameters.AddWithValue("$revisions", revisionsJson);
        command.Parameters.AddWithValue("$id", _seedId.ToString());
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<(string Result, string Revisions)> ReadImplementationReviewJsonAsync(Guid id)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ImplementationResult, ImplementationRevisions
            FROM EngineeringTasks
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static string RemoveCheckoutCertainty(string json)
    {
        var result = JsonNode.Parse(json)!.AsObject();
        Assert.True(result.Remove("activeCheckoutVerified"));
        return result.ToJsonString(JsonOptions);
    }

    private static string RemoveRevisionCheckoutCertainty(string json)
    {
        var revisions = JsonNode.Parse(json)!.AsArray();
        var result = revisions[0]!["result"]!.AsObject();
        Assert.True(result.Remove("activeCheckoutVerified"));
        return revisions.ToJsonString(JsonOptions);
    }

    private async Task UpdateRawAsync(
        string assignment,
        (string Name, object? Value) value,
        (string Name, object? Value) _)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE EngineeringTasks SET {assignment} WHERE Id = $id";
        command.Parameters.AddWithValue("$id", _seedId.ToString());
        if (assignment.Contains(value.Name, StringComparison.Ordinal))
            command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadColumnAsync(string column)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM EngineeringTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", _seedId.ToString());
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<EngineeringTask> SaveAndReopenAsync(EngineeringTask task)
    {
        await new SqliteEngineeringTaskRepository(ConnectionString).SaveAsync(task);
        return (await new SqliteEngineeringTaskRepository(ConnectionString).GetAsync(task.Id))!;
    }

    private static EngineeringTask ApprovedTask()
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now) with
        {
            IsGitRepository = true,
            Branch = "main",
            ShortHeadSha = "aaaaaaaa",
            FullHeadSha = new string('a', 40),
            WorkingTreeStatus = "clean"
        };
        var evidence = PlanningWorkflowTests.Evidence();
        task.BeginRepositoryAnalysis(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), Now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(Now);
        return task;
    }

    private static ImplementationWorkspace Workspace() => new(
        new string('b', 32), $"forge/task-{new string('b', 32)}", new string('a', 40),
        ImplementationWorkspacePhase.Reserved, Now, Now, false,
        new string('1', 64), new string('2', 64), $"refs/forge/tasks/{new string('b', 32)}");

    private static ImplementationResult Result(ImplementationWorkspace workspace) => new(
        ImplementationSource.DeterministicFake, null, workspace.BaseCommitSha, workspace.Branch, "Summary", ["Warning"],
        [new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify, new string('3', 64), new string('4', 64), 10, 20, 1, 2, 1, 0,
            "diff --git a/src/App.cs b/src/App.cs", 36, 36, false, 36, 36)],
        36, 36, false, Now.AddMinutes(2), 36, 36, ActiveCheckoutVerified: true);

    private static ImplementationLease Lease(DateTimeOffset now) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now, now.AddMinutes(5));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
