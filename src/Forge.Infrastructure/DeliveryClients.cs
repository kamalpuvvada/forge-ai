using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Infrastructure;

public interface IDeliveryExecutableAvailability
{
    bool GitAvailable { get; }
    bool GitHubCliAvailable { get; }
}

public sealed class DeliveryExecutableAvailability : IDeliveryExecutableAvailability
{
    public bool GitAvailable => IsAvailable("git");
    public bool GitHubCliAvailable => IsAvailable("gh");

    private static bool IsAvailable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : new[] { string.Empty };
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => extensions.Any(extension => File.Exists(Path.Combine(directory, name + extension))));
    }
}

public sealed class DeliveryProcessOptions
{
    public string WorktreeRoot { get; set; } = string.Empty;
    public string HooksDirectory { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaximumOutputCharacters { get; set; } = 32_768;
}

public sealed record DeliveryProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IDeliveryProcessRunner
{
    Task<DeliveryProcessResult> RunAsync(string executable, string workingDirectory,
        IReadOnlyList<string> arguments, string? standardInput = null,
        CancellationToken cancellationToken = default);
}

public sealed class DeliveryProcessRunner(DeliveryProcessOptions options) : IDeliveryProcessRunner
{
    public async Task<DeliveryProcessResult> RunAsync(string executable, string workingDirectory,
        IReadOnlyList<string> arguments, string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Directory.Exists(workingDirectory))
            throw new DeliveryException("delivery_failed_before_mutation", "A required delivery process is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        if (standardInput is not null) start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("GITHUB_TOKEN");
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new DeliveryException("delivery_failed_before_mutation", "A required delivery process could not start.");
        }
        catch (Exception exception) when (exception is not DeliveryException)
        {
            throw new DeliveryException("delivery_failed_before_mutation", "A required delivery process could not start.", false, exception);
        }
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, options.MaximumOutputCharacters, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, options.MaximumOutputCharacters, cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new DeliveryException("delivery_recovery_required", "A delivery process timed out; its external outcome is uncertain.", true);
        }
        return new DeliveryProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > maximum)
                throw new DeliveryException("delivery_recovery_required", "A delivery process returned oversized output.", true);
            builder.Append(buffer, 0, read);
        }
    }
}

