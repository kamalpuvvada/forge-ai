using System.Diagnostics;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitProcessEnvironmentCollection
{
    public const string Name = "Git process environment";
}

[Collection(GitProcessEnvironmentCollection.Name)]
public sealed class GitProcessRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"forge-git-runner-{Guid.NewGuid():N}");
    private readonly string safetyRoot = Path.Combine(Path.GetTempPath(), $"forge-git-safety-{Guid.NewGuid():N}");

    [Fact]
    public async Task Timeout_and_mutating_cancellation_kill_the_git_process_tree_and_require_recovery()
    {
        InitializeRepository();
        var markerPath = Path.Combine(Path.GetTempPath(), $"forge-delayed-child-{Guid.NewGuid():N}.txt");
        var marker = markerPath.Replace('\\', '/');
        try
        {
            File.WriteAllText(Path.Combine(root, "hang.sh"),
                $"#!/bin/sh\n(sleep 3; echo child > '{marker}') &\nsleep 30\n");
            Git("config", "alias.forge-hang", "!sh ./hang.sh");
            var runner = Runner(timeoutSeconds: 1);

            var timeout = await Assert.ThrowsAsync<ImplementationException>(() => runner.RunAsync(
                root, ["forge-hang"], commandKind: GitCommandKind.Mutating));
            Assert.Equal("git_timeout", timeout.Category);
            Assert.True(timeout.RecoveryRequired);
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.False(File.Exists(markerPath));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var cancelled = await Assert.ThrowsAsync<ImplementationException>(() => runner.RunAsync(
                root, ["forge-hang"], cancellationToken: cancellation.Token, commandKind: GitCommandKind.Mutating));
            Assert.Equal("git_cancelled", cancelled.Category);
            Assert.True(cancelled.RecoveryRequired);
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task Read_only_caller_cancellation_terminates_git_and_preserves_cancellation_semantics()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(root, "hang.sh"), "#!/bin/sh\nsleep 30\n");
        Git("config", "alias.forge-hang", "!sh ./hang.sh");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(30).RunAsync(
            root, ["forge-hang"], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Stdout_and_stderr_truncation_are_reported_explicitly()
    {
        InitializeRepository();
        File.WriteAllText(Path.Combine(root, new string('a', 80) + ".txt"), "untracked");
        var stdout = await Runner(30, maximumError: 8).RunAsync(
            root, ["status", "--short"], maximumOutputCharacters: 5);
        var stderr = await Runner(30, maximumError: 8).RunAsync(root, ["show", "--definitely-invalid-option"]);

        Assert.Equal(0, stdout.ExitCode);
        Assert.True(stdout.OutputTruncated);
        Assert.NotEqual(0, stderr.ExitCode);
        Assert.True(stderr.ErrorTruncated);
    }

    [Fact]
    public async Task Sanitized_environment_disables_inherited_git_redirection_fsmonitor_and_hooks()
    {
        InitializeRepository();
        var marker = Path.Combine(root, "execution-marker.txt").Replace('\\', '/');
        var hookDirectory = Path.Combine(root, "unsafe-hooks");
        Directory.CreateDirectory(hookDirectory);
        File.WriteAllText(Path.Combine(hookDirectory, "post-checkout"), $"#!/bin/sh\necho hook > '{marker}'\n");
        File.WriteAllText(Path.Combine(root, "fsmonitor.sh"), $"#!/bin/sh\necho monitor > '{marker}'\nexit 0\n");
        File.WriteAllText(Path.Combine(root, ".gitattributes"), "tracked.txt filter=unsafe\n");
        Git("add", "--", ".gitattributes");
        Git("commit", "-m", "attribute fixture");
        Git("config", "core.hooksPath", hookDirectory.Replace('\\', '/'));
        Git("config", "core.fsmonitor", "sh ./fsmonitor.sh");
        var previousGitDir = Environment.GetEnvironmentVariable("GIT_DIR");
        var previousConfigCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        var previousGlobalConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var previousSystemConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM");
        var globalConfig = Path.Combine(root, "unsafe-global.gitconfig");
        File.WriteAllText(globalConfig, $"[core]\n\thooksPath = {hookDirectory.Replace('\\', '/')}\n[filter \"unsafe\"]\n\tclean = sh ./fsmonitor.sh\n");
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(root, "not-a-repository"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.hooksPath");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", hookDirectory);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", globalConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", globalConfig);

            var runner = Runner();
            var status = await runner.RunAsync(root, ["status", "--short"]);
            var checkout = await runner.RunAsync(root, ["checkout", "--", "tracked.txt"],
                commandKind: GitCommandKind.Mutating);
            var branch = await runner.RunAsync(root, ["checkout", "-b", "sanitized-envelope-test"],
                commandKind: GitCommandKind.Mutating);

            Assert.Equal(0, status.ExitCode);
            Assert.Equal(0, checkout.ExitCode);
            Assert.Equal(0, branch.ExitCode);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DIR", previousGitDir);
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", previousConfigCount);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previousGlobalConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", previousSystemConfig);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", null);
        }
    }

    private GitProcessRunner Runner(int timeoutSeconds = 30, int maximumError = 8_000) => new(new GitProcessOptions
    {
        OwnedRoot = safetyRoot,
        HooksDirectory = Path.Combine(safetyRoot, ".empty-hooks"),
        SafeHomeDirectory = Path.Combine(safetyRoot, ".git-home"),
        TimeoutSeconds = timeoutSeconds,
        MaximumErrorCharacters = maximumError
    });

    private void InitializeRepository()
    {
        Directory.CreateDirectory(root);
        Git("init");
        Git("config", "user.name", "Forge Tests");
        Git("config", "user.email", "forge-tests@example.invalid");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "tracked\n");
        Git("add", "--", "tracked.txt");
        Git("commit", "-m", "initial");
    }

    private string Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        foreach (var directory in new[] { root, safetyRoot })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
