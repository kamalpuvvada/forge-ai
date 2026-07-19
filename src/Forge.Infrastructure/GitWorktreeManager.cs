using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class ImplementationWorkspaceOptions
{
    public string WorktreeRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForgeAI", "worktrees");
}

public interface IImplementationFileSystem : ISafeDirectoryFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);
    Task WriteReplacementAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken);
    void DeleteFile(string path);
    SafeDirectoryEntry ISafeDirectoryFileSystem.Inspect(string path) =>
        new PhysicalSafeDirectoryFileSystem().Inspect(path);
    bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    bool TryReadSmallTextFile(string path, int maximumCharacters, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > maximumCharacters * 4L) return false;
            value = File.ReadAllText(path);
            return value.Length <= maximumCharacters;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed class PhysicalImplementationFileSystem : IImplementationFileSystem
{
    private readonly PhysicalSafeDirectoryFileSystem directoryFileSystem = new();
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    public async Task WriteReplacementAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException("The target has no parent directory.");
        var temporary = Path.Combine(directory, $".forge-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void DeleteFile(string path) => File.Delete(path);
    public SafeDirectoryEntry Inspect(string path) => directoryFileSystem.Inspect(path);
    public bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    public bool TryReadSmallTextFile(string path, int maximumCharacters, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > maximumCharacters * 4L) return false;
            value = File.ReadAllText(path);
            return value.Length <= maximumCharacters;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed class GitWorktreeManager : IImplementationWorkspaceManager
{
    private readonly IGitProcessRunner git;
    private readonly RepositoryFileSafetyPolicy fileSafety;
    private readonly ImplementationWorkspaceOptions options;
    private readonly IImplementationFileSystem files;
    private readonly ISafeDirectoryAncestry ancestry;

    public GitWorktreeManager(
        IGitProcessRunner git,
        RepositoryFileSafetyPolicy fileSafety,
        ImplementationWorkspaceOptions options,
        IImplementationFileSystem? files = null,
        ISafeDirectoryAncestry? ancestry = null)
    {
        this.git = git;
        this.fileSafety = fileSafety;
        this.options = options;
        this.files = files ?? new PhysicalImplementationFileSystem();
        this.ancestry = ancestry ?? new SafeDirectoryAncestry(this.files);
    }

    public async Task<ImplementationReservation> ReserveAsync(
        Guid taskId,
        string repositoryPath,
        RepositorySnapshot snapshot,
        ImplementationPlan plan,
        CancellationToken cancellationToken = default) =>
        await ReserveAsync(taskId, repositoryPath, snapshot, plan, new ImplementationLimits(), cancellationToken);

    public async Task<ImplementationReservation> ReserveAsync(
        Guid taskId,
        string repositoryPath,
        RepositorySnapshot snapshot,
        ImplementationPlan plan,
        ImplementationLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(limits);
        FakeImplementationCapabilityMatrix.ValidatePlan(plan);
        if (!snapshot.IsGitRepository || string.IsNullOrWhiteSpace(snapshot.FullHeadSha))
            throw new ImplementationException("implementation_repository_not_git", "Implementation generation requires an eligible Git repository.");
        if (!string.Equals(snapshot.WorkingTreeStatus, "clean", StringComparison.Ordinal))
            throw new ImplementationException("implementation_repository_dirty", "The approved repository snapshot was not clean. Re-analyze and approve a clean repository state.");

        var root = NormalizeDirectory(repositoryPath);
        EnsureDirectoryAndAncestorsAreSafe(root, "The selected repository root is unsafe.");
        var worktreeRoot = NormalizeDirectory(options.WorktreeRoot);
        EnsureOutsideRepository(root, worktreeRoot);
        Directory.CreateDirectory(worktreeRoot);
        EnsureDirectoryAndAncestorsAreSafe(worktreeRoot, "The configured implementation worktree root is unsafe.");

        var top = (await RequireOutputAsync(root, ["rev-parse", "--show-toplevel"],
            "implementation_repository_not_git", cancellationToken)).Trim();
        if (!PathsEqual(root, top))
            throw new ImplementationException("implementation_repository_not_git", "The selected path must be the exact Git repository root.");
        var inside = (await RequireOutputAsync(root, ["rev-parse", "--is-inside-work-tree"],
            "implementation_repository_not_git", cancellationToken)).Trim();
        if (!string.Equals(inside, "true", StringComparison.OrdinalIgnoreCase))
            throw new ImplementationException("implementation_repository_not_git", "Implementation generation requires a non-bare Git worktree.");

        await EnsureNoActiveFiltersAsync(root, plan, cancellationToken);
        await EnsureNoGitOperationInProgressAsync(root, cancellationToken);
        var approvedPaths = plan.AffectedFiles.Select(file => file.Path).ToArray();
        var signature = await CaptureActiveCheckoutAsync(root, approvedPaths, limits, cancellationToken);
        if (!string.IsNullOrEmpty(await StatusAsync(root, approvedPaths, cancellationToken)))
            throw new ImplementationException("implementation_repository_dirty", "The selected repository must be completely clean, including untracked files.");
        if (!string.Equals(signature.HeadSha, snapshot.FullHeadSha, StringComparison.Ordinal))
            throw new ImplementationException("implementation_base_changed", "The repository HEAD changed after plan approval. Re-analyze and approve a new plan.");
        var preflightFiles = await BuildPreflightContextsAsync(root, plan, limits, cancellationToken);

        var token = taskId.ToString("N");
        var branch = $"forge/task-{token}";
        var ownerReference = $"refs/forge/tasks/{token}";
        var commonDirectory = await ResolveGitCommonDirectoryAsync(root, cancellationToken);
        var repositoryIdentity = HashCanonicalPath(root);
        var commonIdentity = HashCanonicalPath(commonDirectory);
        var workspacePath = ResolveWorkspacePath(worktreeRoot, token);
        await VerifyOwnershipStateAsync(root, workspacePath, branch, ownerReference, signature.HeadSha,
            allowUnreserved: true, cancellationToken);

        var branchName = await git.RunAsync(root, ["check-ref-format", "--branch", branch], cancellationToken: cancellationToken);
        EnsureCompleteSuccess(branchName, "implementation_workspace_conflict",
            "Forge could not create a safe deterministic implementation branch name.");
        var now = DateTimeOffset.UtcNow;
        return new ImplementationReservation(
            new ImplementationWorkspace(token, branch, signature.HeadSha,
                ImplementationWorkspacePhase.Reserved, now, now, false,
                repositoryIdentity, commonIdentity, ownerReference,
                signature.TrackedContentFingerprint, signature.TrackedFileCount, signature.TrackedBytes),
            signature,
            preflightFiles);
    }

    public Task<PreparedImplementationWorkspace> PrepareAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationLimits limits,
        ActiveCheckoutSignature activeCheckout,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(repositoryPath, workspace, plan, limits, activeCheckout, null, cancellationToken);

    public Task<PreparedImplementationWorkspace> PrepareAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationLimits limits,
        ActiveCheckoutSignature activeCheckout,
        IReadOnlyList<ImplementationFileContext> preflightFiles,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(repositoryPath, workspace, plan, limits, activeCheckout, preflightFiles, cancellationToken);

    private async Task<PreparedImplementationWorkspace> PrepareCoreAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationLimits limits,
        ActiveCheckoutSignature activeCheckout,
        IReadOnlyList<ImplementationFileContext>? preflightFiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(limits);
        FakeImplementationCapabilityMatrix.ValidatePlan(plan);
        var root = NormalizeDirectory(repositoryPath);
        var worktreeRoot = NormalizeDirectory(options.WorktreeRoot);
        EnsureOutsideRepository(root, worktreeRoot);
        EnsureDirectoryAndAncestorsAreSafe(worktreeRoot, "The configured implementation worktree root is unsafe.");
        var workspacePath = ResolveWorkspacePath(worktreeRoot, workspace.Token);
        var workspaceLock = AcquireWorkspaceLock(worktreeRoot, workspace.Token);
        try
        {
            await VerifyRepositoryIdentityAsync(root, workspace, cancellationToken);
            await EnsureNoActiveFiltersAsync(root, plan, cancellationToken);
            await EnsureActiveCheckoutUnchangedAsync(root, plan, limits, activeCheckout, cancellationToken);
            if (preflightFiles is not null)
            {
                var currentPreflight = await BuildPreflightContextsAsync(root, plan, limits, cancellationToken);
                EnsureContextsMatch(preflightFiles, currentPreflight);
            }
            await VerifyOwnershipStateAsync(root, workspacePath, workspace.Branch, workspace.OwnershipReference,
                workspace.BaseCommitSha, allowUnreserved: true, cancellationToken);

            var owner = await ReadOptionalRefAsync(root, workspace.OwnershipReference, cancellationToken);
            if (owner is null)
                await RequireSuccessAsync(root,
                    ["update-ref", "--stdin"],
                    "implementation_workspace_conflict", "The Forge workspace ownership marker could not be reserved safely.",
                    CancellationToken.None, $"create {workspace.OwnershipReference} {workspace.BaseCommitSha}\n",
                    GitCommandKind.Mutating);

            var branchHead = await ReadOptionalRefAsync(root, $"refs/heads/{workspace.Branch}", cancellationToken);
            if (!Directory.Exists(workspacePath))
            {
                if (branchHead is null)
                {
                    await RequireSuccessAsync(root,
                        ["worktree", "add", "--no-checkout", "-b", workspace.Branch, workspacePath, workspace.BaseCommitSha],
                        "implementation_workspace_conflict", "The isolated implementation worktree could not be created safely.",
                        cancellationToken, commandKind: GitCommandKind.Mutating);
                }
                else
                {
                    await RequireSuccessAsync(root,
                        ["worktree", "add", "--no-checkout", workspacePath, workspace.Branch],
                        "implementation_workspace_conflict", "The owned implementation branch could not be linked safely.",
                        cancellationToken, commandKind: GitCommandKind.Mutating);
                }
            }

            await VerifyWorkspaceIdentityAsync(root, workspacePath, workspace, cancellationToken);
            var approvedPaths = plan.AffectedFiles.Select(file => file.Path).ToArray();
            var status = await StatusAsync(workspacePath, approvedPaths, cancellationToken);
            var reservedNoCheckout = workspace.Phase is ImplementationWorkspacePhase.Reserved or
                    ImplementationWorkspacePhase.WorkspacePreparing &&
                Directory.EnumerateFileSystemEntries(workspacePath)
                    .All(path => Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(status) && !reservedNoCheckout)
                throw Recovery("The existing isolated implementation workspace contains unknown or partial changes and requires explicit recovery.");

            foreach (var planned in plan.AffectedFiles)
            {
                var path = RepositoryPathRules.Normalize(planned.Path);
                fileSafety.ValidateEligiblePath(workspacePath, path, mayNotExist: true);
                await EnsureGitAncestorsAreRegularAsync(root, path, cancellationToken);
                if (planned.Action != PlannedFileAction.Create)
                    await EnsureRegularTrackedFileAsync(root, path, cancellationToken);
            }

            var materializedPaths = plan.AffectedFiles.Select(file => RepositoryPathRules.Normalize(file.Path))
                .Distinct(RepositoryPathRules.Comparer).ToArray();
            await RequireSuccessAsync(workspacePath, ["read-tree", "HEAD"],
                "implementation_workspace_conflict", "The isolated worktree index could not be initialized safely.",
                cancellationToken, commandKind: GitCommandKind.Mutating);
            await RequireSuccessAsync(workspacePath, ["sparse-checkout", "init", "--no-cone"],
                "implementation_workspace_conflict", "The isolated sparse worktree could not be initialized.",
                cancellationToken, commandKind: GitCommandKind.Mutating);
            var sparsePatterns = string.Join('\n', materializedPaths.Select(path => $"/{path}")) + "\n";
            await RequireSuccessAsync(workspacePath, ["sparse-checkout", "set", "--no-cone", "--stdin"],
                "implementation_workspace_conflict", "Approved paths could not be materialized in the isolated worktree.",
                cancellationToken, sparsePatterns, GitCommandKind.Mutating);
            var existingPaths = plan.AffectedFiles.Where(file => file.Action != PlannedFileAction.Create)
                .Select(file => RepositoryPathRules.Normalize(file.Path)).Distinct(RepositoryPathRules.Comparer).ToArray();
            if (existingPaths.Length > 0)
                await RequireSuccessAsync(workspacePath, ["checkout-index", "--force", "--", .. existingPaths],
                    "implementation_workspace_conflict", "Approved paths could not be materialized in the isolated worktree.",
                    cancellationToken, commandKind: GitCommandKind.Mutating);

            await VerifyWorkspaceIdentityAsync(root, workspacePath, workspace, cancellationToken);
            if (!string.IsNullOrEmpty(await StatusAsync(workspacePath, approvedPaths, cancellationToken)))
                throw Recovery("The prepared isolated worktree is not clean and requires explicit recovery.");

            var contexts = new List<ImplementationFileContext>(plan.AffectedFiles.Count);
            var totalCharacters = 0;
            foreach (var planned in plan.AffectedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RepositoryPathRules.Normalize(planned.Path);
                fileSafety.ValidateEligiblePath(workspacePath, path, mayNotExist: planned.Action == PlannedFileAction.Create);
                var fullPath = fileSafety.ResolveContainedPath(workspacePath, path);
                if (planned.Action == PlannedFileAction.Create)
                {
                    if (files.FileExists(fullPath) || files.DirectoryExists(fullPath))
                        throw Recovery($"Approved create path '{path}' already exists.");
                    contexts.Add(new ImplementationFileContext(path, planned.Action, null, null));
                    continue;
                }

                var content = await ReadStrictUtf8NoBomAsync(fullPath, path, cancellationToken);
                if (content.IndexOf('\0') >= 0 || content.Length > limits.MaximumCurrentFileCharacters)
                    throw new ImplementationException("implementation_input_limit", $"Approved path '{path}' exceeds the supported writable-text limit.");
                if (fileSafety.ContainsSensitiveValues(content))
                    throw new ImplementationException("implementation_sensitive_content", $"Approved path '{path}' contains sensitive values and cannot be generated safely.");
                totalCharacters = checked(totalCharacters + content.Length);
                if (totalCharacters > limits.MaximumTotalCurrentCharacters)
                    throw new ImplementationException("implementation_input_limit", "Approved writable content exceeds the total implementation-context limit.");
                contexts.Add(new ImplementationFileContext(path, planned.Action, content, ImplementationOutputValidator.Hash(content)));
            }

            if (preflightFiles is not null) EnsureContextsMatch(preflightFiles, contexts);

            await EnsureActiveCheckoutUnchangedAsync(root, plan, limits, activeCheckout, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new PreparedImplementationWorkspace(
                workspace with { Phase = ImplementationWorkspacePhase.Ready, UpdatedAt = now, IsAvailable = true },
                activeCheckout,
                contexts,
                workspaceLock);
        }
        catch
        {
            await workspaceLock.DisposeAsync();
            throw;
        }
    }

    public async Task<ImplementationResult> ApplyAsync(
        string repositoryPath,
        PreparedImplementationWorkspace prepared,
        ImplementationOutput output,
        ImplementationLimits limits,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.WorkspaceLock.IsHeld)
            throw new ImplementationException("implementation_workspace_lock", "The isolated workspace is not exclusively locked.", true);
        var root = NormalizeDirectory(repositoryPath);
        var worktreeRoot = NormalizeDirectory(options.WorktreeRoot);
        var workspacePath = ResolveWorkspacePath(worktreeRoot, prepared.Workspace.Token);
        await VerifyRepositoryIdentityAsync(root, prepared.Workspace, cancellationToken);
        await EnsureActiveCheckoutUnchangedAsync(root, prepared.Files.Select(file => file.Path), limits, prepared.ActiveCheckout, cancellationToken);
        await VerifyWorkspaceIdentityAsync(root, workspacePath, prepared.Workspace, cancellationToken);
        var approvedPaths = prepared.Files.Select(file => file.Path).ToArray();
        if (!string.IsNullOrEmpty(await StatusAsync(workspacePath, approvedPaths, cancellationToken)))
            throw Recovery("The isolated worktree changed before validated operations could be applied.");

        if (prepared.Files.Count == 0)
            throw new ImplementationException("invalid_implementation", "Prepared implementation context is empty.");
        var contexts = prepared.Files.ToDictionary(file => file.Path, RepositoryPathRules.Comparer);
        foreach (var operation in output.Operations)
        {
            var path = RepositoryPathRules.Normalize(operation.Path);
            if (!contexts.TryGetValue(path, out var context))
                throw new ImplementationException("invalid_implementation", "The implementation output contains an undeclared path.");
            fileSafety.ValidateEligiblePath(workspacePath, path, mayNotExist: operation.Action == ImplementationOperationAction.Create);
            if (operation.Content is not null && fileSafety.ContainsSensitiveValues(operation.Content))
                throw new ImplementationException("implementation_sensitive_content", $"Generated content for '{path}' contains sensitive values and cannot be applied safely.");
            var fullPath = fileSafety.ResolveContainedPath(workspacePath, path);
            if (operation.Action == ImplementationOperationAction.Create && (files.FileExists(fullPath) || files.DirectoryExists(fullPath)))
                throw Recovery($"Create path '{path}' already exists.");
            if (operation.Action != ImplementationOperationAction.Create)
            {
                if (!files.FileExists(fullPath)) throw Recovery($"Existing path '{path}' is missing.");
                var current = await ReadStrictUtf8NoBomAsync(fullPath, path, cancellationToken);
                if (!string.Equals(ImplementationOutputValidator.Hash(current), context.OriginalContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw Recovery($"Existing path '{path}' changed before application.");
            }
        }

        var mutationApplied = false;
        try
        {
            foreach (var operation in output.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RepositoryPathRules.Normalize(operation.Path);
                var fullPath = fileSafety.ResolveContainedPath(workspacePath, path);
                if (operation.Action == ImplementationOperationAction.Delete)
                {
                    files.DeleteFile(fullPath);
                    mutationApplied = true;
                    continue;
                }
                var directory = Path.GetDirectoryName(fullPath)!;
                var directoryExisted = files.DirectoryExists(directory);
                files.CreateDirectory(directory);
                if (!directoryExisted) mutationApplied = true;
                _ = fileSafety.ResolveContainedPath(workspacePath, path);
                await files.WriteReplacementAsync(fullPath, Encoding.UTF8.GetBytes(operation.Content!),
                    operation.Action == ImplementationOperationAction.Modify, cancellationToken);
                mutationApplied = true;
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new ImplementationException("implementation_recovery_required",
                "Implementation application was interrupted after isolated-worktree mutation may have begun.", true, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ImplementationException("implementation_recovery_required",
                mutationApplied
                    ? "Forge could not complete all validated writes in the isolated worktree. Explicit recovery is required."
                    : "Forge could not prove that the first isolated-worktree write left no partial state. Explicit recovery is required.",
                true, exception);
        }

        var actual = await ReadAndVerifyActualOutputsAsync(workspacePath, contexts, output.Operations, cancellationToken);
        await VerifyExpectedStatusAsync(workspacePath, output.Operations, cancellationToken);
        var createPaths = output.Operations.Where(value => value.Action == ImplementationOperationAction.Create)
            .Select(value => RepositoryPathRules.Normalize(value.Path)).ToArray();
        if (createPaths.Length > 0)
            await RequireSuccessAsync(workspacePath, ["add", "-N", "--", .. createPaths],
                "implementation_diff_failure", "New files could not be included in the local diff safely.",
                cancellationToken, commandKind: GitCommandKind.Mutating);

        var reviews = new List<ChangedFileReview>(output.Operations.Count);
        var fullCharactersTotal = 0;
        var displayedCharactersTotal = 0;
        var fullBytesTotal = 0;
        var displayedBytesTotal = 0;
        try
        {
            foreach (var operation in output.Operations)
            {
                var path = RepositoryPathRules.Normalize(operation.Path);
                var diff = await git.RunAsync(workspacePath,
                    ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--unified=3", "--", path],
                    maximumOutputCharacters: 2 * (limits.MaximumCurrentFileCharacters + limits.MaximumGeneratedFileCharacters) + 20_000,
                    cancellationToken: cancellationToken);
                EnsureCompleteSuccess(diff, "implementation_diff_failure", "The complete local diff could not be captured safely.", true);
                if (SensitiveContentDetector.ContainsSensitiveValue(diff.Output))
                    throw new ImplementationException("implementation_sensitive_content",
                        "The generated diff contains a sensitive value and cannot be persisted.", true);
                var numstat = await RequireOutputAsync(workspacePath,
                    ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--numstat", "--", path],
                    "implementation_diff_failure", cancellationToken);
                var (additions, deletions) = ParseNumStat(numstat, path);
                var fullCharacters = diff.Output.Length;
                var fullBytes = Encoding.UTF8.GetByteCount(diff.Output);
                fullCharactersTotal = checked(fullCharactersTotal + fullCharacters);
                fullBytesTotal = checked(fullBytesTotal + fullBytes);
                var remaining = Math.Max(0, limits.MaximumDiffPreviewCharactersTotal - displayedCharactersTotal);
                var preview = TruncateDiffPreviewRuneSafe(diff.Output, Math.Min(limits.MaximumDiffPreviewCharactersPerFile, remaining));
                var displayedCharacters = preview.Length;
                var displayedBytes = Encoding.UTF8.GetByteCount(preview);
                displayedCharactersTotal += displayedCharacters;
                displayedBytesTotal = checked(displayedBytesTotal + displayedBytes);
                var context = contexts[path];
                var finalContent = actual[path];
                reviews.Add(new ChangedFileReview(
                    path,
                    operation.Action,
                    context.OriginalContentSha256,
                    finalContent is null ? null : ImplementationOutputValidator.Hash(finalContent),
                    Utf8Bytes(context.OriginalContent),
                    Utf8Bytes(finalContent),
                    CountLines(context.OriginalContent),
                    CountLines(finalContent),
                    additions,
                    deletions,
                    preview,
                    fullCharacters,
                    displayedCharacters,
                    displayedCharacters < fullCharacters,
                    fullBytes,
                    displayedBytes));
            }
        }
        finally
        {
            if (createPaths.Length > 0)
                await RequireSuccessAsync(workspacePath, ["reset", "--", .. createPaths],
                    "implementation_recovery_required", "The isolated worktree index could not be restored after diff generation.",
                    CancellationToken.None, commandKind: GitCommandKind.Mutating);
        }

        await VerifyExpectedStatusAsync(workspacePath, output.Operations, cancellationToken);
        _ = await ReadAndVerifyActualOutputsAsync(workspacePath, contexts, output.Operations, cancellationToken);
        await EnsureActiveCheckoutUnchangedAsync(root, prepared.Files.Select(file => file.Path), limits, prepared.ActiveCheckout, cancellationToken);
        var preliminary = new ImplementationResult(
            output.Source,
            output.Model,
            prepared.Workspace.BaseCommitSha,
            prepared.Workspace.Branch,
            output.Summary,
            output.Warnings,
            reviews,
            fullCharactersTotal,
            displayedCharactersTotal,
            reviews.Any(file => file.DiffTruncated) || displayedCharactersTotal < fullCharactersTotal,
            completedAt,
            fullBytesTotal,
            displayedBytesTotal,
            true);
        var fingerprint = await ComputeWorktreeFingerprintAsync(workspacePath, prepared.Workspace, preliminary, cancellationToken);
        return preliminary with
        {
            WorktreeFingerprint = fingerprint.Hash,
            WorktreeFileCount = fingerprint.FileCount,
            WorktreeBytes = fingerprint.Bytes
        };
    }

    public async Task<bool> IsAvailableAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationResult? result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = NormalizeDirectory(repositoryPath);
            var worktreeRoot = NormalizeDirectory(options.WorktreeRoot);
            EnsureOutsideRepository(root, worktreeRoot);
            var workspacePath = ResolveWorkspacePath(worktreeRoot, workspace.Token);
            if (!Directory.Exists(root) || !Directory.Exists(workspacePath)) return false;
            await using var workspaceLock = AcquireWorkspaceLock(worktreeRoot, workspace.Token);
            await VerifyRepositoryIdentityAsync(root, workspace, cancellationToken);
            await EnsureNoActiveFiltersAsync(root, plan.AffectedFiles.Select(file => file.Path), cancellationToken);
            await VerifyWorkspaceIdentityAsync(root, workspacePath, workspace, cancellationToken);
            if (result is null) return true;
            if (!string.Equals(result.BaseCommitSha, workspace.BaseCommitSha, StringComparison.Ordinal) ||
                !string.Equals(result.Branch, workspace.Branch, StringComparison.Ordinal)) return false;
            var operations = result.ChangedFiles.Select(file => new ImplementationOperation(
                file.Path, file.Action, file.OriginalContentSha256, null, "Persisted review operation.")).ToArray();
            await VerifyExpectedStatusAsync(workspacePath, operations, cancellationToken);
            var fingerprint = await ComputeWorktreeFingerprintAsync(workspacePath, workspace, result, cancellationToken);
            return string.Equals(fingerprint.Hash, result.WorktreeFingerprint, StringComparison.Ordinal) &&
                   fingerprint.FileCount == result.WorktreeFileCount && fingerprint.Bytes == result.WorktreeBytes;
        }
        catch (Exception exception) when (exception is ImplementationException or IOException or
                                          UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    public Task<bool> IsObservedAvailableReadOnlyAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationResult? result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var worktreeRoot = NormalizeDirectory(options.WorktreeRoot);
            if (!ancestry.IsExistingSafe(worktreeRoot)) return Task.FromResult(false);
            var workspacePath = ResolveWorkspacePath(worktreeRoot, workspace.Token);
            if (!ancestry.IsExistingSafe(workspacePath)) return Task.FromResult(false);
            if (!ObservedWorkspaceIdentityIsValid(repositoryPath, workspacePath, workspace))
                return Task.FromResult(false);
            if (result is not null &&
                (!string.Equals(result.BaseCommitSha, workspace.BaseCommitSha, StringComparison.Ordinal) ||
                 !string.Equals(result.Branch, workspace.Branch, StringComparison.Ordinal)))
                return Task.FromResult(false);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is ImplementationException or IOException or
                                          UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(false);
        }
    }

    private bool ObservedWorkspaceIdentityIsValid(
        string repositoryPath,
        string workspacePath,
        ImplementationWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.RepositoryIdentity) ||
            string.IsNullOrWhiteSpace(workspace.GitCommonDirectoryIdentity) ||
            !string.Equals(workspace.Branch, $"forge/task-{workspace.Token}", StringComparison.Ordinal) ||
            !string.Equals(workspace.OwnershipReference, $"refs/forge/tasks/{workspace.Token}", StringComparison.Ordinal))
            return false;

        var repositoryRoot = NormalizeDirectory(repositoryPath);
        if (!ancestry.IsExistingSafe(repositoryRoot) ||
            !string.Equals(HashCanonicalPath(repositoryRoot), workspace.RepositoryIdentity, StringComparison.Ordinal))
            return false;
        if (!TryResolveObservedSourceCommonDirectory(repositoryRoot, out var sourceCommonDirectory) ||
            !string.Equals(HashCanonicalPath(sourceCommonDirectory), workspace.GitCommonDirectoryIdentity,
                StringComparison.Ordinal)) return false;

        return TryResolveObservedLinkedCheckout(workspacePath, $"refs/heads/{workspace.Branch}",
                   out var workspaceCommonDirectory) &&
               PathsEqual(workspaceCommonDirectory, sourceCommonDirectory) &&
               string.Equals(HashCanonicalPath(workspaceCommonDirectory), workspace.GitCommonDirectoryIdentity,
                   StringComparison.Ordinal);
    }

    private bool TryResolveObservedSourceCommonDirectory(string repositoryRoot, out string commonDirectory)
    {
        commonDirectory = string.Empty;
        var gitEntry = Path.Combine(repositoryRoot, ".git");
        SafeDirectoryEntry entry;
        try { entry = files.Inspect(gitEntry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        if (entry.IsReparseOrLink) return false;
        if (entry.Kind == SafeDirectoryEntryKind.Directory)
        {
            if (!ancestry.IsExistingSafe(gitEntry)) return false;
            commonDirectory = NormalizeDirectory(gitEntry);
            return true;
        }
        if (entry.Kind != SafeDirectoryEntryKind.Other) return false;
        return TryResolveObservedLinkedCheckout(repositoryRoot, null, out commonDirectory);
    }

    private bool TryResolveObservedLinkedCheckout(
        string checkoutPath,
        string? expectedHeadReference,
        out string commonDirectory)
    {
        commonDirectory = string.Empty;
        var gitLink = Path.Combine(checkoutPath, ".git");
        if (!ObservedRegularTextFile(gitLink, out var gitLinkText) ||
            !TryParseSingleLineValue(gitLinkText, "gitdir: ", out var metadataValue) ||
            !Path.IsPathFullyQualified(metadataValue)) return false;
        var metadataDirectory = NormalizeDirectory(metadataValue);
        if (!ancestry.IsExistingSafe(metadataDirectory)) return false;

        var backLink = Path.Combine(metadataDirectory, "gitdir");
        var commonLink = Path.Combine(metadataDirectory, "commondir");
        var head = Path.Combine(metadataDirectory, "HEAD");
        if (!ObservedRegularTextFile(backLink, out var backLinkText) ||
            !ObservedRegularTextFile(commonLink, out var commonLinkText) ||
            !ObservedRegularTextFile(head, out var headText) ||
            !TryParseSingleLineValue(backLinkText, string.Empty, out var backLinkValue) ||
            !Path.IsPathFullyQualified(backLinkValue) || !PathsEqual(backLinkValue, gitLink) ||
            !TryParseSingleLineValue(commonLinkText, string.Empty, out var commonLinkValue)) return false;

        var resolvedCommon = Path.GetFullPath(commonLinkValue, metadataDirectory);
        if (!ancestry.IsExistingSafe(resolvedCommon)) return false;
        var worktreesDirectory = Path.GetDirectoryName(metadataDirectory);
        if (worktreesDirectory is null ||
            !string.Equals(Path.GetFileName(worktreesDirectory), "worktrees", StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(Path.GetDirectoryName(worktreesDirectory) ?? string.Empty, resolvedCommon)) return false;

        if (!TryParseSingleLineValue(headText, "ref: ", out var headReference) ||
            !headReference.StartsWith("refs/heads/", StringComparison.Ordinal) ||
            expectedHeadReference is not null && !string.Equals(headReference, expectedHeadReference, StringComparison.Ordinal))
            return false;
        commonDirectory = NormalizeDirectory(resolvedCommon);
        return true;
    }

    private bool ObservedRegularTextFile(string path, out string value)
    {
        value = string.Empty;
        SafeDirectoryEntry entry;
        try { entry = files.Inspect(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        var parent = Path.GetDirectoryName(path);
        return parent is not null && ancestry.IsExistingSafe(parent) && entry.Kind == SafeDirectoryEntryKind.Other &&
               !entry.IsReparseOrLink && files.TryReadSmallTextFile(path, 4_096, out value);
    }

    private static bool TryParseSingleLineValue(string text, string prefix, out string value)
    {
        value = string.Empty;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        if (normalized.Length == 0 || normalized.Contains('\n') || !normalized.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        value = normalized[prefix.Length..].Trim();
        return value.Length > 0;
    }

    public Task VerifyActiveCheckoutAsync(
        string repositoryPath,
        ImplementationPlan plan,
        ActiveCheckoutSignature expected,
        CancellationToken cancellationToken = default) =>
        EnsureActiveCheckoutUnchangedAsync(NormalizeDirectory(repositoryPath), plan, new ImplementationLimits
        {
            MaximumActiveCheckoutFingerprintFiles = Math.Max(10_000, expected.TrackedFileCount),
            MaximumActiveCheckoutFingerprintBytes = Math.Max(50_000_000, expected.TrackedBytes)
        }, expected, cancellationToken);

    public async Task VerifyResultAsync(
        string repositoryPath,
        PreparedImplementationWorkspace prepared,
        ImplementationResult result,
        CancellationToken cancellationToken = default)
    {
        if (!prepared.WorkspaceLock.IsHeld)
            throw Recovery("The isolated workspace lock was lost before result verification.");
        var workspacePath = ResolveWorkspacePath(NormalizeDirectory(options.WorktreeRoot), prepared.Workspace.Token);
        await VerifyWorkspaceIdentityAsync(NormalizeDirectory(repositoryPath), workspacePath, prepared.Workspace, cancellationToken);
        var actual = await ComputeWorktreeFingerprintAsync(workspacePath, prepared.Workspace, result, cancellationToken);
        if (!string.Equals(actual.Hash, result.WorktreeFingerprint, StringComparison.Ordinal) ||
            actual.FileCount != result.WorktreeFileCount || actual.Bytes != result.WorktreeBytes)
            throw Recovery("The isolated implementation workspace changed after result generation.");
    }

    private async Task<Dictionary<string, string?>> ReadAndVerifyActualOutputsAsync(
        string workspacePath,
        IReadOnlyDictionary<string, ImplementationFileContext> contexts,
        IReadOnlyList<ImplementationOperation> operations,
        CancellationToken cancellationToken)
    {
        var actual = new Dictionary<string, string?>(RepositoryPathRules.Comparer);
        foreach (var operation in operations)
        {
            var path = RepositoryPathRules.Normalize(operation.Path);
            var fullPath = fileSafety.ResolveContainedPath(workspacePath, path);
            if (operation.Action == ImplementationOperationAction.Delete)
            {
                if (files.FileExists(fullPath) || files.DirectoryExists(fullPath))
                    throw Recovery($"Delete path '{path}' remains present after application.");
                actual[path] = null;
                continue;
            }
            if (!files.FileExists(fullPath)) throw Recovery($"Generated path '{path}' is missing after application.");
            var content = await ReadStrictUtf8NoBomAsync(fullPath, path, cancellationToken);
            if (!string.Equals(content, operation.Content, StringComparison.Ordinal))
                throw Recovery($"Generated path '{path}' changed during post-apply verification.");
            actual[path] = content;
        }
        return actual;
    }

    private async Task VerifyExpectedStatusAsync(
        string workspacePath,
        IReadOnlyList<ImplementationOperation> operations,
        CancellationToken cancellationToken)
    {
        var status = await StatusAsync(workspacePath, operations.Select(operation => operation.Path), cancellationToken);
        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length != operations.Count)
            throw Recovery("Git reported changes outside the approved implementation set.");
        var expected = operations.ToDictionary(value => RepositoryPathRules.Normalize(value.Path), RepositoryPathRules.Comparer);
        foreach (var entry in entries)
        {
            if (entry.Length < 4) throw Recovery("Git reported an unrecognized worktree change.");
            var code = entry[..2];
            var path = RepositoryPathRules.Normalize(entry[3..]);
            if (!expected.TryGetValue(path, out var operation))
                throw Recovery("Git reported an undeclared worktree change.");
            var expectedCode = operation.Action switch
            {
                ImplementationOperationAction.Create => "??",
                ImplementationOperationAction.Modify => " M",
                ImplementationOperationAction.Delete => " D",
                _ => string.Empty
            };
            if (!string.Equals(code, expectedCode, StringComparison.Ordinal))
                throw Recovery($"Git reported an unexpected action for '{path}'.");
        }
    }

    private async Task EnsureNoActiveFiltersAsync(
        string root,
        ImplementationPlan plan,
        CancellationToken cancellationToken) =>
        await EnsureNoActiveFiltersAsync(root, plan.AffectedFiles.Select(file => file.Path), cancellationToken);

    private async Task EnsureNoActiveFiltersAsync(
        string root,
        IEnumerable<string> approvedPaths,
        CancellationToken cancellationToken)
    {
        var tracked = await RequireOutputAsync(root, ["ls-files", "-z"], "implementation_git_filter", cancellationToken);
        var paths = tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(RepositoryPathRules.Comparer);
        foreach (var approvedPath in approvedPaths)
        {
            var path = RepositoryPathRules.Normalize(approvedPath);
            paths.Add(path);
            var segments = path.Split('/');
            for (var index = 1; index < segments.Length; index++)
                paths.Add(string.Join('/', segments.Take(index)));
        }
        if (paths.Count == 0) return;
        var input = string.Join('\0', paths) + '\0';
        var attributes = await git.RunAsync(root, ["check-attr", "-z", "--stdin", "filter"], input,
            cancellationToken: cancellationToken);
        EnsureCompleteSuccess(attributes, "implementation_git_filter", "Git attributes could not be inspected safely.");
        var values = attributes.Output.Split('\0');
        if ((values.Length - 1) % 3 != 0)
            throw new ImplementationException("implementation_git_filter", "Git returned malformed attribute data.");
        for (var index = 0; index + 2 < values.Length; index += 3)
        {
            var value = values[index + 2];
            if (!string.Equals(value, "unspecified", StringComparison.Ordinal) &&
                !string.Equals(value, "unset", StringComparison.Ordinal))
                throw new ImplementationException("implementation_git_filter",
                    $"Approved repository path '{values[index]}' resolves a Git filter and is not supported.");
        }
    }

    private async Task<IReadOnlyList<ImplementationFileContext>> BuildPreflightContextsAsync(
        string root,
        ImplementationPlan plan,
        ImplementationLimits limits,
        CancellationToken cancellationToken)
    {
        var contexts = new List<ImplementationFileContext>(plan.AffectedFiles.Count);
        var totalCharacters = 0;
        foreach (var file in plan.AffectedFiles)
        {
            var path = RepositoryPathRules.Normalize(file.Path);
            if (file.Action == PlannedFileAction.Create)
            {
                fileSafety.ValidateEligiblePath(root, path, mayNotExist: true);
                var createPath = fileSafety.ResolveContainedPath(root, path);
                if (files.FileExists(createPath) || files.DirectoryExists(createPath))
                    throw new ImplementationException("implementation_terminal_incompatibility",
                        $"Approved create path '{path}' already exists.");
                contexts.Add(new ImplementationFileContext(path, file.Action, null, null));
                continue;
            }
            await EnsureGitAncestorsAreRegularAsync(root, path, cancellationToken);
            await EnsureRegularTrackedFileAsync(root, path, cancellationToken);
            fileSafety.ValidateEligiblePath(root, path, mayNotExist: false);
            var content = await ReadStrictUtf8NoBomAsync(fileSafety.ResolveContainedPath(root, path), path, cancellationToken);
            if (content.Length > limits.MaximumCurrentFileCharacters)
                throw new ImplementationException("implementation_input_limit", $"Approved path '{path}' exceeds the supported writable-text limit.");
            totalCharacters = checked(totalCharacters + content.Length);
            if (totalCharacters > limits.MaximumTotalCurrentCharacters)
                throw new ImplementationException("implementation_input_limit", "Approved writable content exceeds the total implementation-context limit.");
            if (SensitiveContentDetector.ContainsSensitiveValue(content))
                throw new ImplementationException("implementation_sensitive_content", $"Approved path '{path}' contains sensitive values and cannot be generated safely.");
            contexts.Add(new ImplementationFileContext(path, file.Action, content,
                ImplementationOutputValidator.Hash(content)));
        }
        return contexts;
    }

    private static void EnsureContextsMatch(
        IReadOnlyList<ImplementationFileContext> expected,
        IReadOnlyList<ImplementationFileContext> actual)
    {
        if (expected.Count != actual.Count)
            throw Recovery("The prepared implementation context no longer matches its validated preflight.");
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            if (!RepositoryPathRules.Comparer.Equals(RepositoryPathRules.Normalize(left.Path), RepositoryPathRules.Normalize(right.Path)) ||
                left.PlannedAction != right.PlannedAction ||
                !string.Equals(left.OriginalContentSha256, right.OriginalContentSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left.OriginalContent, right.OriginalContent, StringComparison.Ordinal))
                throw Recovery("The prepared implementation context no longer matches its validated preflight.");
        }
    }

    private async Task EnsureGitAncestorsAreRegularAsync(string root, string path, CancellationToken cancellationToken)
    {
        var segments = path.Split('/');
        for (var index = 1; index < segments.Length; index++)
        {
            var ancestor = string.Join('/', segments.Take(index));
            var result = await git.RunAsync(root, ["ls-files", "--stage", "--", ancestor], cancellationToken: cancellationToken);
            if (result.OutputTruncated) throw GitFailure("implementation_unsupported_file", "Git ancestor metadata was truncated.", result);
            if (result.ExitCode != 0) throw GitFailure("implementation_unsupported_file", "Git ancestor metadata could not be inspected.", result);
            foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var mode = line.Split(' ', 2)[0];
                if (mode is "120000" or "160000")
                    throw new ImplementationException("implementation_unsupported_file",
                        $"Approved path '{path}' is beneath a Git symlink, gitlink, or submodule.");
            }
        }
    }

    private async Task EnsureRegularTrackedFileAsync(string root, string path, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(root, ["ls-files", "--stage", "--", path], cancellationToken: cancellationToken);
        if (result.OutputTruncated || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new ImplementationException("implementation_unsupported_file", $"Approved path '{path}' is not a tracked regular file.");
        var mode = result.Output.Split(' ', 2)[0];
        if (mode is "120000" or "160000")
            throw new ImplementationException("implementation_unsupported_file", $"Approved path '{path}' is a symlink, gitlink, or submodule.");
        if (mode is not "100644")
            throw new ImplementationException("implementation_unsupported_file", $"Approved path '{path}' has an unsupported Git file mode.");
    }

    private async Task VerifyRepositoryIdentityAsync(string root, ImplementationWorkspace workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspace.RepositoryIdentity) ||
            string.IsNullOrWhiteSpace(workspace.GitCommonDirectoryIdentity) ||
            string.IsNullOrWhiteSpace(workspace.OwnershipReference))
            throw Recovery("The persisted workspace lacks repository ownership identity.");
        var top = (await RequireOutputAsync(root, ["rev-parse", "--show-toplevel"], "implementation_workspace_conflict", cancellationToken)).Trim();
        var common = await ResolveGitCommonDirectoryAsync(root, cancellationToken);
        if (!PathsEqual(root, top) ||
            !string.Equals(HashCanonicalPath(root), workspace.RepositoryIdentity, StringComparison.Ordinal) ||
            !string.Equals(HashCanonicalPath(common), workspace.GitCommonDirectoryIdentity, StringComparison.Ordinal))
            throw Recovery("The source repository identity no longer matches the persisted implementation workspace.");
    }

    private async Task VerifyWorkspaceIdentityAsync(
        string root,
        string workspacePath,
        ImplementationWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workspacePath)) throw Recovery("The registered isolated implementation worktree is missing.");
        EnsureDirectoryAndAncestorsAreSafe(workspacePath, "The isolated implementation worktree is unsafe.", NormalizeDirectory(options.WorktreeRoot));
        await VerifyOwnershipStateAsync(root, workspacePath, workspace.Branch, workspace.OwnershipReference,
            workspace.BaseCommitSha, allowUnreserved: false, cancellationToken);
        var top = (await RequireOutputAsync(workspacePath, ["rev-parse", "--show-toplevel"], "implementation_workspace_conflict", cancellationToken)).Trim();
        var branch = (await RequireOutputAsync(workspacePath, ["symbolic-ref", "--short", "HEAD"], "implementation_workspace_conflict", cancellationToken)).Trim();
        var head = (await RequireOutputAsync(workspacePath, ["rev-parse", "HEAD"], "implementation_workspace_conflict", cancellationToken)).Trim();
        var common = await ResolveGitCommonDirectoryAsync(workspacePath, cancellationToken);
        if (!PathsEqual(workspacePath, top) ||
            !string.Equals(branch, workspace.Branch, StringComparison.Ordinal) ||
            !string.Equals(head, workspace.BaseCommitSha, StringComparison.Ordinal) ||
            !string.Equals(HashCanonicalPath(common), workspace.GitCommonDirectoryIdentity, StringComparison.Ordinal))
            throw Recovery("The isolated worktree identity does not match the persisted task workspace.");
    }

    private async Task VerifyOwnershipStateAsync(
        string root,
        string workspacePath,
        string branch,
        string ownerReference,
        string baseSha,
        bool allowUnreserved,
        CancellationToken cancellationToken)
    {
        if (!ownerReference.Equals($"refs/forge/tasks/{Path.GetFileName(workspacePath)}", StringComparison.Ordinal))
            throw Recovery("The persisted workspace token and ownership marker do not match.");
        var owner = await ReadOptionalRefAsync(root, ownerReference, cancellationToken);
        var branchHead = await ReadOptionalRefAsync(root, $"refs/heads/{branch}", cancellationToken);
        if (owner is null && branchHead is not null)
            throw new ImplementationException("implementation_workspace_conflict", "A matching branch exists without a Forge ownership marker.", true);
        if (owner is not null && !string.Equals(owner, baseSha, StringComparison.Ordinal))
            throw Recovery("The Forge workspace ownership marker points to a different base commit.");
        if (branchHead is not null && !string.Equals(branchHead, baseSha, StringComparison.Ordinal))
            throw Recovery("The implementation branch points to a different commit.");
        if (!allowUnreserved && (owner is null || branchHead is null))
            throw Recovery("The implementation branch is not durably owned by this Forge task.");

        var registrations = await ReadWorktreeRegistrationsAsync(root, cancellationToken);
        var expectedRegistration = registrations.SingleOrDefault(item => PathsEqual(item.Path, workspacePath));
        if (expectedRegistration is not null)
        {
            if (!Directory.Exists(expectedRegistration.Path))
                throw Recovery("A registered implementation worktree is missing from disk.");
            if (!string.Equals(expectedRegistration.Head, baseSha, StringComparison.Ordinal) ||
                !string.Equals(expectedRegistration.Branch, $"refs/heads/{branch}", StringComparison.Ordinal))
                throw Recovery("The registered implementation worktree has a different branch or commit.");
        }
        else if (Directory.Exists(workspacePath))
        {
            throw new ImplementationException("implementation_workspace_conflict",
                "The deterministic workspace path exists but is not a registered Git worktree.", true);
        }
        var branchRegistration = registrations.FirstOrDefault(item =>
            string.Equals(item.Branch, $"refs/heads/{branch}", StringComparison.Ordinal));
        if (branchRegistration is not null && !PathsEqual(branchRegistration.Path, workspacePath))
            throw Recovery("The owned implementation branch is registered at an unexpected worktree path.");
    }

    private async Task<IReadOnlyList<WorktreeRegistration>> ReadWorktreeRegistrationsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var output = await RequireOutputAsync(root, ["worktree", "list", "--porcelain", "-z"],
            "implementation_workspace_conflict", cancellationToken);
        var records = new List<WorktreeRegistration>();
        string? path = null;
        string? head = null;
        string? branch = null;
        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (path is not null) records.Add(new WorktreeRegistration(path, head ?? string.Empty, branch));
                path = head = branch = null;
                continue;
            }
            if (field.StartsWith("worktree ", StringComparison.Ordinal)) path = field[9..];
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = field[5..];
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field[7..];
        }
        if (path is not null) records.Add(new WorktreeRegistration(path, head ?? string.Empty, branch));
        return records;
    }

    private async Task<string?> ReadOptionalRefAsync(string root, string reference, CancellationToken cancellationToken)
    {
        var exists = await git.RunAsync(root, ["show-ref", "--verify", "--quiet", reference], cancellationToken: cancellationToken);
        if (exists.OutputTruncated) throw GitFailure("implementation_workspace_conflict", "Git reference output was truncated.", exists);
        if (exists.ExitCode == 1) return null;
        if (exists.ExitCode != 0) throw GitFailure("implementation_workspace_conflict", "Git references could not be inspected safely.", exists);
        return (await RequireOutputAsync(root, ["rev-parse", "--verify", $"{reference}^{{commit}}"],
            "implementation_workspace_conflict", cancellationToken)).Trim();
    }

    private async Task<string> ResolveGitCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var value = (await RequireOutputAsync(workingDirectory, ["rev-parse", "--git-common-dir"],
            "implementation_workspace_conflict", cancellationToken)).Trim();
        var fullPath = Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : Path.GetFullPath(value, workingDirectory);
        if (!Directory.Exists(fullPath)) throw Recovery("The Git common directory is missing.");
        EnsureDirectoryAndAncestorsAreSafe(fullPath, "The Git common directory is unsafe.");
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async Task EnsureActiveCheckoutUnchangedAsync(
        string root,
        ImplementationPlan plan,
        ImplementationLimits limits,
        ActiveCheckoutSignature expected,
        CancellationToken cancellationToken) =>
        await EnsureActiveCheckoutUnchangedAsync(root, plan.AffectedFiles.Select(file => file.Path), limits, expected,
            cancellationToken);

    private async Task EnsureActiveCheckoutUnchangedAsync(
        string root,
        IEnumerable<string> approvedPaths,
        ImplementationLimits limits,
        ActiveCheckoutSignature expected,
        CancellationToken cancellationToken)
    {
        var actual = await CaptureActiveCheckoutAsync(root, approvedPaths, limits, cancellationToken);
        if (actual != expected)
            throw new ImplementationException("implementation_active_checkout_changed",
                "The active checkout changed during implementation generation. No further worktree changes were accepted.", true);
    }

    private async Task<ActiveCheckoutSignature> CaptureActiveCheckoutAsync(
        string root,
        IEnumerable<string> approvedPaths,
        ImplementationLimits limits,
        CancellationToken cancellationToken)
    {
        await EnsureSupportedIndexAsync(root, cancellationToken);
        var branch = (await RequireOutputAsync(root, ["symbolic-ref", "--short", "HEAD"], "implementation_repository_state", cancellationToken)).Trim();
        var head = (await RequireOutputAsync(root, ["rev-parse", "HEAD"], "implementation_repository_state", cancellationToken)).Trim();
        var status = await StatusAsync(root, approvedPaths, cancellationToken);
        var indexPath = (await RequireOutputAsync(root, ["rev-parse", "--git-path", "index"], "implementation_repository_state", cancellationToken)).Trim();
        if (!Path.IsPathFullyQualified(indexPath)) indexPath = Path.GetFullPath(indexPath, root);
        var indexHash = File.Exists(indexPath) ? HashBytes(await File.ReadAllBytesAsync(indexPath, cancellationToken)) : "missing";
        var content = await CaptureTrackedContentFingerprintAsync(root, limits, cancellationToken);
        return new ActiveCheckoutSignature(branch, head, HashText(status), indexHash,
            content.Hash, content.FileCount, content.Bytes);
    }

    private async Task EnsureSupportedIndexAsync(string root, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { "core.sparseCheckout", "core.sparseCheckoutCone", "index.sparse" })
        {
            var configured = await git.RunAsync(root, ["config", "--bool", "--get", key], cancellationToken: cancellationToken);
            if (configured.OutputTruncated || configured.ExitCode is not (0 or 1))
                throw GitFailure("implementation_repository_state", "Git index configuration could not be inspected safely.", configured);
            if (configured.ExitCode == 0 && string.Equals(configured.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                throw new ImplementationException("implementation_repository_state", "Sparse checkout and sparse index states are not supported for implementation generation.");
        }
    }

    private async Task<(string Hash, int FileCount, long Bytes)> CaptureTrackedContentFingerprintAsync(
        string root,
        ImplementationLimits limits,
        CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(root, ["ls-files", "--stage", "-v", "-z"],
            maximumOutputCharacters: Math.Max(1_000_000, limits.MaximumActiveCheckoutFingerprintFiles * 400),
            cancellationToken: cancellationToken);
        EnsureCompleteSuccess(result, "implementation_repository_state", "The complete Git index could not be inspected safely.");
        if (result.Output.Length > 0 && result.Output[^1] != '\0')
            throw new ImplementationException("implementation_repository_state", "Git returned truncated index metadata.");
        using var composite = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        long totalBytes = 0;
        foreach (var entry in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = entry.IndexOf('\t');
            if (tab < 0 || tab + 1 >= entry.Length) throw new ImplementationException("implementation_repository_state", "Git returned malformed index metadata.");
            var metadata = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 4 || metadata[0].Length != 1 || metadata[3] != "0")
                throw new ImplementationException("implementation_repository_state", "The Git index contains unresolved or abnormal stages.");
            var tag = metadata[0][0];
            if (char.IsLower(tag) || tag != 'H')
                throw new ImplementationException("implementation_repository_state", "The Git index contains assume-unchanged, skip-worktree, sparse, or abnormal entries.");
            var mode = metadata[1];
            if (mode == "040000")
                throw new ImplementationException("implementation_repository_state", "Sparse index directory entries are not supported.");
            if (mode is "120000" or "160000") continue;
            if (mode is not ("100644" or "100755"))
                throw new ImplementationException("implementation_repository_state", "The Git index contains an unsupported tracked file mode.");
            var path = RepositoryPathRules.Normalize(entry[(tab + 1)..]);
            if (!RepositoryPathRules.IsSafeRelativePath(path))
                throw new ImplementationException("implementation_repository_state", "The Git index contains an unsafe path.");
            var fullPath = fileSafety.ResolveContainedPath(root, path);
            if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new ImplementationException("implementation_repository_state", "A tracked regular working-tree file is missing or unsafe.");
            var info = new FileInfo(fullPath);
            totalBytes = checked(totalBytes + info.Length);
            count = checked(count + 1);
            if (count > limits.MaximumActiveCheckoutFingerprintFiles || totalBytes > limits.MaximumActiveCheckoutFingerprintBytes)
                throw new ImplementationException("implementation_fingerprint_limit", "The active checkout exceeds the configured content-fingerprint budget.");
            composite.AppendData(Encoding.UTF8.GetBytes($"{path}\0{info.Length}\0"));
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileHash = await SHA256.HashDataAsync(stream, cancellationToken);
            composite.AppendData(fileHash);
        }
        return (Convert.ToHexString(composite.GetHashAndReset()).ToLowerInvariant(), count, totalBytes);
    }

    private async Task<(string Hash, int FileCount, long Bytes)> ComputeWorktreeFingerprintAsync(
        string workspacePath,
        ImplementationWorkspace workspace,
        ImplementationResult result,
        CancellationToken cancellationToken)
    {
        var operations = result.ChangedFiles.Select(file => new ImplementationOperation(
            file.Path, file.Action, file.OriginalContentSha256, null, "Persisted review operation.")).ToArray();
        await VerifyExpectedStatusAsync(workspacePath, operations, cancellationToken);
        var status = await StatusAsync(workspacePath, operations.Select(operation => operation.Path), cancellationToken);
        var manifest = new List<object?>
        {
            "forge-implementation-worktree-v2",
            workspace.Token, workspace.Branch, workspace.BaseCommitSha, workspace.RepositoryIdentity,
            workspace.GitCommonDirectoryIdentity, workspace.OwnershipReference, status,
            result.Source, result.Model, result.Summary, result.Warnings.Count
        };
        foreach (var warning in result.Warnings) manifest.Add(warning);
        manifest.AddRange([
            result.FullDiffCharacters, result.DisplayedDiffCharacters, result.FullDiffUtf8Bytes,
            result.DisplayedDiffUtf8Bytes, result.DiffTruncated, result.CompletedAt, result.ActiveCheckoutVerified
        ]);
        long bytes = 0;
        foreach (var file in result.ChangedFiles.OrderBy(value => value.Path, RepositoryPathRules.Comparer))
        {
            var fullPath = fileSafety.ResolveContainedPath(workspacePath, file.Path);
            string actualHash;
            long actualBytes;
            int actualLines;
            if (file.Action == ImplementationOperationAction.Delete)
            {
                if (files.FileExists(fullPath) || files.DirectoryExists(fullPath)) throw Recovery("A deleted implementation path reappeared.");
                actualHash = "deleted";
                actualBytes = 0;
                actualLines = 0;
                if (file.NewBytes != 0 || file.NewLines != 0) throw Recovery("Deleted-file review metadata is inconsistent.");
            }
            else
            {
                if (!files.FileExists(fullPath)) throw Recovery("A generated implementation path is missing.");
                var content = await ReadStrictUtf8NoBomAsync(fullPath, file.Path, cancellationToken);
                actualHash = ImplementationOutputValidator.Hash(content);
                actualBytes = Encoding.UTF8.GetByteCount(content);
                actualLines = CountLines(content);
                if (!string.Equals(actualHash, file.NewContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw Recovery("Generated implementation content no longer matches its persisted review.");
                if (actualBytes != file.NewBytes || actualLines != file.NewLines)
                    throw Recovery("Generated implementation metadata no longer matches its persisted review.");
            }
            bytes = checked(bytes + actualBytes);
            manifest.AddRange([
                file.Path, file.Action, file.OriginalContentSha256, file.NewContentSha256,
                file.OriginalBytes, file.NewBytes, file.OriginalLines, file.NewLines,
                file.Additions, file.Deletions, file.FullDiffCharacters, file.DisplayedDiffCharacters,
                file.FullDiffUtf8Bytes, file.DisplayedDiffUtf8Bytes, file.DiffTruncated,
                HashText(file.DiffPreview), actualHash, actualBytes, actualLines
            ]);
        }
        manifest.Add(result.ChangedFiles.Count);
        manifest.Add(bytes);
        if (bytes != result.WorktreeBytes && result.WorktreeFingerprint.Length > 0)
            throw Recovery("The implementation worktree byte total no longer matches its persisted review.");
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest));
        return (Convert.ToHexString(fingerprint).ToLowerInvariant(), result.ChangedFiles.Count, bytes);
    }

    private async Task EnsureNoGitOperationInProgressAsync(string root, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" })
        {
            var path = (await RequireOutputAsync(root, ["rev-parse", "--git-path", name], "implementation_repository_state", cancellationToken)).Trim();
            if (!Path.IsPathFullyQualified(path)) path = Path.GetFullPath(path, root);
            if (File.Exists(path) || Directory.Exists(path))
                throw new ImplementationException("implementation_repository_state", "The repository has an in-progress merge, rebase, cherry-pick, or revert operation.");
        }
    }

    private async Task<string> StatusAsync(
        string root,
        IEnumerable<string> approvedPaths,
        CancellationToken cancellationToken)
    {
        await EnsureNoActiveFiltersAsync(root, approvedPaths, cancellationToken);
        return await RequireOutputAsync(root,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames"],
            "implementation_repository_state", cancellationToken);
    }

    private async Task<string> ReadStrictUtf8NoBomAsync(string fullPath, string safePath, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await files.ReadAllBytesAsync(fullPath, cancellationToken);
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
                throw new DecoderFallbackException("UTF-8 byte-order marks are not supported for writable context.");
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ImplementationException("implementation_unsupported_file",
                $"Approved path '{safePath}' is not readable supported strict UTF-8 text without a byte-order mark.", false, exception);
        }
    }

    private async Task<string> RequireOutputAsync(
        string root,
        IReadOnlyList<string> arguments,
        string category,
        CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(root, arguments, cancellationToken: cancellationToken);
        EnsureCompleteSuccess(result, category, "Git could not inspect the repository safely.");
        return result.Output;
    }

    private async Task RequireSuccessAsync(
        string root,
        IReadOnlyList<string> arguments,
        string category,
        string message,
        CancellationToken cancellationToken,
        string? standardInput = null,
        GitCommandKind commandKind = GitCommandKind.ReadOnly)
    {
        var result = await git.RunAsync(root, arguments, standardInput,
            cancellationToken: cancellationToken, commandKind: commandKind);
        EnsureCompleteSuccess(result, category, message, commandKind == GitCommandKind.Mutating);
    }

    private static void EnsureCompleteSuccess(
        GitProcessResult result,
        string category,
        string message,
        bool recoveryRequired = false)
    {
        if (result.ExitCode != 0 || result.OutputTruncated)
            throw GitFailure(category, message, result, recoveryRequired);
    }

    private static (int Additions, int Deletions) ParseNumStat(string value, string path)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).SingleOrDefault()
            ?? throw Recovery($"Git did not report line counts for '{path}'.");
        var parts = line.Split('\t');
        if (parts.Length < 3 || parts[0] == "-" || parts[1] == "-" ||
            !int.TryParse(parts[0], out var additions) || !int.TryParse(parts[1], out var deletions))
            throw Recovery($"Git reported unsupported binary diff counts for '{path}'.");
        return (additions, deletions);
    }

    private IImplementationWorkspaceLock AcquireWorkspaceLock(string worktreeRoot, string token)
    {
        var directory = Path.Combine(worktreeRoot, ".locks");
        Directory.CreateDirectory(directory);
        EnsureDirectoryAndAncestorsAreSafe(directory, "The Forge workspace-lock directory is unsafe.", worktreeRoot);
        var path = Path.Combine(directory, $"{token}.lock");
        try
        {
            return new WorkspaceFileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new ImplementationException("implementation_workspace_lock",
                "Another process currently holds the isolated implementation workspace lock.", false, exception);
        }
    }

    internal static string TruncateDiffPreviewRuneSafe(string value, int maximumCharacters)
    {
        if (maximumCharacters <= 0 || value.Length == 0) return string.Empty;
        if (value.Length <= maximumCharacters) return value;
        var length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) && length < value.Length && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }

    private static string ResolveWorkspacePath(string worktreeRoot, string token)
    {
        if (token.Length != 32 || token.Any(character => !Uri.IsHexDigit(character)))
            throw new ImplementationException("implementation_workspace_conflict", "The persisted implementation workspace token is invalid.");
        var path = Path.GetFullPath(token, worktreeRoot);
        var prefix = worktreeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ImplementationException("implementation_workspace_conflict", "The implementation workspace escaped the configured Forge root.");
        return path;
    }

    private static void EnsureOutsideRepository(string repositoryRoot, string worktreeRoot)
    {
        var repo = repositoryRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var worktree = worktreeRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (worktree.StartsWith(repo, StringComparison.OrdinalIgnoreCase) || repo.StartsWith(worktree, StringComparison.OrdinalIgnoreCase))
            throw new ImplementationException("implementation_workspace_configuration", "The Forge worktree root must be outside the selected repository.");
    }

    private static void EnsureDirectoryAndAncestorsAreSafe(string path, string message, string? stopAt = null)
    {
        if (!Directory.Exists(path)) throw new ImplementationException("implementation_unsafe_path", message);
        var fullPath = Path.GetFullPath(path);
        var stop = stopAt is null ? Path.GetPathRoot(fullPath) : Path.GetFullPath(stopAt);
        var current = fullPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ImplementationException("implementation_unsafe_path", message);
            if (PathsEqual(current, stop!)) break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, current)) break;
            current = parent;
        }
    }

    private static string NormalizeDirectory(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ImplementationException("implementation_unsafe_path", "A configured repository or worktree path is invalid.", false, exception);
        }
    }

    private static ImplementationException GitFailure(
        string category,
        string message,
        GitProcessResult result,
        bool recoveryRequired = false) =>
        new(category,
            result.OutputTruncated
                ? $"{message} Required Git output was truncated safely."
                : result.ErrorTruncated ? $"{message} Git diagnostics were truncated safely." : message,
            recoveryRequired);

    private static ImplementationException Recovery(string message) =>
        new("implementation_recovery_required", message, true);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string HashCanonicalPath(string path) =>
        HashText(Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant());
    private static string HashText(string value) => HashBytes(Encoding.UTF8.GetBytes(value));
    private static string HashBytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static long Utf8Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
    private static int CountLines(string? value) => string.IsNullOrEmpty(value) ? 0 : value.Count(character => character == '\n') + 1;

    private sealed record WorktreeRegistration(string Path, string Head, string? Branch);

    private sealed class WorkspaceFileLock(FileStream stream) : IImplementationWorkspaceLock
    {
        private FileStream? stream = stream;
        public bool IsHeld => stream is not null;
        public ValueTask DisposeAsync()
        {
            stream?.Dispose();
            stream = null;
            return ValueTask.CompletedTask;
        }
    }
}