public sealed partial class GitHubDeliveryClient(
    IDeliveryProcessRunner process,
    IGitExecutablePathResolver gitResolver,
    DeliveryProcessOptions options) : IDeliveryGitClient
{
    public async Task<DeliveryPreflight> PreflightAsync(string repositoryPath, RepositorySnapshot snapshot, ImplementationPlan plan,
        ImplementationWorkspace workspace, ImplementationResult result, string deliveryBranch,
        CancellationToken cancellationToken = default)
    {
        var root = ExactDirectory(repositoryPath);
        var git = gitResolver.Resolve(null);
        var remotes = Lines(await Require(git, root, ["remote"], "delivery_remote_invalid", cancellationToken));
        if (remotes.Length != 1 || remotes[0] != "origin")
            throw new DeliveryException("delivery_remote_invalid", "Delivery requires exactly one Git remote named origin.");
        var fetchUrls = Lines(await Require(git, root, ["remote", "get-url", "--all", "origin"],
            "delivery_remote_invalid", cancellationToken));
        if (fetchUrls.Length != 1)
            throw new DeliveryException("delivery_remote_invalid", "Delivery requires exactly one origin fetch destination.");
        var (owner, repository) = ParseGitHubRemote(fetchUrls[0]);
        await VerifyApprovedPushDestinationAsync(git, root, owner, repository, false, cancellationToken);
        var branch = (await Require(git, root, ["branch", "--show-current"],
            "delivery_stale_binding", cancellationToken)).Trim();
        var head = (await Require(git, root, ["rev-parse", "HEAD"], "delivery_stale_binding", cancellationToken)).Trim();
        var status = await Require(git, root, ["status", "--porcelain=v1", "--untracked-files=all"],
            "delivery_stale_binding", cancellationToken);
        if (snapshot.Branch != "main" || branch != snapshot.Branch || head != workspace.BaseCommitSha || status.Length != 0)
            throw new DeliveryException("delivery_stale_binding", "The protected active checkout changed after implementation approval.");
        await Require(git, root, ["show-ref", "--verify", "refs/heads/main"], "delivery_remote_invalid", cancellationToken);
        await Require(git, root, ["cat-file", "-e", workspace.BaseCommitSha + "^{commit}"], "delivery_stale_binding", cancellationToken);
        var remoteBase = ParseLsRemote(await Require(git, root,
            ["ls-remote", "--heads", "origin", "refs/heads/main"], "delivery_remote_invalid", cancellationToken));
        if (remoteBase is null) throw new DeliveryException("delivery_remote_invalid", "The origin main branch is unavailable.");
        if (!string.Equals(remoteBase, workspace.BaseCommitSha, StringComparison.Ordinal))
            throw new DeliveryException("delivery_remote_conflict",
                "The origin main branch no longer matches the approved implementation base commit.");
        var localBranch = await process.RunAsync(git, root, ["show-ref", "--verify", "--quiet", $"refs/heads/{deliveryBranch}"], cancellationToken: cancellationToken);
        if (localBranch.ExitCode == 0) throw new DeliveryException("delivery_branch_conflict", "The Forge delivery branch already exists locally.");
        var remoteBranch = ParseLsRemote(await Require(git, root,
            ["ls-remote", "--heads", "origin", $"refs/heads/{deliveryBranch}"], "delivery_remote_invalid", cancellationToken));
        if (remoteBranch is not null) throw new DeliveryException("delivery_branch_conflict", "The Forge delivery branch already exists on origin.");
        var workspacePath = ResolveWorkspace(workspace.Token);
        VerifyWorkspace(workspacePath, plan, result);
        return new DeliveryPreflight("origin", owner, repository, "main", remoteBase, true, true);
    }

    public async Task<DeliveryCommitResult> CreateCommitAsync(DeliveryExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var git = gitResolver.Resolve(null);
        var root = ResolveWorkspace(context.Workspace.Token);
        VerifyWorkspace(root, context.Plan, context.Result);
        var expectedTree = await ReadExpectedTreeObjectsAsync(git, root, context.Result, cancellationToken);
        await Require(git, root, ["switch", "-c", context.Proposal.DeliveryBranch],
            "delivery_recovery_required", cancellationToken, true);
        var paths = context.Proposal.ChangedPaths.ToArray();
        var add = new List<string> { "add", "-A", "--" }; add.AddRange(paths);
        await Require(git, root, add, "delivery_recovery_required", cancellationToken, true);
        var staged = ParseNameStatus(await Require(git, root,
            ["diff", "--cached", "--name-status", "--no-renames"], "delivery_scope_mismatch", cancellationToken));
        VerifyExpected(staged, context.Plan, context.Result);
        if ((await Require(git, root, ["diff", "--name-only"], "delivery_scope_mismatch", cancellationToken)).Length != 0 ||
            (await Require(git, root, ["ls-files", "--others", "--exclude-standard"], "delivery_scope_mismatch", cancellationToken)).Length != 0)
            throw new DeliveryException("delivery_scope_mismatch", "The delivery worktree contains unstaged or untracked paths.", true);
        var hooks = CreateEmptyHooksDirectory();
        try
        {
            var commitArgs = new[] { "-c", $"core.hooksPath={hooks}", "-c", "commit.gpgSign=false",
                "commit", "--no-verify", "--no-gpg-sign", "-m", context.Proposal.CommitMessage };
            await Require(git, root, commitArgs, "delivery_recovery_required", cancellationToken, true);
        }
        finally { TryDeleteEmptyHooksDirectory(hooks); }
        var sha = (await Require(git, root, ["rev-parse", "HEAD"], "delivery_recovery_required", cancellationToken)).Trim();
        var parent = (await Require(git, root, ["rev-parse", "HEAD^"], "delivery_recovery_required", cancellationToken)).Trim();
        var message = (await Require(git, root, ["log", "-1", "--format=%B"], "delivery_recovery_required", cancellationToken)).TrimEnd();
        var committed = ParseNameStatus(await Require(git, root,
            ["diff-tree", "--no-commit-id", "--name-status", "--no-renames", "-r", "HEAD"],
            "delivery_scope_mismatch", cancellationToken));
        VerifyExpected(committed, context.Plan, context.Result);
        await VerifyCommitTreeAsync(git, root, expectedTree, cancellationToken);
        if (parent != context.Proposal.BaseCommitSha || message != context.Proposal.CommitMessage)
            throw new DeliveryException("delivery_recovery_required", "The created commit did not match the approved parent or message.", true);
        await VerifyActiveCheckout(root: ExactDirectory(context.RepositoryPath), context.RepositorySnapshot, context.Workspace, cancellationToken);
        return new DeliveryCommitResult(sha, true);
    }

    public async Task<DeliveryPushResult> PushAsync(DeliveryExecutionContext context, string commitSha,
        CancellationToken cancellationToken = default)
    {
        var git = gitResolver.Resolve(null);
        var root = ResolveWorkspace(context.Workspace.Token);
        await VerifyApprovedPushDestinationAsync(git, ExactDirectory(context.RepositoryPath),
            context.Proposal.GitHubRepositoryOwner, context.Proposal.GitHubRepositoryName, true, cancellationToken);
        var hooks = CreateEmptyHooksDirectory();
        var arguments = new[] { "-c", $"core.hooksPath={hooks}", "push", "--no-verify", "origin",
            $"{commitSha}:refs/heads/{context.Proposal.DeliveryBranch}" };
        if (arguments.Any(value => value.Contains("--force", StringComparison.Ordinal) || value.StartsWith('+')) ||
            context.Proposal.DeliveryBranch is "main" or "master")
            throw new DeliveryException("delivery_recovery_required", "The delivery push destination is unsafe.", true);
        try { await Require(git, root, arguments, "delivery_recovery_required", cancellationToken, true); }
        finally { TryDeleteEmptyHooksDirectory(hooks); }
        var remote = ParseLsRemote(await Require(git, root,
            ["ls-remote", "--heads", "origin", $"refs/heads/{context.Proposal.DeliveryBranch}"],
            "delivery_recovery_required", cancellationToken));
        if (remote != commitSha) throw new DeliveryException("delivery_recovery_required", "The pushed branch SHA could not be verified.", true);
        await VerifyActiveCheckout(ExactDirectory(context.RepositoryPath), context.RepositorySnapshot, context.Workspace, cancellationToken);
        return new DeliveryPushResult(remote, true);
    }

    public async Task<string?> InspectMatchingCommitAsync(DeliveryExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var git = gitResolver.Resolve(null);
        var root = ResolveWorkspace(context.Workspace.Token);
        var branch = (await Require(git, root, ["branch", "--show-current"], "delivery_recovery_required", cancellationToken)).Trim();
        if (branch != context.Proposal.DeliveryBranch) return null;
        var sha = (await Require(git, root, ["rev-parse", "HEAD"], "delivery_recovery_required", cancellationToken)).Trim();
        var parent = (await Require(git, root, ["rev-parse", "HEAD^"], "delivery_recovery_required", cancellationToken)).Trim();
        var message = (await Require(git, root, ["log", "-1", "--format=%B"], "delivery_recovery_required", cancellationToken)).TrimEnd();
        var committed = ParseNameStatus(await Require(git, root,
            ["diff-tree", "--no-commit-id", "--name-status", "--no-renames", "-r", "HEAD"],
            "delivery_recovery_required", cancellationToken));
        try { VerifyExpected(committed, context.Plan, context.Result); }
        catch (DeliveryException) { return null; }
        var expectedTree = await ReadExpectedTreeObjectsAsync(git, root, context.Result, cancellationToken);
        try { await VerifyCommitTreeAsync(git, root, expectedTree, cancellationToken); }
        catch (DeliveryException) { return null; }
        if (parent != context.Proposal.BaseCommitSha || message != context.Proposal.CommitMessage) return null;
        await VerifyActiveCheckout(ExactDirectory(context.RepositoryPath), context.RepositorySnapshot,
            context.Workspace, cancellationToken);
        return sha;
    }

    public async Task<string?> ReadRemoteBranchAsync(string repositoryPath, string branch,
        CancellationToken cancellationToken = default) => ParseLsRemote(await Require(gitResolver.Resolve(null),
        ExactDirectory(repositoryPath), ["ls-remote", "--heads", "origin", $"refs/heads/{branch}"],
        "delivery_recovery_required", cancellationToken));

    private async Task VerifyActiveCheckout(string root, RepositorySnapshot snapshot, ImplementationWorkspace workspace, CancellationToken cancellationToken)
    {
        var git = gitResolver.Resolve(null);
        var branch = (await Require(git, root, ["branch", "--show-current"], "delivery_recovery_required", cancellationToken)).Trim();
        var head = (await Require(git, root, ["rev-parse", "HEAD"], "delivery_recovery_required", cancellationToken)).Trim();
        var status = await Require(git, root, ["status", "--porcelain=v1", "--untracked-files=all"], "delivery_recovery_required", cancellationToken);
        var content = await CaptureTrackedContentFingerprintAsync(git, root, workspace, cancellationToken);
        if (branch != "main" || branch != snapshot.Branch || head != workspace.BaseCommitSha || status.Length != 0 ||
            !string.Equals(content.Hash, workspace.ActiveCheckoutContentFingerprint, StringComparison.Ordinal) ||
            content.FileCount != workspace.ActiveCheckoutTrackedFileCount || content.Bytes != workspace.ActiveCheckoutTrackedBytes)
            throw new DeliveryException("delivery_recovery_required", "The protected active checkout changed during delivery.", true);
    }

    private async Task<(string Hash, int FileCount, long Bytes)> CaptureTrackedContentFingerprintAsync(
        string git, string root, ImplementationWorkspace workspace, CancellationToken cancellationToken)
    {
        var output = await Require(git, root, ["ls-files", "--stage", "-v", "-z"],
            "delivery_recovery_required", cancellationToken);
        if (output.Length > 0 && output[^1] != '\0')
            throw new DeliveryException("delivery_recovery_required", "The protected checkout index inspection was incomplete.", true);
        using var composite = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        long bytes = 0;
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            var metadata = tab > 0 ? entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            if (metadata.Length != 4 || metadata[0] != "H" || metadata[3] != "0")
                throw new DeliveryException("delivery_recovery_required", "The protected checkout index is unsupported.", true);
            var mode = metadata[1];
            if (mode is "120000" or "160000") continue;
            if (mode is not ("100644" or "100755"))
                throw new DeliveryException("delivery_recovery_required", "The protected checkout contains an unsupported tracked entry.", true);
            var path = RepositoryPathRules.Normalize(entry[(tab + 1)..]);
            if (!RepositoryPathRules.IsSafeRelativePath(path))
                throw new DeliveryException("delivery_recovery_required", "The protected checkout contains an unsafe tracked path.", true);
            var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new DeliveryException("delivery_recovery_required", "The protected checkout contains an unavailable tracked file.", true);
            try { new SafeDirectoryAncestry().EnsureExisting(Path.GetDirectoryName(full)!); }
            catch (ImplementationException exception)
            {
                throw new DeliveryException("delivery_recovery_required",
                    "The protected checkout contains an unsafe tracked path.", true, exception);
            }
            var info = new FileInfo(full);
            count = checked(count + 1);
            bytes = checked(bytes + info.Length);
            if (count > workspace.ActiveCheckoutTrackedFileCount || bytes > workspace.ActiveCheckoutTrackedBytes)
                throw new DeliveryException("delivery_recovery_required", "The protected checkout fingerprint no longer matches.", true);
            composite.AppendData(Encoding.UTF8.GetBytes($"{path}\0{info.Length}\0"));
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            composite.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return (Convert.ToHexString(composite.GetHashAndReset()).ToLowerInvariant(), count, bytes);
    }

    private async Task VerifyApprovedPushDestinationAsync(string git, string root, string approvedOwner,
        string approvedRepository, bool mutationAlreadyOccurred, CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await Require(git, root, ["remote", "get-url", "--push", "--all", "origin"],
                "delivery_push_destination_mismatch", cancellationToken);
        }
        catch (DeliveryException exception)
        {
            throw new DeliveryException("delivery_push_destination_mismatch",
                "The effective origin push destination could not be verified.", mutationAlreadyOccurred, exception);
        }
        var urls = Lines(output);
        if (urls.Length != 1)
            throw new DeliveryException("delivery_push_destination_mismatch",
                "The effective origin push destination is ambiguous or unavailable.", mutationAlreadyOccurred);
        (string Owner, string Repository) destination;
        try { destination = ParseGitHubRemote(urls[0]); }
        catch (DeliveryException exception)
        {
            throw new DeliveryException("delivery_push_destination_mismatch",
                "The effective origin push destination is invalid.", mutationAlreadyOccurred, exception);
        }
        if (!string.Equals(destination.Owner, approvedOwner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(destination.Repository, approvedRepository, StringComparison.OrdinalIgnoreCase))
            throw new DeliveryException("delivery_push_destination_mismatch",
                "The effective origin push destination does not match the approved GitHub repository.", mutationAlreadyOccurred);
    }

    private async Task<Dictionary<string, string?>> ReadExpectedTreeObjectsAsync(string git, string root,
        ImplementationResult result, CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, string?>(RepositoryPathRules.Comparer);
        foreach (var file in result.ChangedFiles)
        {
            if (file.Action == ImplementationOperationAction.Delete) { expected[file.Path] = null; continue; }
            var hash = (await Require(git, root, ["hash-object", "--no-filters", "--", file.Path],
                "delivery_scope_mismatch", cancellationToken)).Trim();
            if (!DeliveryValidator.Sha(hash))
                throw new DeliveryException("delivery_scope_mismatch", "A generated file object identity is invalid.");
            expected[file.Path] = hash;
        }
        return expected;
    }

    private async Task VerifyCommitTreeAsync(string git, string root,
        IReadOnlyDictionary<string, string?> expected, CancellationToken cancellationToken)
    {
        foreach (var pair in expected)
        {
            var result = await process.RunAsync(git, root, ["rev-parse", $"HEAD:{pair.Key}"], cancellationToken: cancellationToken);
            if (pair.Value is null)
            {
                if (result.ExitCode == 0)
                    throw new DeliveryException("delivery_recovery_required", "The created commit tree retained an approved deletion.", true);
                continue;
            }
            if (result.ExitCode != 0 || !string.Equals(result.StandardOutput.Trim(), pair.Value, StringComparison.Ordinal))
                throw new DeliveryException("delivery_recovery_required", "The created commit tree does not match the approved result.", true);
        }
    }

    private void VerifyWorkspace(string root, ImplementationPlan plan, ImplementationResult result)
    {
        var status = RequireSync(gitResolver.Resolve(null), root, ["status", "--porcelain=v1", "--untracked-files=all"]);
        var actual = ParsePorcelain(status);
        VerifyExpected(actual, plan, result);
        var ignored = RequireSync(gitResolver.Resolve(null), root, ["ls-files", "--others", "--ignored", "--exclude-standard"]);
        if (ignored.Length != 0) throw new DeliveryException("delivery_scope_mismatch", "The delivery worktree contains ignored files.");
        foreach (var path in result.ChangedFiles.Select(item => item.Path))
        {
            var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new DeliveryException("delivery_scope_mismatch", "A delivery path escaped its worktree.");
            for (var cursor = new FileInfo(full).Directory; cursor is not null && cursor.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase); cursor = cursor.Parent)
                if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new DeliveryException("delivery_scope_mismatch", "A delivery path uses a reparse point.");
        }
        foreach (var file in result.ChangedFiles)
        {
            var full = Path.GetFullPath(file.Path.Replace('/', Path.DirectorySeparatorChar), root);
            if (file.Action == ImplementationOperationAction.Delete)
            {
                if (File.Exists(full) || Directory.Exists(full))
                    throw new DeliveryException("delivery_scope_mismatch", "A deleted delivery path is still present in the worktree.");
                continue;
            }
            if (!File.Exists(full) || new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new DeliveryException("delivery_scope_mismatch", "A generated delivery file is unavailable or unsafe.");
            var bytes = File.ReadAllBytes(full);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, file.NewContentSha256, StringComparison.Ordinal) || bytes.LongLength != file.NewBytes)
                throw new DeliveryException("delivery_scope_mismatch", "A generated delivery file does not match the approved result.");
            string content;
            try { content = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                throw new DeliveryException("delivery_scope_mismatch", "A generated delivery file is not supported text.");
            }
            var lines = content.Length == 0 ? 0 : content.Count(character => character == '\n') + 1;
            if (lines != file.NewLines)
                throw new DeliveryException("delivery_scope_mismatch", "A generated delivery file does not match the approved result.");
        }
    }

    private string ResolveWorkspace(string token)
    {
        if (!Regex.IsMatch(token, "^[A-Za-z0-9_-]{8,160}$", RegexOptions.CultureInvariant))
            throw new DeliveryException("delivery_workspace_unavailable", "The approved implementation workspace is unavailable.");
        var parent = ExactDirectory(options.WorktreeRoot);
        var root = Path.GetFullPath(token, parent);
        if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root) ||
            new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new DeliveryException("delivery_workspace_unavailable", "The approved implementation workspace is unavailable.");
        return root;
    }

    private string CreateEmptyHooksDirectory()
    {
        var ownedRoot = ExactDirectory(options.WorktreeRoot);
        var parent = Path.GetFullPath(options.HooksDirectory);
        if (!parent.StartsWith(ownedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DeliveryException("delivery_workspace_unavailable", "The delivery hooks boundary is invalid.");
        try { new SafeDirectoryAncestry().EnsureCreated(parent); }
        catch (ImplementationException exception)
        {
            throw new DeliveryException("delivery_workspace_unavailable", "The delivery hooks boundary is unsafe.", false, exception);
        }
        var hooks = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        try { new SafeDirectoryAncestry().EnsureCreated(hooks); }
        catch (ImplementationException exception)
        {
            throw new DeliveryException("delivery_workspace_unavailable", "The delivery hooks boundary is unsafe.", false, exception);
        }
        return hooks;
    }

    private static void TryDeleteEmptyHooksDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch { }
    }

    private static (string Owner, string Repository) ParseGitHubRemote(string value)
    {
        if (value.Contains('@') && value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('?') || value.Contains('#')) throw new DeliveryException("delivery_remote_invalid", "The origin URL is unsafe.");
        var match = HttpsRemote().Match(value);
        if (!match.Success) match = ScpRemote().Match(value);
        if (!match.Success) match = SshRemote().Match(value);
        if (!match.Success) throw new DeliveryException("delivery_remote_invalid", "Origin must be an uncredentialed GitHub.com repository URL.");
        var owner = match.Groups[1].Value; var repository = match.Groups[2].Value;
        if (!DeliveryValidator.SafeName(owner, 100) || !DeliveryValidator.SafeName(repository, 100))
            throw new DeliveryException("delivery_remote_invalid", "The GitHub repository identity is invalid.");
        return (owner, repository);
    }

    private async Task<string> Require(string executable, string root, IReadOnlyList<string> arguments,
        string category, CancellationToken cancellationToken, bool mutation = false)
    {
        var result = await process.RunAsync(executable, root, arguments, cancellationToken: cancellationToken);
        if (result.ExitCode != 0) throw new DeliveryException(category,
            mutation ? "A delivery Git operation failed after mutation may have started." : "A delivery Git preflight check failed.", mutation);
        return result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private string RequireSync(string executable, string root, IReadOnlyList<string> arguments) =>
        Require(executable, root, arguments, "delivery_scope_mismatch", CancellationToken.None).GetAwaiter().GetResult();
    private static string ExactDirectory(string value)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(full))
            throw new DeliveryException("delivery_workspace_unavailable", "A required repository directory is unavailable.");
        try { new SafeDirectoryAncestry().EnsureExisting(full); }
        catch (ImplementationException exception)
        {
            throw new DeliveryException("delivery_workspace_unavailable", "A required repository directory is unsafe.", false, exception);
        }
        return full;
    }
    private static string[] Lines(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? ParseLsRemote(string value) { var lines = Lines(value); return lines.Length == 0 ? null : lines.Length == 1 && DeliveryValidator.Sha(lines[0].Split('\t')[0]) ? lines[0].Split('\t')[0] : throw new DeliveryException("delivery_remote_conflict", "The remote branch lookup was ambiguous."); }
    private static Dictionary<string, char> ParseNameStatus(string value) => RawLines(value).ToDictionary(
        line => line[(line.IndexOf('\t') + 1)..].Replace('\\', '/'), line => line[0], RepositoryPathRules.Comparer);
    private static Dictionary<string, char> ParsePorcelain(string value) => RawLines(value).ToDictionary(
        line => line[3..].Replace('\\', '/'), line => line[..2].Contains('D') ? 'D' :
            line[..2].Contains('A') || line[..2] == "??" ? 'A' : 'M', RepositoryPathRules.Comparer);
    private static string[] RawLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static void VerifyExpected(IReadOnlyDictionary<string, char> actual, ImplementationPlan plan, ImplementationResult result)
    {
        var expected = result.ChangedFiles.ToDictionary(item => RepositoryPathRules.Normalize(item.Path), item => item.Action switch { ImplementationOperationAction.Create => 'A', ImplementationOperationAction.Delete => 'D', _ => 'M' }, RepositoryPathRules.Comparer);
        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var action) || action != pair.Value) ||
            expected.Keys.Any(path => !plan.AffectedFiles.Any(file => RepositoryPathRules.Comparer.Equals(file.Path, path))))
            throw new DeliveryException("delivery_scope_mismatch", "The delivery worktree does not exactly match the approved changed-file scope.");
    }

    [GeneratedRegex("^https://github\\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\\.git)?$")]
    private static partial Regex HttpsRemote();
    [GeneratedRegex("^git@github\\.com:([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\\.git)?$")]
    private static partial Regex ScpRemote();
    [GeneratedRegex("^ssh://git@github\\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\\.git)?$")]
    private static partial Regex SshRemote();
}

