using System.Diagnostics;
using System.Security.Cryptography;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class GitWorktreeManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-worktree-repo-{Guid.NewGuid():N}");
    private readonly string _worktrees = Path.Combine(Path.GetTempPath(), $"forge-worktrees-{Guid.NewGuid():N}");

    [Fact]
    public async Task Fake_flow_changes_only_isolated_worktree_and_produces_truthful_diff()
    {
        InitializeRepository();
        var executionMarker = Path.Combine(Path.GetTempPath(), $"forge-git-execution-{Guid.NewGuid():N}.txt");
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "src/App.cs diff=unsafe\n");
        Git("add", ".gitattributes");
        Git("commit", "-m", "diff attributes");
        Git("config", "diff.unsafe.command", $"sh -c \"echo external > '{executionMarker.Replace('\\', '/')}'\"");
        Git("config", "diff.unsafe.textconv", $"sh -c \"echo textconv > '{executionMarker.Replace('\\', '/')}'\"");
        File.WriteAllText(GitPath("hooks/post-checkout"), $"#!/bin/sh\necho hook > '{executionMarker.Replace('\\', '/')}'\n");
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var taskId = Guid.NewGuid();
        var beforeFile = File.ReadAllBytes(Path.Combine(_root, "src", "App.cs"));
        var beforeIndex = HashFile(GitPath("index"));
        var beforeBranch = Git("branch", "--show-current").Trim();
        var beforeHead = Git("rev-parse", "HEAD").Trim();
        var beforeStatus = Git("status", "--porcelain=v1", "--untracked-files=all");

        var reservation = await manager.ReserveAsync(taskId, _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var evaluation = await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow));
        ImplementationOutputValidator.Validate(plan, prepared.Files, evaluation.Output, new ImplementationLimits());
        var result = await manager.ApplyAsync(_root, prepared, evaluation.Output, new ImplementationLimits(), DateTimeOffset.UtcNow);

        Assert.Equal(beforeFile, File.ReadAllBytes(Path.Combine(_root, "src", "App.cs")));
        Assert.Equal(beforeIndex, HashFile(GitPath("index")));
        Assert.Equal(beforeBranch, Git("branch", "--show-current").Trim());
        Assert.Equal(beforeHead, Git("rev-parse", "HEAD").Trim());
        Assert.Equal(beforeStatus, Git("status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Matches("^[0-9a-f]{64}$", reservation.ActiveCheckout.TrackedContentFingerprint);
        Assert.True(reservation.ActiveCheckout.TrackedFileCount > 0);
        Assert.Equal($"forge/task-{taskId:N}", result.Branch);
        Assert.Equal(3, result.ChangedFiles.Count);
        Assert.Contains(result.ChangedFiles, file => file.Path == "src/App.cs" && file.Action == ImplementationOperationAction.Modify && file.Additions > 0);
        Assert.Contains(result.ChangedFiles, file => file.Path == "docs/New.md" && file.Action == ImplementationOperationAction.Create && file.Additions > 0);
        Assert.Contains(result.ChangedFiles, file => file.Path == "docs/Delete.txt" && file.Action == ImplementationOperationAction.Delete && file.Deletions > 0);
        Assert.All(result.ChangedFiles, file => Assert.Contains("diff --git", file.DiffPreview, StringComparison.Ordinal));
        Assert.Matches("^[0-9a-f]{64}$", result.WorktreeFingerprint);
        Assert.False(File.Exists(executionMarker), "A repository hook, external diff, or textconv command executed.");
        await workspaceLock.DisposeAsync();
        Assert.True(await manager.IsAvailableAsync(_root, prepared.Workspace, plan, result));
        await File.AppendAllTextAsync(Path.Combine(_worktrees, prepared.Workspace.Token, "src", "App.cs"), "unexpected\n");
        Assert.False(await manager.IsAvailableAsync(_root, prepared.Workspace, plan, result));
        Git("worktree", "remove", "--force", Path.Combine(_worktrees, prepared.Workspace.Token));
        Assert.False(await manager.IsAvailableAsync(_root, prepared.Workspace, plan, result));
        Assert.NotEmpty(result.ChangedFiles);
    }

    [Fact]
    public async Task Non_git_dirty_untracked_and_stale_repositories_are_rejected_before_worktree_creation()
    {
        Directory.CreateDirectory(_root);
        var manager = Manager();
        var snapshot = EmptySnapshot();
        await Assert.ThrowsAsync<ImplementationException>(() => manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));

        InitializeRepository();
        snapshot = Snapshot();
        File.WriteAllText(Path.Combine(_root, "untracked.txt"), "dirty");
        var dirty = await Assert.ThrowsAsync<ImplementationException>(() => manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_repository_dirty", dirty.Category);
        File.Delete(Path.Combine(_root, "untracked.txt"));

        var stale = snapshot with { FullHeadSha = new string('0', snapshot.FullHeadSha!.Length) };
        var changed = await Assert.ThrowsAsync<ImplementationException>(() => manager.ReserveAsync(Guid.NewGuid(), _root, stale, Plan(stale)));
        Assert.Equal("implementation_base_changed", changed.Category);
        Assert.False(Directory.Exists(_worktrees) && Directory.EnumerateDirectories(_worktrees)
            .Any(path => Path.GetFileName(path).Length == 32));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("process")]
    [InlineData("smudge")]
    public async Task Every_named_filter_on_affected_path_is_rejected_without_execution(string filterCommand)
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "src/App.cs filter=unsafe\n");
        Git("add", ".gitattributes");
        Git("commit", "-m", "attributes");
        var hookMarker = Path.Combine(Path.GetTempPath(), $"forge-hook-{Guid.NewGuid():N}.txt");
        Git("config", $"filter.unsafe.{filterCommand}",
            $"sh -c \"echo filter > '{hookMarker.Replace('\\', '/')}'\"");
        var hook = GitPath("hooks/post-checkout");
        File.WriteAllText(hook, $"#!/bin/sh\necho ran > '{hookMarker.Replace('\\', '/')}'\n");
        var snapshot = Snapshot();
        var manager = Manager();
        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));

        Assert.Equal("implementation_git_filter", exception.Category);
        Assert.False(File.Exists(hookMarker));
    }

    [Fact]
    public async Task Set_filter_attribute_is_rejected_even_without_a_named_driver()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "src/App.cs filter\n");
        Git("add", ".gitattributes");
        Git("commit", "-m", "set filter attribute");

        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, Snapshot(), Plan(Snapshot())));

        Assert.Equal("implementation_git_filter", exception.Category);
    }

    [Fact]
    public async Task Create_target_inherits_parent_attributes_and_is_rejected_before_worktree_creation()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(_root, "docs", ".gitattributes"), "New.md filter=unsafe\n");
        Git("add", "--", "docs/.gitattributes");
        Git("commit", "-m", "scoped create attributes");
        var snapshot = Snapshot();
        var plan = Plan(snapshot);

        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, plan));

        Assert.Equal("implementation_git_filter", exception.Category);
        Assert.False(Directory.Exists(_worktrees) && Directory.EnumerateDirectories(_worktrees)
            .Any(path => Path.GetFileName(path).Length == 32));
    }

    [Fact]
    public async Task Runtime_workspace_availability_rechecks_filters_before_status_without_execution()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan,
            new ImplementationLimits(), reservation.ActiveCheckout);
        await prepared.WorkspaceLock.DisposeAsync();
        var marker = Path.Combine(Path.GetTempPath(), $"forge-runtime-filter-{Guid.NewGuid():N}.txt");
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "src/App.cs filter=unsafe\n");
        Git("config", "filter.unsafe.clean", $"sh -c \"echo ran > '{marker.Replace('\\', '/')}'\"");

        var available = await manager.IsAvailableAsync(_root, prepared.Workspace, plan, null);

        Assert.False(available);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Diff_preview_truncation_is_explicit_and_hashes_and_counts_remain_complete()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var limits = new ImplementationLimits { MaximumDiffPreviewCharactersPerFile = 20, MaximumDiffPreviewCharactersTotal = 30 };
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, limits, reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        ImplementationOutputValidator.Validate(plan, prepared.Files, output, limits);

        var result = await manager.ApplyAsync(_root, prepared, output, limits, DateTimeOffset.UtcNow);

        Assert.True(result.DiffTruncated);
        Assert.True(result.FullDiffCharacters > result.DisplayedDiffCharacters);
        Assert.Equal(30, result.DisplayedDiffCharacters);
        Assert.Equal(result.ChangedFiles.Sum(file => file.FullDiffUtf8Bytes), result.FullDiffUtf8Bytes);
        Assert.Equal(result.ChangedFiles.Sum(file => file.DisplayedDiffUtf8Bytes), result.DisplayedDiffUtf8Bytes);
        Assert.All(result.ChangedFiles.Skip(1), file => Assert.True(file.DiffTruncated));
        Assert.All(result.ChangedFiles, file => Assert.NotEqual(file.OriginalContentSha256, file.NewContentSha256));
    }

    [Fact]
    public void Diff_preview_truncation_never_splits_a_unicode_rune()
    {
        const string value = "abc😀def";
        var preview = GitWorktreeManager.TruncateDiffPreviewRuneSafe(value, 4);

        Assert.Equal("abc", preview);
        Assert.DoesNotContain(preview, character => char.IsSurrogate(character));
    }

    [Fact]
    public async Task Matching_untouched_workspace_can_resume_but_partial_workspace_requires_recovery()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var reservation = await Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await Manager().PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await prepared.WorkspaceLock.DisposeAsync();

        var resumed = await Manager().PrepareAsync(_root, prepared.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        var resumedLock = resumed.WorkspaceLock;
        Assert.Equal(prepared.Workspace.Token, resumed.Workspace.Token);
        Assert.Equal(prepared.Files.Select(file => file.Path), resumed.Files.Select(file => file.Path));
        await resumedLock.DisposeAsync();

        File.WriteAllText(Path.Combine(_worktrees, prepared.Workspace.Token, "unexpected.txt"), "partial");
        var exception = await Assert.ThrowsAsync<ImplementationException>(() => Manager().PrepareAsync(
            _root, resumed.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout));
        Assert.Equal("implementation_recovery_required", exception.Category);
        Assert.True(exception.RecoveryRequired);
    }

    [Fact]
    public async Task In_progress_git_operation_and_worktree_root_inside_repository_are_rejected()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        File.WriteAllText(GitPath("MERGE_HEAD"), snapshot.FullHeadSha);
        var state = await Assert.ThrowsAsync<ImplementationException>(() => Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_repository_state", state.Category);
        File.Delete(GitPath("MERGE_HEAD"));

        var insideManager = new GitWorktreeManager(new GitProcessRunner(new GitProcessOptions()),
            new RepositoryFileSafetyPolicy(), new ImplementationWorkspaceOptions { WorktreeRoot = Path.Combine(_root, ".forge-worktrees") });
        var configuration = await Assert.ThrowsAsync<ImplementationException>(() => insideManager.ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_workspace_configuration", configuration.Category);
    }

    [Fact]
    public async Task Conflicting_deterministic_branch_fails_closed()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var taskId = Guid.NewGuid();
        Git("commit", "--allow-empty", "-m", "other head");
        Git("branch", $"forge/task-{taskId:N}");
        Git("reset", "--hard", snapshot.FullHeadSha!);
        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(taskId, _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_workspace_conflict", exception.Category);
        Assert.False(Directory.Exists(Path.Combine(_worktrees, taskId.ToString("N"))));
    }

    [Fact]
    public async Task Matching_branch_without_ownership_marker_is_rejected()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var taskId = Guid.NewGuid();
        Git("branch", $"forge/task-{taskId:N}", snapshot.FullHeadSha!);
        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(taskId, _root, snapshot, Plan(snapshot)));

        Assert.Equal("implementation_workspace_conflict", exception.Category);
        Assert.False(Directory.Exists(Path.Combine(_worktrees, taskId.ToString("N"))));
        Assert.Equal(snapshot.FullHeadSha, Git("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, Git("status", "--porcelain=v1", "--untracked-files=all"));
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("dist/generated.cs")]
    [InlineData(".env")]
    [InlineData("assets/image.png")]
    public async Task Unsafe_create_file_classes_are_rejected_before_any_file_write(string path)
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = CreateOnlyPlan(snapshot, path);
        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, plan));

        Assert.Equal("implementation_terminal_incompatibility", exception.Category);
    }

    [Theory]
    [InlineData("120000")]
    [InlineData("160000")]
    public async Task Symlink_and_gitlink_modes_are_rejected(string mode)
    {
        InitializeRepository();
        var path = mode == "120000" ? "src/Link.cs" : "modules/Child.cs";
        var linkPayload = Path.Combine(_root, "link-target.txt");
        File.WriteAllText(linkPayload, "src/App.cs");
        var objectId = mode == "120000"
            ? Git("hash-object", "-w", "link-target.txt").Trim()
            : Git("rev-parse", "HEAD").Trim();
        File.Delete(linkPayload);
        Git("update-index", "--add", "--cacheinfo", mode, objectId, path);
        Git("commit", "-m", "unsafe mode");
        Git("reset", "--hard", "HEAD");
        var snapshot = Snapshot();
        var plan = ModifyOnlyPlan(snapshot, path);
        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, plan));

        Assert.Equal("implementation_unsupported_file", exception.Category);
    }

    [Fact]
    public async Task Executable_file_mode_is_rejected()
    {
        InitializeRepository();
        Git("update-index", "--chmod=+x", "src/App.cs");
        Git("commit", "-m", "executable mode");
        var snapshot = Snapshot();
        var executable = await Assert.ThrowsAsync<ImplementationException>(() => PrepareDefaultAsync(snapshot));
        Assert.Equal("implementation_unsupported_file", executable.Category);
    }

    [Fact]
    public async Task Sensitive_writable_context_is_rejected_instead_of_using_redacted_placeholder_content()
    {
        InitializeRepository();
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "App.cs"), "api_key = repository-secret\n");
        Git("add", "src/App.cs");
        Git("commit", "-m", "sensitive file");
        var snapshot = Snapshot();

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => PrepareDefaultAsync(snapshot));

        Assert.Equal("implementation_sensitive_content", exception.Category);
    }

    [Fact]
    public async Task Utf8_bom_writable_context_is_rejected_instead_of_changing_encoding_or_byte_counts()
    {
        InitializeRepository();
        await File.WriteAllBytesAsync(Path.Combine(_root, "src", "App.cs"),
            [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("public class App { }\n")]);
        Git("add", "src/App.cs");
        Git("commit", "-m", "bom file");
        var snapshot = Snapshot();

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => PrepareDefaultAsync(snapshot));

        Assert.Equal("implementation_unsupported_file", exception.Category);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"forgeDeterministicFake\":true}")]
    public async Task Json_content_dependencies_are_rejected_before_branch_worktree_or_owner_reservation(string content)
    {
        InitializeRepository();
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "settings.json"), content);
        Git("add", "--", "config/settings.json");
        Git("commit", "-m", "json input");
        var snapshot = Snapshot();
        var plan = ModifyOnlyPlan(snapshot, "config/settings.json");
        var taskId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(taskId, _root, snapshot, plan));

        Assert.Equal("implementation_terminal_incompatibility", exception.Category);
        Assert.Equal(string.Empty, Git("branch", "--list", $"forge/task-{taskId:N}").Trim());
        Assert.Equal(string.Empty, Git("for-each-ref", $"refs/forge/tasks/{taskId:N}").Trim());
        Assert.False(Directory.Exists(Path.Combine(_worktrees, taskId.ToString("N"))));
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task Hidden_index_flags_are_rejected_before_reservation(string flag)
    {
        InitializeRepository();
        var snapshot = Snapshot();
        Git("update-index", flag, "--", "src/App.cs");
        var taskId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(taskId, _root, snapshot, Plan(snapshot)));

        Assert.Equal("implementation_repository_state", exception.Category);
        Assert.False(Directory.Exists(Path.Combine(_worktrees, taskId.ToString("N"))));
    }

    [Fact]
    public async Task Sparse_checkout_configuration_and_insufficient_fingerprint_budget_fail_closed()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        Git("config", "core.sparseCheckout", "true");
        var sparse = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_repository_state", sparse.Category);

        Git("config", "core.sparseCheckout", "false");
        var limits = new ImplementationLimits { MaximumActiveCheckoutFingerprintBytes = 1 };
        var bounded = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot), limits));
        Assert.Equal("implementation_fingerprint_limit", bounded.Category);
    }

    [Fact]
    public async Task Sparse_index_and_nonzero_index_stages_are_rejected_before_reservation()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        Git("config", "index.sparse", "true");
        var sparseIndex = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_repository_state", sparseIndex.Category);

        Git("config", "index.sparse", "false");
        var baseBlob = Git("rev-parse", "HEAD:src/App.cs").Trim();
        File.WriteAllText(Path.Combine(_root, "other.txt"), "other\n");
        var otherBlob = Git("hash-object", "-w", "other.txt").Trim();
        File.Delete(Path.Combine(_root, "other.txt"));
        GitWithInput("update-index", "--index-info",
            $"0 {new string('0', 40)}\tsrc/App.cs\n100644 {baseBlob} 1\tsrc/App.cs\n100644 {otherBlob} 2\tsrc/App.cs\n100644 {baseBlob} 3\tsrc/App.cs\n");

        var staged = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager().ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot)));
        Assert.Equal("implementation_repository_state", staged.Category);
    }

    [Fact]
    public async Task Active_checkout_byte_mutation_hidden_after_reservation_is_detected_before_preparation()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        Git("update-index", "--assume-unchanged", "--", "src/App.cs");
        File.AppendAllText(Path.Combine(_root, "src", "App.cs"), "hidden change\n");

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.PrepareAsync(
            _root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout));

        Assert.Equal("implementation_repository_state", exception.Category);
    }

    [Fact]
    public async Task Sensitive_generated_content_is_rejected_by_shared_output_validation_before_first_write()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        output = output with
        {
            Operations = output.Operations.Select(operation => operation.Action == ImplementationOperationAction.Create
                ? operation with { Content = "api_key = generated-secret\n" }
                : operation).ToArray()
        };
        var original = await File.ReadAllTextAsync(Path.Combine(_worktrees, prepared.Workspace.Token, "src", "App.cs"));

        var exception = Assert.Throws<ImplementationException>(() =>
            ImplementationOutputValidator.Validate(plan, prepared.Files, output, new ImplementationLimits()));

        Assert.Equal("implementation_sensitive_content", exception.Category);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_worktrees, prepared.Workspace.Token, "src", "App.cs")));
        Assert.Equal(string.Empty, GitAt(Path.Combine(_worktrees, prepared.Workspace.Token), "status", "--porcelain=v1", "--untracked-files=all"));
    }

    [Theory]
    [InlineData("status", "implementation_repository_state")]
    [InlineData("worktree", "implementation_workspace_conflict")]
    public async Task Truncated_correctness_output_is_never_treated_as_success(string command, string category)
    {
        InitializeRepository();
        var runner = new TruncatingGitRunner(CreateRunner()) { Command = command };

        var exception = await Assert.ThrowsAsync<ImplementationException>(() =>
            Manager(runner).ReserveAsync(Guid.NewGuid(), _root, Snapshot(), Plan(Snapshot())));

        Assert.Equal(category, exception.Category);
    }

    [Fact]
    public async Task Truncated_diff_output_cannot_produce_a_completed_review()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var runner = new TruncatingGitRunner(CreateRunner());
        var manager = Manager(runner);
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        runner.Command = "diff";

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.ApplyAsync(
            _root, prepared, output, new ImplementationLimits(), DateTimeOffset.UtcNow));

        Assert.Equal("implementation_diff_failure", exception.Category);
        Assert.True(exception.RecoveryRequired);
    }

    [Fact]
    public async Task Exclusive_workspace_lock_blocks_a_second_preparation()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.PrepareAsync(
            _root, prepared.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout));

        Assert.Equal("implementation_workspace_lock", exception.Category);
    }

    [Fact]
    public async Task Partial_apply_after_first_success_is_recovery_required_and_active_checkout_is_unchanged()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var faultingFiles = new FaultingFileSystem { FailWriteNumber = 2 };
        var manager = Manager(files: faultingFiles);
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        var sourceBytes = File.ReadAllBytes(Path.Combine(_root, "src", "App.cs"));

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.ApplyAsync(
            _root, prepared, output, new ImplementationLimits(), DateTimeOffset.UtcNow));

        Assert.True(exception.RecoveryRequired);
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(_root, "src", "App.cs")));
        Assert.Contains("src/App.cs", GitAt(Path.Combine(_worktrees, prepared.Workspace.Token),
            "status", "--porcelain=v1", "--untracked-files=all"));
    }

    [Fact]
    public async Task First_write_disk_style_failure_never_produces_a_completed_review()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager(files: new FaultingFileSystem { FailWriteNumber = 1 });
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan,
            new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.ApplyAsync(
            _root, prepared, output, new ImplementationLimits(), DateTimeOffset.UtcNow));

        Assert.Equal("implementation_recovery_required", exception.Category);
        Assert.True(exception.RecoveryRequired);
        Assert.Equal("", GitAt(Path.Combine(_worktrees, prepared.Workspace.Token),
            "status", "--porcelain=v1", "--untracked-files=all"));
    }

    [Fact]
    public async Task Delete_then_write_failure_remains_truthful_partial_apply()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager(files: new FaultingFileSystem { FailWriteNumber = 1 });
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var generated = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        var operations = generated.Operations.OrderBy(operation => operation.Action == ImplementationOperationAction.Delete ? 0 : 1).ToArray();

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.ApplyAsync(
            _root, prepared, generated with { Operations = operations }, new ImplementationLimits(), DateTimeOffset.UtcNow));

        Assert.True(exception.RecoveryRequired);
        Assert.False(File.Exists(Path.Combine(_worktrees, prepared.Workspace.Token, "docs", "Delete.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "docs", "Delete.txt")));
    }

    [Fact]
    public async Task Cancellation_between_file_operations_is_recovery_required_after_the_first_write()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        using var cancellation = new CancellationTokenSource();
        var manager = Manager(files: new FaultingFileSystem
        {
            CancelAfterWriteNumber = 1,
            Cancellation = cancellation
        });
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan,
            new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        var sourceBytes = File.ReadAllBytes(Path.Combine(_root, "src", "App.cs"));

        var exception = await Assert.ThrowsAsync<ImplementationException>(() => manager.ApplyAsync(
            _root, prepared, output, new ImplementationLimits(), DateTimeOffset.UtcNow, cancellation.Token));

        Assert.Equal("implementation_recovery_required", exception.Category);
        Assert.True(exception.RecoveryRequired);
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(_root, "src", "App.cs")));
        Assert.Contains("src/App.cs", GitAt(Path.Combine(_worktrees, prepared.Workspace.Token),
            "status", "--porcelain=v1", "--untracked-files=all"));
    }

    [Fact]
    public async Task File_lock_and_post_write_interference_prevent_completed_review()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await using var workspaceLock = prepared.WorkspaceLock;
        var output = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, prepared.Files, DateTimeOffset.UtcNow))).Output;
        await using (var locked = new FileStream(Path.Combine(_worktrees, prepared.Workspace.Token, "src", "App.cs"),
                         FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockedFailure = await Assert.ThrowsAsync<ImplementationException>(() => manager.ApplyAsync(
                _root, prepared, output, new ImplementationLimits(), DateTimeOffset.UtcNow));
            Assert.True(lockedFailure.RecoveryRequired);
        }

        var secondTask = Guid.NewGuid();
        var interfering = new FaultingFileSystem { InterfereAfterWriteNumber = 1 };
        var secondManager = Manager(files: interfering);
        var secondReservation = await secondManager.ReserveAsync(secondTask, _root, snapshot, plan);
        var secondPrepared = await secondManager.PrepareAsync(_root, secondReservation.Workspace, plan,
            new ImplementationLimits(), secondReservation.ActiveCheckout);
        await using var secondLock = secondPrepared.WorkspaceLock;
        var secondOutput = (await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, secondPrepared.Files, DateTimeOffset.UtcNow))).Output;

        var interference = await Assert.ThrowsAsync<ImplementationException>(() => secondManager.ApplyAsync(
            _root, secondPrepared, secondOutput, new ImplementationLimits(), DateTimeOffset.UtcNow));
        Assert.True(interference.RecoveryRequired);
    }

    [Fact]
    public async Task Registered_missing_source_missing_and_unregistered_collisions_fail_closed()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout);
        await prepared.WorkspaceLock.DisposeAsync();
        var workspacePath = Path.Combine(_worktrees, prepared.Workspace.Token);
        foreach (var path in Directory.EnumerateFileSystemEntries(workspacePath, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
        Directory.Delete(workspacePath, true);

        Assert.False(await manager.IsAvailableAsync(_root, prepared.Workspace, plan, null));
        var missing = await Assert.ThrowsAsync<ImplementationException>(() => manager.PrepareAsync(
            _root, prepared.Workspace, plan, new ImplementationLimits(), reservation.ActiveCheckout));
        Assert.True(missing.RecoveryRequired);

        var collisionTask = Guid.NewGuid();
        var collision = await manager.ReserveAsync(collisionTask, _root, snapshot, plan);
        Directory.CreateDirectory(Path.Combine(_worktrees, collision.Workspace.Token));
        var unregistered = await Assert.ThrowsAsync<ImplementationException>(() => manager.PrepareAsync(
            _root, collision.Workspace, plan, new ImplementationLimits(), collision.ActiveCheckout));
        Assert.Equal("implementation_workspace_conflict", unregistered.Category);

        var missingSource = _root + "-missing";
        Assert.False(await manager.IsAvailableAsync(missingSource, collision.Workspace, plan, null));
    }

    [Fact]
    public async Task Owned_branch_marker_allows_safe_crash_resume_but_repository_identity_replacement_is_rejected()
    {
        InitializeRepository();
        var snapshot = Snapshot();
        var plan = Plan(snapshot);
        var taskId = Guid.NewGuid();
        var token = taskId.ToString("N");
        Git("update-ref", $"refs/forge/tasks/{token}", snapshot.FullHeadSha!);
        Git("branch", $"forge/task-{token}", snapshot.FullHeadSha!);
        var manager = Manager();
        var reservation = await manager.ReserveAsync(taskId, _root, snapshot, plan);
        var prepared = await manager.PrepareAsync(_root, reservation.Workspace, plan,
            new ImplementationLimits(), reservation.ActiveCheckout);
        await prepared.WorkspaceLock.DisposeAsync();

        Assert.Equal(token, prepared.Workspace.Token);
        Assert.True(await manager.IsAvailableAsync(_root, prepared.Workspace, plan, null));
        var replacementRoot = Path.Combine(Path.GetTempPath(), $"forge-replacement-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(replacementRoot);
            GitAt(replacementRoot, "init");
            Assert.False(await manager.IsAvailableAsync(replacementRoot, prepared.Workspace, plan, null));
        }
        finally
        {
            if (Directory.Exists(replacementRoot)) Directory.Delete(replacementRoot, true);
        }
    }

    private GitWorktreeManager Manager(IGitProcessRunner? runner = null, IImplementationFileSystem? files = null) => new(
        runner ?? CreateRunner(),
        new RepositoryFileSafetyPolicy(),
        new ImplementationWorkspaceOptions { WorktreeRoot = _worktrees },
        files);

    private GitProcessRunner CreateRunner() => new(new GitProcessOptions
    {
        OwnedRoot = _worktrees,
        HooksDirectory = Path.Combine(_worktrees, ".empty-hooks"),
        SafeHomeDirectory = Path.Combine(_worktrees, ".git-home")
    });

    private async Task<PreparedImplementationWorkspace> PrepareDefaultAsync(RepositorySnapshot snapshot)
    {
        var manager = Manager();
        var reservation = await manager.ReserveAsync(Guid.NewGuid(), _root, snapshot, Plan(snapshot));
        return await manager.PrepareAsync(_root, reservation.Workspace, Plan(snapshot), new ImplementationLimits(), reservation.ActiveCheckout);
    }

    private void InitializeRepository()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "src", "App.cs"), "public class App { }\n");
        File.WriteAllText(Path.Combine(_root, "docs", "Delete.txt"), "delete me\n");
        File.WriteAllText(Path.Combine(_root, "Repository.props"), "root-level tracked file\n");
        Git("init");
        Git("config", "user.email", "forge-tests@example.invalid");
        Git("config", "user.name", "Forge Tests");
        Git("add", ".");
        Git("commit", "-m", "initial");
    }

    private RepositorySnapshot Snapshot()
    {
        var head = Git("rev-parse", "HEAD").Trim();
        return new RepositorySnapshot(_root, true, Git("branch", "--show-current").Trim(), head[..8], head, "clean",
            2, 2, 0, ["C#/.NET"], [".cs", ".txt"], [], [], [], DateTimeOffset.UtcNow, "fingerprint",
            [
                new RepositoryFileMetadata("src/App.cs", ".cs", 21, 1, "source", false, "App", ["App"]),
                new RepositoryFileMetadata("docs/Delete.txt", ".txt", 10, 1, "source", false, "docs", [])
            ], "status");
    }

    private static RepositorySnapshot EmptySnapshot() => new("C:/none", false, null, null, null, "unknown", 0, 0, 0, [], [], [], [], [], DateTimeOffset.UtcNow, "empty", []);

    private static ImplementationPlan Plan(RepositorySnapshot snapshot) => new(
        "Implement", "Objective", "Understanding",
        [
            new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Modify.", ["E1"], .9m),
            new PlannedFileChange("docs/New.md", PlannedFileAction.Create, "Create.", [], .8m),
            new PlannedFileChange("docs/Delete.txt", PlannedFileAction.Delete, "Delete.", ["E2"], .8m),
            new PlannedFileChange("Repository.props", PlannedFileAction.Inspect, "Inspect.", [], .4m)
        ],
        [new ImplementationStep(1, "Change files.", ["src/App.cs", "docs/New.md", "docs/Delete.txt", "Repository.props"], ["E1", "E2"], "Diff exists.")],
        [], [], [], [], [new RequirementCoverageItem("Change files.", ["src/App.cs", "docs/New.md", "docs/Delete.txt", "Repository.props"], [1])],
        "Summary", PlanningSource.DeterministicFake, null, DateTimeOffset.UtcNow, snapshot.Fingerprint);

    private static ImplementationPlan CreateOnlyPlan(RepositorySnapshot snapshot, string path) => new(
        "Create", "Objective", "Understanding",
        [new PlannedFileChange(path, PlannedFileAction.Create, "Create.", [], .8m)],
        [new ImplementationStep(1, "Create file.", [path], [], "File exists.")], [], [], [], [],
        [new RequirementCoverageItem("Create file.", [path], [1])], "Summary",
        PlanningSource.DeterministicFake, null, DateTimeOffset.UtcNow, snapshot.Fingerprint);

    private static ImplementationPlan ModifyOnlyPlan(RepositorySnapshot snapshot, string path) => new(
        "Modify", "Objective", "Understanding",
        [new PlannedFileChange(path, PlannedFileAction.Modify, "Modify.", ["E1"], .8m)],
        [new ImplementationStep(1, "Modify file.", [path], ["E1"], "File changes.")], [], [], [], [],
        [new RequirementCoverageItem("Modify file.", [path], [1])], "Summary",
        PlanningSource.DeterministicFake, null, DateTimeOffset.UtcNow, snapshot.Fingerprint);

    private string Git(params string[] arguments)
        => GitAt(_root, arguments);

    private void GitWithInput(string first, string second, string input)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(first);
        info.ArgumentList.Add(second);
        using var process = Process.Start(info)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static string GitAt(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private string GitPath(string name)
    {
        var value = Git("rev-parse", "--git-path", name).Trim();
        return Path.IsPathFullyQualified(value) ? value : Path.GetFullPath(value, _root);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class TruncatingGitRunner(IGitProcessRunner inner) : IGitProcessRunner
    {
        public string? Command { get; set; }

        public async Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            string? standardInput = null,
            int? maximumOutputCharacters = null,
            CancellationToken cancellationToken = default,
            GitCommandKind commandKind = GitCommandKind.ReadOnly)
        {
            var result = await inner.RunAsync(workingDirectory, arguments, standardInput,
                maximumOutputCharacters, cancellationToken, commandKind);
            return arguments.Count > 0 && string.Equals(arguments[0], Command, StringComparison.Ordinal)
                ? result with { OutputTruncated = true }
                : result;
        }
    }

    private sealed class FaultingFileSystem : IImplementationFileSystem
    {
        private readonly PhysicalImplementationFileSystem inner = new();
        private int writes;
        public int? FailWriteNumber { get; init; }
        public int? InterfereAfterWriteNumber { get; init; }
        public int? CancelAfterWriteNumber { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
            inner.ReadAllBytesAsync(path, cancellationToken);
        public async Task WriteReplacementAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
        {
            writes++;
            if (writes == FailWriteNumber) throw new IOException("Injected write failure.");
            await inner.WriteReplacementAsync(path, content, overwrite, cancellationToken);
            if (writes == InterfereAfterWriteNumber)
                await File.WriteAllTextAsync(path, "external interference\n", cancellationToken);
            if (writes == CancelAfterWriteNumber)
                Cancellation?.Cancel();
        }
        public void DeleteFile(string path) => inner.DeleteFile(path);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _worktrees })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
