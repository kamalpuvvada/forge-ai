using Forge.Core;
using Forge.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Tests;

public sealed class ImplementationServiceRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"forge-service-recovery-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(directory, "forge.db");
    private string ConnectionString => $"Data Source={DatabasePath}";
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Final_result_save_failure_persists_recovery_instead_of_false_active_work()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner, failOnSave: 6);
        var manager = new DeterministicWorkspaceManager();
        var service = Service(repository, manager, new FakeImplementationEngine());
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() => service.GenerateImplementationAsync(task.Id));
        Assert.Equal("implementation_persistence_failure", failure.Category);
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal(WorkflowStatus.Implementing, persisted?.Status);
        Assert.Equal(ImplementationWorkspacePhase.RecoveryRequired, persisted?.ImplementationWorkspace?.Phase);
        Assert.True(persisted?.LastImplementationFailure?.RecoveryRequired);
        Assert.Null(persisted?.ImplementationLease);
        Assert.Null(persisted?.ImplementationResult);
    }

    [Fact]
    public async Task Workspace_preparing_save_failure_uses_last_durable_reserved_phase_and_remains_retryable()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner, failOnSave: 2);
        var manager = new DeterministicWorkspaceManager();
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(repository, manager, new FakeImplementationEngine()).GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal("implementation_persistence_failure", failure.Category);
        Assert.False(failure.RecoveryRequired);
        Assert.Equal(WorkflowStatus.Implementing, persisted?.Status);
        Assert.Equal(ImplementationWorkspacePhase.Reserved, persisted?.ImplementationWorkspace?.Phase);
        Assert.True(persisted?.LastImplementationFailure?.SafeToResume);
        Assert.False(persisted?.LastImplementationFailure?.RecoveryRequired);
        Assert.Null(persisted?.ImplementationLease);
        Assert.Null(persisted?.ImplementationResult);
    }

    [Fact]
    public async Task Post_save_worktree_drift_retains_review_but_persists_recovery_state()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner);
        var manager = new DeterministicWorkspaceManager { FailSecondResultVerification = true };
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(repository, manager, new FakeImplementationEngine()).GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal("implementation_workspace_drift", failure.Category);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, persisted?.Status);
        Assert.NotNull(persisted?.ImplementationResult);
        Assert.Equal(ImplementationWorkspacePhase.RecoveryRequired, persisted?.ImplementationWorkspace?.Phase);
        Assert.Equal("implementation_workspace_drift", persisted?.LastImplementationFailure?.Category);
    }

    [Fact]
    public async Task Persistent_preparation_persistence_failure_does_not_mask_the_safe_original_error()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner, failOnSave: 2, failPersistently: true);
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(repository, new DeterministicWorkspaceManager(), new FakeImplementationEngine())
                .GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal("implementation_persistence_failure", failure.Category);
        Assert.False(failure.RecoveryRequired);
        Assert.Equal(ImplementationWorkspacePhase.Reserved, persisted?.ImplementationWorkspace?.Phase);
        Assert.NotNull(persisted?.ImplementationLease);
        Assert.Null(persisted?.LastImplementationFailure);
    }

    [Fact]
    public async Task One_shot_real_provider_failure_before_prepare_is_safe_resume_not_false_recovery()
    {
        var seeded = await SeedAsync();
        var task = (await seeded.ListRecentAsync(1)).Single();
        using var repository = new ProviderLockingRepository(
            new SqliteEngineeringTaskRepository($"Data Source={DatabasePath};Default Timeout=1;Pooling=False"),
            DatabasePath, lockOnSave: 2, releaseAfterFailure: true);
        var manager = new DeterministicWorkspaceManager();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(repository, manager, new FakeImplementationEngine()).GenerateImplementationAsync(task.Id));
        var persisted = await seeded.GetAsync(task.Id);

        Assert.Equal("implementation_persistence_failure", failure.Category);
        Assert.False(failure.RecoveryRequired);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(DatabasePath, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, manager.PrepareCalls);
        Assert.Equal(ImplementationWorkspacePhase.Reserved, persisted?.ImplementationWorkspace?.Phase);
        Assert.True(persisted?.LastImplementationFailure?.SafeToResume);
        Assert.False(persisted?.LastImplementationFailure?.RecoveryRequired);
    }

    [Fact]
    public async Task Persistent_real_provider_failure_and_best_effort_failure_preserve_original_safe_error()
    {
        var seeded = await SeedAsync();
        var task = (await seeded.ListRecentAsync(1)).Single();
        using var repository = new ProviderLockingRepository(
            new SqliteEngineeringTaskRepository($"Data Source={DatabasePath};Default Timeout=1;Pooling=False"),
            DatabasePath, lockOnSave: 2, releaseAfterFailure: false);
        var manager = new DeterministicWorkspaceManager();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(repository, manager, new FakeImplementationEngine()).GenerateImplementationAsync(task.Id));
        Assert.Equal("implementation_persistence_failure", failure.Category);
        Assert.False(failure.RecoveryRequired);
        Assert.Null(failure.InnerException);
        Assert.True(repository.LockedReadAttempts > 0);
        Assert.Equal(0, manager.PrepareCalls);
        repository.Release();
        var persisted = await seeded.GetAsync(task.Id);

        Assert.Equal(ImplementationWorkspacePhase.Reserved, persisted?.ImplementationWorkspace?.Phase);
        Assert.NotNull(persisted?.ImplementationLease);
        Assert.Null(persisted?.LastImplementationFailure);
    }

    [Fact]
    public async Task Sensitive_generated_value_is_rejected_before_mutation_and_never_persisted()
    {
        var inner = await SeedAsync();
        var value = $"sk-{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(new FailOnSaveRepository(inner), new DeterministicWorkspaceManager(), new SensitiveEngine(value))
                .GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(ImplementationResult, '') || coalesce(LastImplementationFailure, '') FROM EngineeringTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        var stored = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal("implementation_sensitive_content", failure.Category);
        Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(value, persisted?.LastImplementationFailure?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(WorkflowStatus.PlanApproved, persisted?.Status);
        Assert.Null(persisted?.ImplementationWorkspace);
        Assert.Null(persisted?.LastImplementationFailure);
    }

    [Fact]
    public async Task Sensitive_failure_after_mutation_is_normalized_before_exception_and_persistence()
    {
        var inner = await SeedAsync();
        var value = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                    Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "Aa1-";
        var manager = new DeterministicWorkspaceManager
        {
            ApplyFailure = new ImplementationException("implementation_failure",
                $"deployment credential: {value}", true)
        };
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(new FailOnSaveRepository(inner), manager, new FakeImplementationEngine())
                .GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(ImplementationResult, '') || coalesce(LastImplementationFailure, '') FROM EngineeringTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        var stored = (string)(await command.ExecuteScalarAsync())!;

        Assert.True(manager.ApplyCalled);
        Assert.True(failure.RecoveryRequired);
        Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(value, persisted?.LastImplementationFailure?.Message ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(ImplementationWorkspacePhase.RecoveryRequired, persisted?.ImplementationWorkspace?.Phase);
    }

    [Fact]
    public async Task Two_independent_services_use_row_cas_so_only_one_attempt_can_implement()
    {
        var seedRepository = await SeedAsync();
        var firstRepository = new FailOnSaveRepository(new SqliteEngineeringTaskRepository(ConnectionString));
        var secondRepository = new FailOnSaveRepository(new SqliteEngineeringTaskRepository(ConnectionString));
        var barrier = new TwoCallerBarrier();
        var manager = new DeterministicWorkspaceManager { ReserveBarrier = barrier.WaitAsync };
        var task = (await seedRepository.ListRecentAsync(1)).Single();
        var first = Service(firstRepository, manager, new FakeImplementationEngine());
        var second = Service(secondRepository, manager, new FakeImplementationEngine());

        var attempts = await Task.WhenAll(CaptureAsync(() => first.GenerateImplementationAsync(task.Id)),
            CaptureAsync(() => second.GenerateImplementationAsync(task.Id)));
        var persisted = await seedRepository.GetAsync(task.Id);

        Assert.Single(attempts, exception => exception is null);
        Assert.Single(attempts, exception => exception is TaskConcurrencyException);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, persisted?.Status);
        Assert.NotNull(persisted?.ImplementationResult);
        Assert.Null(persisted?.ImplementationLease);
    }

    [Fact]
    public async Task Cancellation_during_exact_output_preflight_leaves_the_approved_task_untouched()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner);
        var manager = new DeterministicWorkspaceManager();
        using var source = new CancellationTokenSource();
        var service = Service(repository, manager, new CancellingEngine(source.Cancel));
        var task = (await inner.ListRecentAsync(1)).Single();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateImplementationAsync(task.Id, source.Token));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal(WorkflowStatus.PlanApproved, persisted?.Status);
        Assert.Null(persisted?.LastImplementationFailure);
        Assert.Null(persisted?.ImplementationWorkspace);
        Assert.Null(persisted?.ImplementationLease);
    }

    [Fact]
    public async Task Cancellation_before_workspace_reservation_leaves_the_approved_task_untouched()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner);
        using var source = new CancellationTokenSource();
        var manager = new DeterministicWorkspaceManager { BeforeReserve = source.Cancel };
        var service = Service(repository, manager, new FakeImplementationEngine());
        var task = (await inner.ListRecentAsync(1)).Single();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateImplementationAsync(task.Id, source.Token));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal(WorkflowStatus.PlanApproved, persisted?.Status);
        Assert.Null(persisted?.ImplementationWorkspace);
        Assert.Null(persisted?.ImplementationLease);
        Assert.Null(persisted?.LastImplementationFailure);
    }

    [Fact]
    public async Task Terminal_fake_preflight_failure_leaves_plan_approved_without_lease_or_resume_state()
    {
        var inner = await SeedAsync();
        var manager = new DeterministicWorkspaceManager
        {
            ReserveFailure = new ImplementationException("implementation_terminal_incompatibility",
                "The approved deterministic input is incompatible.")
        };
        var task = (await inner.ListRecentAsync(1)).Single();

        var failure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(new FailOnSaveRepository(inner), manager, new FakeImplementationEngine())
                .GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal("implementation_terminal_incompatibility", failure.Category);
        Assert.Equal(WorkflowStatus.PlanApproved, persisted?.Status);
        Assert.Null(persisted?.ImplementationWorkspace);
        Assert.Null(persisted?.ImplementationLease);
        Assert.Null(persisted?.LastImplementationFailure);
    }

    [Fact]
    public async Task Pretty_printed_json_over_per_file_limit_is_rejected_before_implementation_state_exists()
    {
        var original = "{\"values\":[" + string.Join(',', Enumerable.Range(1, 80)) + "]}";
        var file = Context("config/settings.json", original);
        var generated = await new FakeImplementationEngine().GenerateAsync(new ImplementationContext(
            "Approved", PlanFor([file]), [file], Now));
        var generatedLength = Assert.Single(generated.Output.Operations).Content!.Length;
        Assert.True(generatedLength > original.Length);

        await AssertFakePreflightRejectedAsync([file], new ImplementationLimits
        {
            MaximumGeneratedFileCharacters = generatedLength - 1
        });
    }

    [Fact]
    public async Task Multiple_json_outputs_over_total_limit_are_rejected_before_implementation_state_exists()
    {
        var files = new[]
        {
            Context("config/first.json", "{\"value\":1}"),
            Context("config/second.json", "{\"value\":2}")
        };
        var output = (await new FakeImplementationEngine().GenerateAsync(new ImplementationContext(
            "Approved", PlanFor(files), files, Now))).Output;
        var total = output.Operations.Sum(operation => operation.Content?.Length ?? 0);

        await AssertFakePreflightRejectedAsync(files, new ImplementationLimits
        {
            MaximumTotalGeneratedCharacters = total - 1
        });
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"forgeDeterministicFake\":true}")]
    public async Task Exact_json_incompatibility_leaves_plan_approved_without_resume_artifacts(string original)
    {
        await AssertFakePreflightRejectedAsync([Context("config/settings.json", original)],
            new ImplementationLimits());
    }

    [Fact]
    public async Task Cancellation_during_workspace_preparation_is_recovery_required()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner);
        using var source = new CancellationTokenSource();
        var manager = new DeterministicWorkspaceManager { BeforePrepare = source.Cancel };
        var service = Service(repository, manager, new FakeImplementationEngine());
        var task = (await inner.ListRecentAsync(1)).Single();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateImplementationAsync(task.Id, source.Token));
        var persisted = await inner.GetAsync(task.Id);

        Assert.True(persisted?.LastImplementationFailure?.RecoveryRequired);
        Assert.False(persisted?.LastImplementationFailure?.SafeToResume);
        Assert.Equal(ImplementationWorkspacePhase.RecoveryRequired, persisted?.ImplementationWorkspace?.Phase);
    }

    [Fact]
    public async Task Active_checkout_postcondition_failure_is_persisted_as_recovery_required()
    {
        var inner = await SeedAsync();
        var repository = new FailOnSaveRepository(inner);
        var manager = new DeterministicWorkspaceManager { FailActiveCheckoutVerification = true };
        var service = Service(repository, manager, new FakeImplementationEngine());
        var task = (await inner.ListRecentAsync(1)).Single();

        await Assert.ThrowsAsync<ImplementationException>(() => service.GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);

        Assert.True(persisted?.LastImplementationFailure?.RecoveryRequired);
        Assert.False(persisted?.LastImplementationFailure?.ActiveCheckoutVerified);
        Assert.Equal(ImplementationWorkspacePhase.RecoveryRequired, persisted?.ImplementationWorkspace?.Phase);
    }

    private async Task AssertFakePreflightRejectedAsync(
        IReadOnlyList<ImplementationFileContext> files,
        ImplementationLimits limits)
    {
        var task = ApprovedTask(PlanFor(files));
        var inner = await SeedAsync(task);
        var manager = new DeterministicWorkspaceManager { PreflightFiles = files };

        await Assert.ThrowsAsync<ImplementationException>(() =>
            Service(new FailOnSaveRepository(inner), manager, new FakeImplementationEngine(), limits)
                .GenerateImplementationAsync(task.Id));
        var persisted = await inner.GetAsync(task.Id);

        Assert.Equal(WorkflowStatus.PlanApproved, persisted?.Status);
        Assert.Null(persisted?.ImplementationWorkspace);
        Assert.Null(persisted?.ImplementationLease);
        Assert.Null(persisted?.LastImplementationFailure);
        Assert.Equal(0, manager.PrepareCalls);
    }

    private async Task<SqliteEngineeringTaskRepository> SeedAsync(EngineeringTask? task = null)
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        await repository.SaveAsync(task ?? ApprovedTask());
        return repository;
    }

    private static EngineeringTaskService Service(
        IEngineeringTaskRepository repository,
        IImplementationWorkspaceManager manager,
        IImplementationEngine engine,
        ImplementationLimits? limits = null) => new(
        repository,
        new NullClarificationEngine(),
        new FixedTimeProvider(Now.AddMinutes(1)),
        implementationEngine: engine,
        implementationWorkspaceManager: manager,
        implementationLimits: limits ?? new ImplementationLimits(),
        implementationCoordinator: new ImplementationOperationCoordinator(),
        implementationProcessIdentity: new ImplementationProcessIdentity(Guid.NewGuid()));

    private static async Task<Exception?> CaptureAsync(Func<Task<EngineeringTask>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static EngineeringTask ApprovedTask()
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.BeginRepositoryAnalysis(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), Now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(Now);
        return task;
    }

    private static EngineeringTask ApprovedTask(ImplementationPlan plan)
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = SnapshotFor(plan.AffectedFiles);
        var evidence = EvidenceFor(plan.AffectedFiles);
        task.BeginRepositoryAnalysis(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection(evidence, evidence.Count, evidence.Count,
            evidence.Sum(item => item.Excerpt.Length)), Now);
        task.StoreImplementationPlan(plan with { RepositoryFingerprint = snapshot.Fingerprint }, Now,
            TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(Now);
        return task;
    }

    private static ImplementationPlan PlanFor(IReadOnlyList<ImplementationFileContext> files)
    {
        var affected = files.Select((file, index) => new PlannedFileChange(file.Path, file.PlannedAction,
            "Apply the deterministic fixture.", file.PlannedAction == PlannedFileAction.Create ? [] : [$"E{index + 1}"], .9m)).ToArray();
        var snapshot = SnapshotFor(affected);
        return new ImplementationPlan("Deterministic preflight", "Exercise exact Fake output validation.",
            "The approved files are available from the bounded snapshot.", affected,
            [new ImplementationStep(1, "Apply deterministic fixture changes.", affected.Select(file => file.Path).ToArray(),
                affected.SelectMany(file => file.EvidenceIds).Distinct().ToArray(), "The approved files change.")],
            [], [], [], [], [new RequirementCoverageItem("Apply deterministic fixture changes.",
                affected.Select(file => file.Path).ToArray(), [1])], "A bounded deterministic preflight plan.",
            PlanningSource.DeterministicFake, null, Now, snapshot.Fingerprint);
    }

    private static RepositorySnapshot SnapshotFor(IReadOnlyList<PlannedFileChange> files) =>
        PlanningWorkflowTests.Snapshot(Now) with
        {
            TotalDiscoveredFiles = files.Count,
            EligibleTextFileCount = files.Count,
            DetectedExtensions = files.Select(file => Path.GetExtension(file.Path)).Distinct().ToArray(),
            Files = files.Where(file => file.Action != PlannedFileAction.Create).Select(file =>
                new RepositoryFileMetadata(file.Path, Path.GetExtension(file.Path), 20, 1, "source", false,
                    null, [])).ToArray(),
            Fingerprint = "preflight-fingerprint"
        };

    private static IReadOnlyList<EvidenceItem> EvidenceFor(IReadOnlyList<PlannedFileChange> files) =>
        files.Select((file, index) => (file, index)).Where(item => item.file.Action != PlannedFileAction.Create)
            .Select(item => new EvidenceItem($"E{item.index + 1}", item.file.Path, 1, 1, "fixture content",
                "Direct approved-file evidence.", 20, "hash")).ToArray();

    private static ImplementationFileContext Context(string path, string original) => new(
        path, PlannedFileAction.Modify, original, ImplementationOutputValidator.Hash(original));

    private sealed class FailOnSaveRepository(
        SqliteEngineeringTaskRepository inner,
        int? failOnSave = null,
        bool failPersistently = false) : IEngineeringTaskRepository
    {
        private int saves;
        public Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(int maximumCount,
            CancellationToken cancellationToken = default) => inner.ListRecentAsync(maximumCount, cancellationToken);
        public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);
        public Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            saves++;
            return failOnSave is not null && (saves == failOnSave || failPersistently && saves >= failOnSave)
                ? Task.FromException(new IOException("Injected SQLite save failure."))
                : inner.SaveAsync(task, cancellationToken);
        }
    }

    private sealed class ProviderLockingRepository(
        SqliteEngineeringTaskRepository inner,
        string databasePath,
        int lockOnSave,
        bool releaseAfterFailure) : IEngineeringTaskRepository, IDisposable
    {
        private SqliteConnection? lockConnection;
        private int saves;
        public int LockedReadAttempts { get; private set; }

        public Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(int maximumCount,
            CancellationToken cancellationToken = default) => inner.ListRecentAsync(maximumCount, cancellationToken);

        public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (lockConnection is not null) LockedReadAttempts++;
            return inner.GetAsync(id, cancellationToken);
        }

        public async Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            saves++;
            if (saves == lockOnSave) Acquire();
            try
            {
                await inner.SaveAsync(task, cancellationToken);
            }
            catch (TaskPersistenceException)
            {
                if (releaseAfterFailure) Release();
                throw;
            }
        }

        private void Acquire()
        {
            lockConnection = new SqliteConnection($"Data Source={databasePath};Default Timeout=1;Pooling=False");
            lockConnection.Open();
            using var command = lockConnection.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE;";
            command.ExecuteNonQuery();
        }

        public void Release()
        {
            if (lockConnection is null) return;
            using var command = lockConnection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
            lockConnection.Dispose();
            lockConnection = null;
        }

        public void Dispose() => Release();
    }

    private sealed class CancellingEngine(Action cancel) : IImplementationEngine
    {
        public Task<ImplementationEvaluation> GenerateAsync(ImplementationContext context,
            CancellationToken cancellationToken = default)
        {
            cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }

    private sealed class SensitiveEngine(string value) : IImplementationEngine
    {
        public Task<ImplementationEvaluation> GenerateAsync(ImplementationContext context,
            CancellationToken cancellationToken = default)
        {
            var file = Assert.Single(context.Files);
            var output = new ImplementationOutput("Summary", [],
                [new ImplementationOperation(file.Path, ImplementationOperationAction.Modify,
                    file.OriginalContentSha256, $"api_key={value}\n", "Mechanical fixture.")],
                ImplementationSource.DeterministicFake, null);
            return Task.FromResult(new ImplementationEvaluation(output));
        }
    }

    private sealed class NullClarificationEngine : IClarificationEngine
    {
        public Task<ClarificationEvaluation> EvaluateAsync(EngineeringTask task,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DeterministicWorkspaceManager : IImplementationWorkspaceManager
    {
        private static readonly string Token = new('a', 32);
        public bool FailActiveCheckoutVerification { get; init; }
        public Action? BeforeReserve { get; init; }
        public Action? BeforePrepare { get; init; }
        public Func<CancellationToken, Task>? ReserveBarrier { get; init; }
        public ImplementationException? ReserveFailure { get; init; }
        public ImplementationException? ApplyFailure { get; init; }
        public bool ApplyCalled { get; private set; }
        public bool FailSecondResultVerification { get; init; }
        public IReadOnlyList<ImplementationFileContext>? PreflightFiles { get; init; }
        public int PrepareCalls { get; private set; }
        private int resultVerifications;

        public async Task<ImplementationReservation> ReserveAsync(Guid taskId, string repositoryPath,
            RepositorySnapshot snapshot, ImplementationPlan plan, CancellationToken cancellationToken = default)
        {
            BeforeReserve?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (ReserveFailure is not null) throw ReserveFailure;
            if (ReserveBarrier is not null) await ReserveBarrier(cancellationToken);
            var workspace = new ImplementationWorkspace(Token, $"forge/task-{Token}", new string('b', 40),
                ImplementationWorkspacePhase.Reserved, Now, Now, true, new string('1', 64),
                new string('2', 64), $"refs/forge/tasks/{Token}");
            const string original = "public class App { }\n";
            var preflight = PreflightFiles ??
                [new ImplementationFileContext("src/App.cs", PlannedFileAction.Modify, original,
                    ImplementationOutputValidator.Hash(original))];
            return new ImplementationReservation(workspace,
                new ActiveCheckoutSignature("main", new string('b', 40), "status", "index"),
                preflight);
        }

        public Task<PreparedImplementationWorkspace> PrepareAsync(string repositoryPath,
            ImplementationWorkspace workspace, ImplementationPlan plan, ImplementationLimits limits,
            ActiveCheckoutSignature activeCheckout, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            BeforePrepare?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            const string original = "public class App { }\n";
            var files = PreflightFiles ??
                [new ImplementationFileContext("src/App.cs", PlannedFileAction.Modify, original,
                    ImplementationOutputValidator.Hash(original))];
            return Task.FromResult(new PreparedImplementationWorkspace(
                workspace with { Phase = ImplementationWorkspacePhase.Ready, IsAvailable = true },
                activeCheckout,
                files,
                new TestWorkspaceLock()));
        }

        public Task<ImplementationResult> ApplyAsync(string repositoryPath,
            PreparedImplementationWorkspace prepared, ImplementationOutput output, ImplementationLimits limits,
            DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            if (ApplyFailure is not null) throw ApplyFailure;
            var operation = Assert.Single(output.Operations);
            const string diff = "diff";
            var file = Assert.Single(prepared.Files);
            return Task.FromResult(new ImplementationResult(output.Source, output.Model,
                prepared.Workspace.BaseCommitSha, prepared.Workspace.Branch, output.Summary, output.Warnings,
                [new ChangedFileReview(operation.Path, operation.Action, operation.OriginalContentSha256,
                    ImplementationOutputValidator.Hash(operation.Content!),
                    System.Text.Encoding.UTF8.GetByteCount(file.OriginalContent!),
                    System.Text.Encoding.UTF8.GetByteCount(operation.Content!), 1, 2, 1, 0,
                    diff, diff.Length, diff.Length, false, diff.Length, diff.Length)],
                diff.Length, diff.Length, false, completedAt, diff.Length, diff.Length, true));
        }

        public Task<bool> IsAvailableAsync(string repositoryPath, ImplementationWorkspace workspace,
            ImplementationPlan plan, ImplementationResult? result, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task VerifyResultAsync(string repositoryPath, PreparedImplementationWorkspace prepared,
            ImplementationResult result, CancellationToken cancellationToken = default)
        {
            resultVerifications++;
            return FailSecondResultVerification && resultVerifications == 2
                ? Task.FromException(new ImplementationException("implementation_workspace_drift",
                    "The isolated implementation workspace changed.", true))
                : Task.CompletedTask;
        }

        public Task VerifyActiveCheckoutAsync(string repositoryPath, ImplementationPlan plan,
            ActiveCheckoutSignature expected, CancellationToken cancellationToken = default) => FailActiveCheckoutVerification
            ? Task.FromException(new ImplementationException("implementation_active_checkout_changed",
                "The active checkout postcondition could not be verified.", true))
            : Task.CompletedTask;
    }

    private sealed class TestWorkspaceLock : IImplementationWorkspaceLock
    {
        public bool IsHeld { get; private set; } = true;
        public ValueTask DisposeAsync()
        {
            IsHeld = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TwoCallerBarrier
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref count) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