public sealed class GitHubCliClient(IDeliveryProcessRunner process) : IGitHubCliClient
{
    public async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.GetTempPath();
        var result = await process.RunAsync("gh", root, ["auth", "status", "--hostname", "github.com"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0) throw new DeliveryException("delivery_authentication_unavailable", "GitHub CLI authentication for github.com is unavailable.");
    }

    public async Task<GitHubPullRequestResult> CreatePullRequestAsync(DeliveryProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var repo = $"{proposal.GitHubRepositoryOwner}/{proposal.GitHubRepositoryName}";
        var title = proposal.PullRequestTitle;
        var body = DeliveryPullRequestText.NormalizeLineEndings(proposal.PullRequestBody);
        var created = await process.RunAsync("gh", Path.GetTempPath(),
            ["pr", "create", "--repo", repo, "--base", proposal.TargetBaseBranch, "--head", proposal.DeliveryBranch,
                "--title", title, "--body-file", "-"], body, cancellationToken);
        if (created.ExitCode != 0) throw new DeliveryException("delivery_recovery_required", "Pull-request creation may have failed after dispatch. No automatic retry is available.", true);
        var url = created.StandardOutput.Trim();
        return await ViewAsync(proposal, url, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubPullRequestResult>> FindPullRequestsAsync(DeliveryProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var repo = $"{proposal.GitHubRepositoryOwner}/{proposal.GitHubRepositoryName}";
        var result = await process.RunAsync("gh", Path.GetTempPath(), ["pr", "list", "--repo", repo,
            "--head", proposal.DeliveryBranch, "--base", proposal.TargetBaseBranch, "--state", "open", "--limit", "2",
            "--json", "number,url,state,headRefName,headRefOid,baseRefName,title,body,isDraft,mergedAt,commits,files"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0) throw new DeliveryException("delivery_recovery_required", "Pull-request reconciliation failed safely.", true);
        return ParseResults(result.StandardOutput);
    }

    private async Task<GitHubPullRequestResult> ViewAsync(DeliveryProposal proposal, string url,
        CancellationToken cancellationToken)
    {
        var repo = $"{proposal.GitHubRepositoryOwner}/{proposal.GitHubRepositoryName}";
        var result = await process.RunAsync("gh", Path.GetTempPath(), ["pr", "view", url, "--repo", repo,
            "--json", "number,url,state,headRefName,headRefOid,baseRefName,title,body,mergedAt,commits,files"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0) throw new DeliveryException("delivery_recovery_required", "The created pull request could not be verified.", true);
        return ParseResults("[" + result.StandardOutput + "]").Single();
    }

    private static IReadOnlyList<GitHubPullRequestResult> ParseResults(string json)
    {
        if (json.Length > 32_768) throw new DeliveryException("delivery_recovery_required", "GitHub CLI returned oversized output.", true);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => new GitHubPullRequestResult(
            item.GetProperty("number").GetInt32(), item.GetProperty("url").GetString() ?? string.Empty,
            item.GetProperty("state").GetString() ?? string.Empty, item.GetProperty("headRefName").GetString() ?? string.Empty,
            item.GetProperty("baseRefName").GetString() ?? string.Empty, item.GetProperty("title").GetString() ?? string.Empty,
            item.GetProperty("body").GetString() ?? string.Empty, item.TryGetProperty("mergedAt", out var merged) &&
                merged.ValueKind != JsonValueKind.Null, item.GetProperty("headRefOid").GetString() ?? string.Empty,
            item.GetProperty("commits").GetArrayLength(), item.GetProperty("files").EnumerateArray()
                .Select(file => file.GetProperty("path").GetString() ?? string.Empty).ToArray())).ToArray();
    }
}
