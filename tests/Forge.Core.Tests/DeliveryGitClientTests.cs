using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class DeliveryGitClientTests
{
    [Fact]
    public async Task Preflight_rejects_credentialed_origin_and_remote_main_drift_without_mutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-delivery-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow; var baseSha = new string('a', 40);
            var snapshot = PlanningWorkflowTests.Snapshot(now) with
            { NormalizedRoot = root, IsGitRepository = true, Branch = "main", FullHeadSha = baseSha };
            var plan = PlanningWorkflowTests.Plan(snapshot, [PlanningWorkflowTests.Evidence()]);
            var workspace = new ImplementationWorkspace("deliverytest123", "forge-implementation-test", baseSha,
                ImplementationWorkspacePhase.Completed, now, now, true);
            var result = new ImplementationResult(ImplementationSource.DeterministicFake, null, baseSha,
                workspace.Branch, "Change.", [], [], 0, 0, false, now, ActiveCheckoutVerified: true);
            var credentialedUrl = "https://" + "user" + ":" + "synthetic" + "@github.com/acme/widget.git";
            var credentialed = new GitHubDeliveryClient(
                new PreflightRunner(credentialedUrl, baseSha, credentialedUrl),
                new FixedGitResolver(), new DeliveryProcessOptions { WorktreeRoot = root, HooksDirectory = root });
            var unsafeRemote = await Assert.ThrowsAsync<DeliveryException>(() => credentialed.PreflightAsync(
                root, snapshot, plan, workspace, result, "forge-delivery-a1b2c3d4-r1"));
            Assert.Equal("delivery_remote_invalid", unsafeRemote.Category);

            var drifted = new GitHubDeliveryClient(new PreflightRunner("https://github.com/acme/widget.git", new string('b', 40), "git@github.com:acme/widget.git"),
                new FixedGitResolver(), new DeliveryProcessOptions { WorktreeRoot = root, HooksDirectory = root });
            var remoteConflict = await Assert.ThrowsAsync<DeliveryException>(() => drifted.PreflightAsync(
                root, snapshot, plan, workspace, result, "forge-delivery-a1b2c3d4-r1"));
            Assert.Equal("delivery_remote_conflict", remoteConflict.Category);

            var existingBranch = new GitHubDeliveryClient(new PreflightRunner("https://github.com/acme/widget.git", baseSha, "git@github.com:acme/widget.git"),
                new FixedGitResolver(), new DeliveryProcessOptions { WorktreeRoot = root, HooksDirectory = root });
            var branchConflict = await Assert.ThrowsAsync<DeliveryException>(() => existingBranch.PreflightAsync(
                root, snapshot, plan, workspace, result, "forge-delivery-a1b2c3d4-r1"));
            Assert.Equal("delivery_branch_conflict", branchConflict.Category);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://github.com/acme/widget.git")]
    [InlineData("git@github.com:acme/widget.git")]
    [InlineData("ssh://git@github.com/acme/widget.git")]
    public async Task Matching_effective_push_destination_is_accepted_regardless_of_supported_scheme(string pushUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-delivery-pushurl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (snapshot, plan, workspace, result) = PreflightContext(root);
            var runner = new PreflightRunner("https://github.com/acme/widget.git", workspace.BaseCommitSha, pushUrl);
            var client = new GitHubDeliveryClient(runner, new FixedGitResolver(),
                new DeliveryProcessOptions { WorktreeRoot = root, HooksDirectory = root });

            var exception = await Assert.ThrowsAsync<DeliveryException>(() => client.PreflightAsync(root, snapshot,
                plan, workspace, result, "forge-delivery-a1b2c3d4-r1"));

            Assert.Equal("delivery_branch_conflict", exception.Category);
            Assert.Contains(runner.Calls, call => call.SequenceEqual(["remote", "get-url", "--push", "--all", "origin"]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Mismatched_multiple_credentialed_and_non_github_push_destinations_are_rejected_before_mutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-delivery-pushurl-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (snapshot, plan, workspace, result) = PreflightContext(root);
            var credentialed = "https://" + "user" + ":" + "synthetic" + "@github.com/acme/widget.git";
            var candidates = new[]
            {
                "https://github.com/acme/other.git",
                "https://github.com/acme/widget.git\ngit@github.com:acme/widget.git",
                credentialed,
                "https://example.invalid/acme/widget.git"
            };
            foreach (var pushUrl in candidates)
            {
                var runner = new PreflightRunner("https://github.com/acme/widget.git", workspace.BaseCommitSha, pushUrl);
                var client = new GitHubDeliveryClient(runner, new FixedGitResolver(),
                    new DeliveryProcessOptions { WorktreeRoot = root, HooksDirectory = root });
                var exception = await Assert.ThrowsAsync<DeliveryException>(() => client.PreflightAsync(root, snapshot,
                    plan, workspace, result, "forge-delivery-a1b2c3d4-r1"));
                Assert.Equal("delivery_push_destination_mismatch", exception.Category);
                Assert.DoesNotContain(runner.Calls, call => call.Contains("push"));
                Assert.DoesNotContain(pushUrl, exception.Message, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Exact_approved_file_is_committed_unsigned_with_hooks_disabled_and_pushed_without_force()
    {
        using var fixture = new RepositoryFixture();
        var context = fixture.Context();

        var commit = await fixture.Client.CreateCommitAsync(context);
        var push = await fixture.Client.PushAsync(context, commit.CommitSha);

        Assert.Equal(fixture.BaseSha, fixture.Git(fixture.Worktree, "rev-parse", "HEAD^"));
        Assert.Equal(context.Proposal.CommitMessage, fixture.Git(fixture.Worktree, "log", "-1", "--format=%B"));
        Assert.Equal("M\tsrc/App.cs", fixture.Git(fixture.Worktree, "diff-tree", "--no-commit-id", "--name-status", "--no-renames", "-r", "HEAD"));
        Assert.DoesNotContain("gpgsig", fixture.Git(fixture.Worktree, "cat-file", "commit", commit.CommitSha), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.HookMarker));
        Assert.Equal(commit.CommitSha, push.RemoteBranchSha);
        Assert.Equal(commit.CommitSha, fixture.Git(fixture.Active, "ls-remote", "--heads", "origin", $"refs/heads/{context.Proposal.DeliveryBranch}").Split('\t')[0]);
        Assert.Equal("main", fixture.Git(fixture.Active, "branch", "--show-current"));
        Assert.Equal(fixture.BaseSha, fixture.Git(fixture.Active, "rev-parse", "HEAD"));
        Assert.Equal(string.Empty, fixture.Git(fixture.Active, "status", "--porcelain=v1", "--untracked-files=all"));
        var pushCall = fixture.Runner.Calls.Single(call => call.Arguments.Contains("push"));
        Assert.Equal("--no-verify", pushCall.Arguments[3]);
        Assert.Equal("origin", pushCall.Arguments[4]);
        Assert.Equal($"{commit.CommitSha}:refs/heads/{context.Proposal.DeliveryBranch}", pushCall.Arguments[5]);
        Assert.DoesNotContain(pushCall.Arguments, value => value.StartsWith('+') || value.Contains("force", StringComparison.OrdinalIgnoreCase));
        var addCall = fixture.Runner.Calls.Single(call => call.Arguments.FirstOrDefault() == "add");
        Assert.Equal(["add", "-A", "--", "src/App.cs"], addCall.Arguments);
    }

    [Fact]
    public async Task Unexpected_worktree_path_is_rejected_before_a_delivery_branch_is_created()
    {
        using var fixture = new RepositoryFixture();
        File.WriteAllText(Path.Combine(fixture.Worktree, "unexpected.txt"), "unexpected\n");

        var exception = await Assert.ThrowsAsync<DeliveryException>(() =>
            fixture.Client.CreateCommitAsync(fixture.Context()));

        Assert.Equal("delivery_scope_mismatch", exception.Category);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Arguments.SequenceEqual(["switch", "-c", fixture.Context().Proposal.DeliveryBranch]));
        Assert.Equal("main", fixture.Git(fixture.Active, "branch", "--show-current"));
        Assert.Equal(fixture.BaseSha, fixture.Git(fixture.Active, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Push_destination_change_after_commit_is_rejected_without_invoking_push()
    {
        using var fixture = new RepositoryFixture(); var context = fixture.Context();
        var commit = await fixture.Client.CreateCommitAsync(context);
        fixture.Runner.EffectivePushUrl = "git@github.com:acme/other.git";

        var exception = await Assert.ThrowsAsync<DeliveryException>(() => fixture.Client.PushAsync(context, commit.CommitSha));

        Assert.Equal("delivery_push_destination_mismatch", exception.Category);
        Assert.True(exception.RecoveryRequired);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Arguments.Contains("push"));
        Assert.DoesNotContain("github.com", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static (RepositorySnapshot Snapshot, ImplementationPlan Plan, ImplementationWorkspace Workspace,
        ImplementationResult Result) PreflightContext(string root)
    {
        var now = DateTimeOffset.UtcNow; var baseSha = new string('a', 40);
        var snapshot = PlanningWorkflowTests.Snapshot(now) with
        { NormalizedRoot = root, IsGitRepository = true, Branch = "main", FullHeadSha = baseSha };
        var plan = PlanningWorkflowTests.Plan(snapshot, [PlanningWorkflowTests.Evidence()]);
        var workspace = new ImplementationWorkspace("deliverytest123", "forge-implementation-test", baseSha,
            ImplementationWorkspacePhase.Completed, now, now, true);
        var result = new ImplementationResult(ImplementationSource.DeterministicFake, null, baseSha,
            workspace.Branch, "Change.", [], [], 0, 0, false, now, ActiveCheckoutVerified: true);
        return (snapshot, plan, workspace, result);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "forge-delivery-git-" + Guid.NewGuid().ToString("N"));
        internal string Active { get; }
        internal string WorktreeRoot { get; }
        internal string Worktree { get; }
        internal string HookMarker { get; }
        internal string BaseSha { get; }
        internal RecordingRunner Runner { get; }
        internal GitHubDeliveryClient Client { get; }

        internal RepositoryFixture()
        {
            Active = Path.Combine(root, "active"); WorktreeRoot = Path.Combine(root, "worktrees");
            Worktree = Path.Combine(WorktreeRoot, "deliverytest123"); HookMarker = Path.Combine(root, "hook-ran.txt");
            var bare = Path.Combine(root, "remote.git"); var hooks = Path.Combine(WorktreeRoot, ".delivery-hooks");
            Directory.CreateDirectory(Active); Directory.CreateDirectory(WorktreeRoot); Directory.CreateDirectory(hooks);
            Directory.CreateDirectory(bare);
            Git(bare, "init", "--bare"); Git(Active, "init", "-b", "main");
            Git(Active, "config", "user.name", "Forge Delivery Tests");
            Git(Active, "config", "user.email", "forge-delivery-tests@example.invalid");
            Directory.CreateDirectory(Path.Combine(Active, "src"));
            File.WriteAllText(Path.Combine(Active, "src", "App.cs"), "old\n");
            Git(Active, "add", "--", "src/App.cs"); Git(Active, "commit", "-m", "base");
            BaseSha = Git(Active, "rev-parse", "HEAD");
            Git(Active, "remote", "add", "origin", bare); Git(Active, "push", "origin", "main");
            Git(Active, "worktree", "add", "-b", "forge-implementation-test", Worktree, BaseSha);
            File.WriteAllText(Path.Combine(Worktree, "src", "App.cs"), "new\n");
            var hook = Path.Combine(Active, ".git", "hooks", "pre-commit");
            File.WriteAllText(hook, $"#!/bin/sh\nprintf ran > '{HookMarker.Replace("\\", "/")}'\nexit 1\n");
            Runner = new RecordingRunner(new DeliveryProcessRunner(new DeliveryProcessOptions()));
            Client = new GitHubDeliveryClient(Runner, new GitExecutablePathResolver(), new DeliveryProcessOptions
            {
                WorktreeRoot = WorktreeRoot, HooksDirectory = hooks, TimeoutSeconds = 20,
                MaximumOutputCharacters = 64_000
            });
        }

        internal DeliveryExecutionContext Context()
        {
            var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
            var snapshot = PlanningWorkflowTests.Snapshot(now) with
            {
                NormalizedRoot = Active, IsGitRepository = true, Branch = "main", FullHeadSha = BaseSha,
                ShortHeadSha = BaseSha[..7], WorkingTreeStatus = "clean"
            };
            var plan = PlanningWorkflowTests.Plan(snapshot, [PlanningWorkflowTests.Evidence()]) with
            {
                AffectedFiles = [new PlannedFileChange("src/App.cs", PlannedFileAction.Modify,
                    "Apply the approved change.", ["E1"], 1m)]
            };
            var workspace = new ImplementationWorkspace("deliverytest123", "forge-implementation-test", BaseSha,
                ImplementationWorkspacePhase.Completed, now, now, true,
                ActiveCheckoutContentFingerprint: ActiveFingerprint("src/App.cs", "old\n"),
                ActiveCheckoutTrackedFileCount: 1, ActiveCheckoutTrackedBytes: 4);
            var changed = new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify,
                ImplementationOutputValidator.Hash("old\n"), ImplementationOutputValidator.Hash("new\n"),
                4, 4, 2, 2, 1, 1, "@@\n-old\n+new\n", 16, 16, false);
            var result = new ImplementationResult(ImplementationSource.DeterministicFake, null, BaseSha,
                workspace.Branch, "Change App.", [], [changed], 16, 16, false, now,
                ActiveCheckoutVerified: true, WorktreeFingerprint: new string('a', 64), WorktreeFileCount: 1, WorktreeBytes: 4);
            var proposal = new DeliveryProposal(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a', 64),
                Guid.NewGuid(), new string('b', 64), Guid.NewGuid(), new string('c', 64), BaseSha, "origin", "acme",
                "widget", "main", BaseSha, "forge-delivery-a1b2c3d4-r1", "forge: deliver task a1b2c3d4 revision 1",
                "Forge AI: Change App", "body", ["src/App.cs"], new string('d', 64), now,
                DeliveryProposalStatus.Approved, now, Guid.NewGuid(), 1);
            return new DeliveryExecutionContext(Active, snapshot, plan, workspace, result, proposal);
        }

        private static string ActiveFingerprint(string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            using var composite = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            composite.AppendData(Encoding.UTF8.GetBytes($"{path}\0{bytes.Length}\0"));
            composite.AppendData(SHA256.HashData(bytes));
            return Convert.ToHexString(composite.GetHashAndReset()).ToLowerInvariant();
        }

        internal string Git(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Disposable Git fixture failed: {error}");
            return output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Worktree)) Git(Active, "worktree", "remove", "--force", Worktree); } catch { }
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class RecordingRunner(IDeliveryProcessRunner inner) : IDeliveryProcessRunner
    {
        internal sealed record Call(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments);
        internal List<Call> Calls { get; } = [];
        internal string EffectivePushUrl { get; set; } = "https://github.com/acme/widget.git";
        public Task<DeliveryProcessResult> RunAsync(string executable, string workingDirectory,
            IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(executable, workingDirectory, arguments.ToArray()));
            if (arguments.SequenceEqual(["remote", "get-url", "--push", "--all", "origin"]))
                return Task.FromResult(new DeliveryProcessResult(0, EffectivePushUrl + "\n", string.Empty));
            return inner.RunAsync(executable, workingDirectory, arguments, standardInput, cancellationToken);
        }
    }

    private sealed class FixedGitResolver : IGitExecutablePathResolver
    {
        public string Resolve(string? configuredPath) => "git";
    }

    private sealed class PreflightRunner(string remoteUrl, string remoteBase, string pushUrls) : IDeliveryProcessRunner
    {
        internal List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<DeliveryProcessResult> RunAsync(string executable, string workingDirectory,
            IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            var output = arguments switch
            {
                ["remote"] => "origin\n",
                ["remote", "get-url", "--all", "origin"] => remoteUrl + "\n",
                ["remote", "get-url", "--push", "--all", "origin"] => pushUrls + "\n",
                ["branch", "--show-current"] => "main\n",
                ["rev-parse", "HEAD"] => new string('a', 40) + "\n",
                ["status", ..] => string.Empty,
                ["ls-remote", "--heads", "origin", "refs/heads/main"] => $"{remoteBase}\trefs/heads/main\n",
                _ => string.Empty
            };
            return Task.FromResult(new DeliveryProcessResult(0, output, string.Empty));
        }
    }
}
