using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class ImplementationWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Approved_plan_transitions_through_implementing_to_diff_review()
    {
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        Assert.Equal(WorkflowStatus.Implementing, task.Status);

        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));

        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, task.Status);
        Assert.NotNull(task.ImplementationResult);
        Assert.Equal(ImplementationWorkspacePhase.ResultPersisted, task.ImplementationWorkspace?.Phase);
        var revision = Assert.Single(task.ImplementationRevisions);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(ImplementationRevisionKind.Initial, revision.Kind);
        Assert.Equal(ImplementationGenerationState.Succeeded, revision.GenerationState);
        Assert.Equal(ImplementationReviewState.Current, revision.ReviewState);
        Assert.Equal(revision.RevisionId, task.ActiveImplementationRevisionId);
        Assert.Matches("^[0-9a-f]{64}$", revision.PlanFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", revision.ResultFingerprint!);
    }

    [Fact]
    public void Exact_current_review_can_be_approved_once_and_replayed_idempotently()
    {
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        var revision = Assert.Single(task.ImplementationRevisions);
        var commandId = Guid.NewGuid();

        Assert.True(task.ApproveImplementation(commandId, task.RowVersion, revision.RevisionId,
            revision.ResultFingerprint!, Now.AddMinutes(3)));
        var approved = Assert.Single(task.ImplementationRevisions);
        Assert.Equal(WorkflowStatus.ImplementationApproved, task.Status);
        Assert.Equal(ImplementationReviewState.Approved, approved.ReviewState);
        Assert.Equal(approved.RevisionId, task.ApprovedImplementationRevisionId);
        Assert.Equal(Now.AddMinutes(3), approved.ApprovedAt);

        Assert.False(task.ApproveImplementation(commandId, 0, approved.RevisionId,
            approved.ResultFingerprint!, Now.AddMinutes(4)));
        Assert.Equal(Now.AddMinutes(3), Assert.Single(task.ImplementationRevisions).ApprovedAt);
        Assert.Throws<WorkflowException>(() => task.ApproveImplementation(commandId, 1,
            approved.RevisionId, approved.ResultFingerprint!, Now.AddMinutes(4)));
        Assert.Throws<WorkflowException>(() => task.ApproveImplementation(Guid.NewGuid(), task.RowVersion,
            approved.RevisionId, approved.ResultFingerprint!, Now.AddMinutes(4)));
    }

    [Fact]
    public void Approval_rejects_stale_row_revision_and_result_fingerprint()
    {
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        var revision = Assert.Single(task.ImplementationRevisions);

        Assert.Throws<TaskConcurrencyException>(() => task.ApproveImplementation(Guid.NewGuid(), 1,
            revision.RevisionId, revision.ResultFingerprint!, Now.AddMinutes(3)));
        Assert.Throws<TaskConcurrencyException>(() => task.ApproveImplementation(Guid.NewGuid(), task.RowVersion,
            Guid.NewGuid(), revision.ResultFingerprint!, Now.AddMinutes(3)));
        Assert.Throws<TaskConcurrencyException>(() => task.ApproveImplementation(Guid.NewGuid(), task.RowVersion,
            revision.RevisionId, new string('0', 64), Now.AddMinutes(3)));
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, task.Status);
    }

    [Fact]
    public void Approval_rejects_uncertain_checkout_evidence_without_mutating_review()
    {
        var task = ApprovedTask();
        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        task.RecordImplementationPostconditionFailure(new ImplementationFailure(
            "implementation_active_checkout_changed",
            "Forge could not verify that the active checkout remained unchanged.",
            true,
            Now.AddMinutes(3),
            ActiveCheckoutVerified: false), Now.AddMinutes(3));
        var revision = Assert.Single(task.ImplementationRevisions);
        var before = System.Text.Json.JsonSerializer.Serialize(task);

        var exception = Assert.Throws<ImplementationException>(() => task.ApproveImplementation(
            Guid.NewGuid(), task.RowVersion, revision.RevisionId, revision.ResultFingerprint!, Now.AddMinutes(4)));

        Assert.Equal("implementation_review_ineligible", exception.Category);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(task));
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, task.Status);
        Assert.Null(task.ApprovedImplementationRevisionId);
    }

    [Fact]
    public void Implementation_approval_is_forbidden_before_and_after_the_review_gate()
    {
        var before = ApprovedTask();
        Assert.Throws<WorkflowException>(() => before.ApproveImplementation(Guid.NewGuid(), before.RowVersion,
            Guid.NewGuid(), new string('a', 64), Now));

        var workspace = Workspace();
        var lease = Lease(Now.AddMinutes(1));
        before.BeginImplementation(workspace, lease, Now.AddMinutes(1));
        before.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now.AddMinutes(2));
        var revision = Assert.Single(before.ImplementationRevisions);
        before.ApproveImplementation(Guid.NewGuid(), before.RowVersion, revision.RevisionId,
            revision.ResultFingerprint!, Now.AddMinutes(3));
        Assert.Throws<WorkflowException>(() => before.ApproveImplementation(Guid.NewGuid(), before.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!, Now.AddMinutes(4)));
        Assert.Throws<WorkflowException>(() => before.RequestPlanRevision("Change the approved plan.", Now.AddMinutes(4)));
    }

    [Fact]
    public void Canonical_result_fingerprint_binds_every_review_surface()
    {
        var taskId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var workspace = Workspace();
        const string preview = "diff --git a/src/App.cs b/src/App.cs";
        var result = Result(workspace) with
        {
            ChangedFiles = [new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify,
                new string('3', 64), new string('4', 64), 10, 20, 1, 2, 1, 0,
                preview, preview.Length, preview.Length, false, preview.Length, preview.Length)],
            FullDiffCharacters = preview.Length,
            DisplayedDiffCharacters = preview.Length,
            FullDiffUtf8Bytes = preview.Length,
            DisplayedDiffUtf8Bytes = preview.Length,
            WorktreeFingerprint = new string('5', 64),
            WorktreeFileCount = 1,
            WorktreeBytes = 20
        };
        var planFingerprint = new string('a', 64);
        var baseline = ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 1,
            ImplementationRevisionKind.Initial, planFingerprint, result);
        var file = Assert.Single(result.ChangedFiles);
        var mutations = new ImplementationResult[]
        {
            result with { Source = ImplementationSource.OpenAI },
            result with { Model = "model" },
            result with { BaseCommitSha = new string('b', 40) },
            result with { Branch = result.Branch + "-changed" },
            result with { Summary = result.Summary + " changed" },
            result with { Warnings = ["changed"] },
            result with { ChangedFiles = [file with { Path = "src/Other.cs" }] },
            result with { ChangedFiles = [file with { Action = ImplementationOperationAction.Create }] },
            result with { ChangedFiles = [file with { OriginalContentSha256 = new string('1', 64) }] },
            result with { ChangedFiles = [file with { NewContentSha256 = new string('2', 64) }] },
            result with { ChangedFiles = [file with { OriginalBytes = file.OriginalBytes + 1 }] },
            result with { ChangedFiles = [file with { NewBytes = file.NewBytes + 1 }] },
            result with { ChangedFiles = [file with { OriginalLines = file.OriginalLines + 1 }] },
            result with { ChangedFiles = [file with { NewLines = file.NewLines + 1 }] },
            result with { ChangedFiles = [file with { Additions = file.Additions + 1 }] },
            result with { ChangedFiles = [file with { Deletions = file.Deletions + 1 }] },
            result with { ChangedFiles = [file with { DiffPreview = file.DiffPreview + "x" }] },
            result with { ChangedFiles = [file with { FullDiffCharacters = file.FullDiffCharacters + 1 }] },
            result with { ChangedFiles = [file with { DisplayedDiffCharacters = file.DisplayedDiffCharacters + 1 }] },
            result with { ChangedFiles = [file with { DiffTruncated = !file.DiffTruncated }] },
            result with { ChangedFiles = [file with { FullDiffUtf8Bytes = file.FullDiffUtf8Bytes + 1 }] },
            result with { ChangedFiles = [file with { DisplayedDiffUtf8Bytes = file.DisplayedDiffUtf8Bytes + 1 }] },
            result with { FullDiffCharacters = result.FullDiffCharacters + 1 },
            result with { DisplayedDiffCharacters = result.DisplayedDiffCharacters + 1 },
            result with { DiffTruncated = !result.DiffTruncated },
            result with { FullDiffUtf8Bytes = result.FullDiffUtf8Bytes + 1 },
            result with { DisplayedDiffUtf8Bytes = result.DisplayedDiffUtf8Bytes + 1 },
            result with { CompletedAt = result.CompletedAt.AddSeconds(1) },
            result with { ActiveCheckoutVerified = !result.ActiveCheckoutVerified },
            result with { WorktreeFingerprint = new string('3', 64) },
            result with { WorktreeFileCount = result.WorktreeFileCount + 1 },
            result with { WorktreeBytes = result.WorktreeBytes + 1 }
        };

        Assert.All(mutations, mutation => Assert.NotEqual(baseline,
            ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 1,
                ImplementationRevisionKind.Initial, planFingerprint, mutation)));
        Assert.NotEqual(baseline, ImplementationReviewFingerprint.ComputeResult(taskId, Guid.NewGuid(), 1,
            ImplementationRevisionKind.Initial, planFingerprint, result));
        Assert.NotEqual(baseline, ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 2,
            ImplementationRevisionKind.Initial, planFingerprint, result));
        Assert.NotEqual(baseline, ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 1,
            ImplementationRevisionKind.Initial, new string('b', 64), result));
        Assert.NotEqual(baseline, ImplementationReviewFingerprint.ComputeResult(Guid.NewGuid(), revisionId, 1,
            ImplementationRevisionKind.Initial, planFingerprint, result));
        Assert.NotEqual(baseline, ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 1,
            ImplementationRevisionKind.Correction, planFingerprint, result));

        var secondFile = file with { Path = "src/Second.cs", NewContentSha256 = new string('6', 64) };
        var ordered = result with { ChangedFiles = [file, secondFile] };
        var reversed = ordered with { ChangedFiles = [secondFile, file] };
        Assert.NotEqual(
            ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 1,
                ImplementationRevisionKind.Initial, planFingerprint, ordered),
            ImplementationReviewFingerprint.ComputeResult(taskId, revisionId, 1,
                ImplementationRevisionKind.Initial, planFingerprint, reversed));
    }

    [Fact]
    public void Implementation_requires_plan_approval_and_matching_result_workspace()
    {
        var unapproved = EngineeringTask.Create("C:/repo", "Requirement", Now);
        Assert.Throws<WorkflowException>(() => unapproved.BeginImplementation(Workspace(), Lease(Now), Now));

        var task = ApprovedTask();
        var lease = Lease(Now);
        task.BeginImplementation(Workspace(), lease, Now);
        Assert.Throws<WorkflowException>(() => task.StoreImplementationResult(
            Result(Workspace()) with { Branch = "forge/task-other" }, lease.AttemptId, lease.OwnerId, Now));
    }

    [Fact]
    public void Legacy_implementing_rehydrates_as_plan_approved_but_new_workspace_remains_implementing()
    {
        var approved = ApprovedTask();
        var legacy = Rehydrate(approved, null);
        var current = Rehydrate(approved, Workspace());

        Assert.Equal(WorkflowStatus.PlanApproved, legacy.Status);
        Assert.Equal(WorkflowStatus.Implementing, current.Status);
    }

    [Fact]
    public void Valid_output_requires_exact_approved_operations_and_hashes()
    {
        var (plan, files, output) = ValidOutput();
        ImplementationOutputValidator.Validate(plan, files, output, new ImplementationLimits());

        var missing = output with { Operations = output.Operations.Take(1).ToArray() };
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, missing, new ImplementationLimits()));
        var duplicate = output with { Operations = [output.Operations[0], output.Operations[0], output.Operations[2]] };
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, duplicate, new ImplementationLimits()));
        var undeclared = output with { Operations = output.Operations.Select((item, index) => index == 0 ? item with { Path = "src/Other.cs" } : item).ToArray() };
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, undeclared, new ImplementationLimits()));
    }

    [Fact]
    public void Validator_rejects_action_hash_noop_inspect_and_content_limits()
    {
        var (plan, files, output) = ValidOutput();
        var modify = output.Operations[0];
        AssertInvalid(modify with { Action = ImplementationOperationAction.Delete });
        AssertInvalid(modify with { OriginalContentSha256 = new string('0', 64) });
        AssertInvalid(modify with { Content = files[0].OriginalContent });
        AssertInvalid(modify with { Path = "../App.cs" });
        var createWithExistingBytes = output with
        {
            Operations = output.Operations.Select((item, index) =>
                index == 1 ? item with { ExpectedOriginalUtf8Bytes = 1 } : item).ToArray()
        };
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(
            plan, files, createWithExistingBytes, new ImplementationLimits()));

        var inspectPlan = plan with { AffectedFiles = [.. plan.AffectedFiles, new PlannedFileChange("src/Inspect.cs", PlannedFileAction.Inspect, "Inspect.", ["E1"], .5m)] };
        var inspectOutput = output with { Operations = [.. output.Operations, new ImplementationOperation("src/Inspect.cs", ImplementationOperationAction.Modify, "hash", "changed", "No.")] };
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(inspectPlan, files, inspectOutput, new ImplementationLimits()));

        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files,
            output with { Operations = output.Operations.Select((item, index) => index == 0 ? item with { Content = new string('x', 11) } : item).ToArray() },
            new ImplementationLimits { MaximumGeneratedFileCharacters = 10 }));

        void AssertInvalid(ImplementationOperation replacement)
        {
            var changed = output with { Operations = output.Operations.Select((item, index) => index == 0 ? replacement : item).ToArray() };
            Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, changed, new ImplementationLimits()));
        }
    }

    [Fact]
    public void Validator_enforces_operation_total_path_summary_warning_and_generated_total_limits()
    {
        var (plan, files, output) = ValidOutput();
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, output,
            new ImplementationLimits { MaximumApprovedOperations = 2 }));
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files,
            output with { Summary = new string('s', 11) }, new ImplementationLimits { MaximumSummaryCharacters = 10 }));
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files,
            output with { Warnings = ["one", "two"] }, new ImplementationLimits { MaximumWarnings = 1 }));
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files,
            output with { Operations = output.Operations.Select(item => item with { Summary = new string('s', 11) }).ToArray() },
            new ImplementationLimits { MaximumItemSummaryCharacters = 10 }));
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, output,
            new ImplementationLimits { MaximumTotalGeneratedCharacters = 5 }));
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files, output,
            new ImplementationLimits { MaximumRelativePathCharacters = 5 }));
        Assert.Throws<ImplementationException>(() => ImplementationOutputValidator.Validate(plan, files,
            output with { Source = ImplementationSource.OpenAI, Model = new string('m', 161) },
            new ImplementationLimits()));
    }

    [Fact]
    public void Validator_rejects_every_sensitive_implementation_output_text_surface_without_echoing_the_value()
    {
        var (plan, files, output) = ValidOutput();
        var value = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                    Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "Aa1-";
        var labelled = $"deployment credential: {value}";
        var variants = new[]
        {
            output with { Summary = labelled },
            output with { Warnings = [labelled] },
            output with { Operations = output.Operations.Select((operation, index) =>
                index == 0 ? operation with { Summary = labelled } : operation).ToArray() },
            output with { Operations = output.Operations.Select((operation, index) =>
                index == 0 ? operation with { Content = $"// deployment credential: {value}\n" } : operation).ToArray() }
        };

        foreach (var variant in variants)
        {
            var failure = Assert.Throws<ImplementationException>(() =>
                ImplementationOutputValidator.Validate(plan, files, variant, new ImplementationLimits()));
            Assert.Equal("implementation_sensitive_content", failure.Category);
            Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Fake_engine_rejects_unsupported_modify_format_without_returning_telemetry()
    {
        const string original = "value\n";
        var plan = ValidOutput().Plan with
        {
            AffectedFiles = [new PlannedFileChange("src/value.bintext", PlannedFileAction.Modify, "Modify.", ["E1"], .8m)]
        };
        var context = new ImplementationContext("Approved", plan,
            [new ImplementationFileContext("src/value.bintext", PlannedFileAction.Modify, original, ImplementationOutputValidator.Hash(original))], Now);

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => new FakeImplementationEngine().GenerateAsync(context));

        Assert.Equal("implementation_terminal_incompatibility", exception.Category);
    }

    [Fact]
    public async Task Fake_engine_is_deterministic_labelled_and_records_no_model_call()
    {
        var (plan, files, _) = ValidOutput();
        var context = new ImplementationContext("Approved requirement", plan, files, Now);
        var first = await new FakeImplementationEngine().GenerateAsync(context);
        var second = await new FakeImplementationEngine().GenerateAsync(context);

        Assert.Equal(first.Output.Summary, second.Output.Summary);
        Assert.Equal(first.Output.Warnings, second.Output.Warnings);
        Assert.Equal(first.Output.Operations, second.Output.Operations);
        Assert.Null(first.ModelCall);
        Assert.Equal(ImplementationSource.DeterministicFake, first.Output.Source);
        Assert.Contains(first.Output.Warnings, warning => warning.Contains("not AI-authored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"value\":1}")]
    [InlineData("[1,2]")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Fake_json_modify_is_deterministic_for_every_valid_json_root(string original)
    {
        var plan = ValidOutput().Plan with
        {
            AffectedFiles = [new PlannedFileChange("src/value.json", PlannedFileAction.Modify, "Modify.", ["E1"], .8m)]
        };
        var context = new ImplementationContext("Approved", plan,
            [new ImplementationFileContext("src/value.json", PlannedFileAction.Modify, original,
                ImplementationOutputValidator.Hash(original))], Now);

        var result = await new FakeImplementationEngine().GenerateAsync(context);

        Assert.Contains("forgeDeterministicFake", Assert.Single(result.Output.Operations).Content!);
        Assert.Null(result.ModelCall);
    }

    [Theory]
    [InlineData("src/App.cs")]
    [InlineData("web/App.ts")]
    [InlineData("web/App.tsx")]
    [InlineData("web/app.js")]
    [InlineData("web/app.jsx")]
    [InlineData("settings/app.jsonc")]
    [InlineData("web/app.css")]
    [InlineData("web/app.scss")]
    [InlineData("web/index.html")]
    [InlineData("web/index.htm")]
    [InlineData("settings/app.xml")]
    [InlineData("src/Forge.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("data/value.json")]
    [InlineData("settings/app.config")]
    [InlineData("ForgeAI.slnx")]
    [InlineData("docs/readme.md")]
    [InlineData("scripts/run.ps1")]
    [InlineData("scripts/run.sh")]
    [InlineData("config/app.yml")]
    [InlineData("config/app.yaml")]
    [InlineData("config/app.toml")]
    [InlineData("docs/notes.txt")]
    [InlineData("db/change.sql")]
    [InlineData("scripts/run.cmd")]
    [InlineData("scripts/run.bat")]
    [InlineData("ForgeAI.sln")]
    [InlineData("Dockerfile")]
    [InlineData(".editorconfig")]
    public void Fake_capability_matrix_supports_every_declared_action_for_accepted_types(string path)
    {
        foreach (var action in new[] { PlannedFileAction.Create, PlannedFileAction.Modify, PlannedFileAction.Delete })
            Assert.True(Enum.IsDefined(FakeImplementationCapabilityMatrix.GetStyle(path, action)));
    }

    [Fact]
    public async Task Fake_marker_preserves_existing_crlf_style()
    {
        const string original = "public class App { }\r\n";
        var plan = ValidOutput().Plan with
        {
            AffectedFiles = [new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Modify.", ["E1"], .8m)]
        };
        var result = await new FakeImplementationEngine().GenerateAsync(new ImplementationContext(
            "Approved", plan, [new ImplementationFileContext("src/App.cs", PlannedFileAction.Modify,
                original, ImplementationOutputValidator.Hash(original))], Now));
        var content = Assert.Single(result.Output.Operations).Content!;
        Assert.DoesNotContain("\n", content.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../App.cs")]
    [InlineData("src/.git/config")]
    [InlineData("src/file.cs ")]
    [InlineData("src/file.cs.")]
    [InlineData("src/CON.txt")]
    [InlineData("src/CONIN$.txt")]
    [InlineData("src/CONOUT$.txt")]
    [InlineData("src/COM¹.txt")]
    [InlineData("src/LPT².txt")]
    [InlineData("-danger/file.cs")]
    [InlineData("src/-danger.cs")]
    [InlineData("C:relative.cs")]
    [InlineData("\\\\server\\share\\file.cs")]
    [InlineData("src//file.cs")]
    [InlineData("src/file.cs:stream")]
    public void Repository_paths_reject_aliases_traversal_and_repository_metadata(string path)
    {
        Assert.False(RepositoryPathRules.IsSafeRelativePath(path));
    }

    [Fact]
    public void Repository_paths_use_form_c_for_comparison_and_percent_encoding_is_literal()
    {
        const string composed = "src/café.cs";
        const string decomposed = "src/cafe\u0301.cs";
        Assert.True(RepositoryPathRules.IsSafeRelativePath(composed));
        Assert.False(RepositoryPathRules.IsSafeRelativePath(decomposed));
        Assert.Equal(composed, RepositoryPathRules.Normalize(decomposed));
        Assert.True(RepositoryPathRules.IsSafeRelativePath("src/%2e%2e/file.cs"));
    }

    [Theory]
    [InlineData("node_modules/package/index.js")]
    [InlineData("vendor/library.cs")]
    [InlineData("package-lock.json")]
    [InlineData("src/app.js.map")]
    [InlineData(".npmrc")]
    [InlineData("id_rsa.pub")]
    public void Shared_file_safety_rejects_dependencies_generated_outputs_and_likely_secrets(string path)
    {
        var policy = new RepositoryFileSafetyPolicy();
        Assert.True(policy.IsExcludedPath(path) || policy.IsGeneratedFile(path) || policy.IsSecretFile(path) ||
            policy.IsBinaryOrUnsupported(path));
    }

    private static (ImplementationPlan Plan, ImplementationFileContext[] Files, ImplementationOutput Output) ValidOutput()
    {
        const string original = "public class App { }\n";
        const string deleted = "delete me\n";
        var files = new[]
        {
            new ImplementationFileContext("src/App.cs", PlannedFileAction.Modify, original, ImplementationOutputValidator.Hash(original)),
            new ImplementationFileContext("docs/New.md", PlannedFileAction.Create, null, null),
            new ImplementationFileContext("docs/Delete.txt", PlannedFileAction.Delete, deleted, ImplementationOutputValidator.Hash(deleted))
        };
        var affected = new[]
        {
            new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Modify.", ["E1"], .9m),
            new PlannedFileChange("docs/New.md", PlannedFileAction.Create, "Create.", [], .8m),
            new PlannedFileChange("docs/Delete.txt", PlannedFileAction.Delete, "Delete.", ["E2"], .8m)
        };
        var snapshot = PlanningWorkflowTests.Snapshot(Now) with { FullHeadSha = "abc", WorkingTreeStatus = "clean" };
        var plan = new ImplementationPlan("Plan", "Objective", "Understanding", affected,
            [new ImplementationStep(1, "Change files.", affected.Select(item => item.Path).ToArray(), ["E1", "E2"], "Diff exists.")],
            [], [], [], [], [new RequirementCoverageItem("Change.", affected.Select(item => item.Path).ToArray(), [1])],
            "Summary", PlanningSource.DeterministicFake, null, Now, snapshot.Fingerprint);
        var output = new ImplementationOutput("Mechanical implementation.", ["Not AI-authored."],
            [
                new ImplementationOperation("src/App.cs", ImplementationOperationAction.Modify, files[0].OriginalContentSha256, original + "// marker\n", "Modify."),
                new ImplementationOperation("docs/New.md", ImplementationOperationAction.Create, null, "# placeholder\n", "Create."),
                new ImplementationOperation("docs/Delete.txt", ImplementationOperationAction.Delete, files[2].OriginalContentSha256, null, "Delete.")
            ], ImplementationSource.DeterministicFake, null);
        return (plan, files, output);
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

    private static ImplementationLease Lease(DateTimeOffset now) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now, now.AddMinutes(5));

    private static EngineeringTask Rehydrate(EngineeringTask task, ImplementationWorkspace? workspace) => EngineeringTask.Rehydrate(
        task.Id, task.Repository, task.OriginalRequirement, task.CurrentClarifiedRequirement,
        task.ClarificationAnswers, task.RequirementRevisionNotes, task.ModelCalls, task.CurrentPendingQuestion,
        task.RequirementSummary, WorkflowStatus.Implementing, task.CreatedAt, task.UpdatedAt,
        task.RequirementApprovedAt, task.PlanApprovedAt, task.RepositorySnapshot, task.EvidenceItems,
        task.EvidenceFilesInspected, task.EvidenceFilesSelected, task.TotalEvidenceCharacters,
        task.ImplementationPlan, task.RepositoryAnalyzedAt, task.RepositoryFingerprint, task.PlanCreatedAt,
        task.PlanRevisionNotes, workspace);

    private static ImplementationWorkspace Workspace() => new(
        new string('a', 32), "forge/task-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('a', 40),
        ImplementationWorkspacePhase.Reserved, Now, Now, false);

    private static ImplementationResult Result(ImplementationWorkspace workspace) => new(
        ImplementationSource.DeterministicFake, null, workspace.BaseCommitSha, workspace.Branch,
        "Summary", ["Warning"], [], 0, 0, false, Now.AddMinutes(2),
        ActiveCheckoutVerified: true);
}
