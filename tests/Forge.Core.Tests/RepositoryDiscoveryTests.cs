using System.Diagnostics;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class RepositoryDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-discovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task Missing_directory_is_rejected_safely()
    {
        var exception = await Assert.ThrowsAsync<RepositoryDiscoveryException>(() => CreateService().DiscoverAsync(Path.Combine(_root, "missing")));
        Assert.Equal("missing_path", exception.Category);
    }

    [Fact]
    public async Task Non_git_directory_maps_supported_stack_and_ignores_excluded_content()
    {
        Write("src/App.tsx", "export function App() { return null }\n");
        Write("tests/AppTests.cs", "public class AppTests { }\n");
        Write("node_modules/secret.ts", "export const ignored = true\n");

        var result = await CreateService().DiscoverAsync(_root);

        Assert.False(result.Snapshot.IsGitRepository);
        Assert.Equal("unknown", result.Snapshot.WorkingTreeStatus);
        Assert.Contains("TypeScript", result.Snapshot.DetectedLanguages);
        Assert.DoesNotContain(result.Snapshot.Files, file => file.RelativePath.Contains("node_modules"));
        Assert.Contains(result.Snapshot.TestLocations, path => path.StartsWith("tests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Binary_large_and_secret_files_are_excluded()
    {
        Write("src/keep.cs", "public class Keep { }\n");
        WriteBytes("image.png", [0, 1, 2, 3]);
        Write("large.cs", new string('x', 65));
        Write(".env", "API_KEY=should-not-appear");
        Write("private-key.pem", "should-not-appear");
        var limits = new RepositoryAnalysisLimits { MaximumTextFileBytes = 64 };

        var result = await CreateService(limits).DiscoverAsync(_root);

        Assert.Single(result.TextFiles);
        Assert.Equal("src/keep.cs", result.TextFiles[0].Metadata.RelativePath);
        Assert.DoesNotContain("should-not-appear", string.Join(' ', result.TextFiles.Select(file => file.Content)));
        Assert.Contains(result.Snapshot.Warnings, warning => warning.Contains("larger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Valid_git_repository_reports_head_branch_and_dirty_state()
    {
        Write("README.md", "# Example\n");
        RunGit("init");
        RunGit("config", "user.email", "forge-tests@example.invalid");
        RunGit("config", "user.name", "Forge Tests");
        RunGit("add", "README.md");
        RunGit("commit", "-m", "initial");

        var clean = await CreateService().DiscoverAsync(_root);
        Assert.True(clean.Snapshot.IsGitRepository);
        Assert.Equal("clean", clean.Snapshot.WorkingTreeStatus);
        Assert.False(string.IsNullOrWhiteSpace(clean.Snapshot.Branch));
        Assert.NotNull(clean.Snapshot.FullHeadSha);
        Assert.Equal(8, clean.Snapshot.ShortHeadSha!.Length);

        Write("README.md", "# Changed\n");
        var dirty = await CreateService().DiscoverAsync(_root);
        Assert.Equal("dirty", dirty.Snapshot.WorkingTreeStatus);
        Assert.NotEqual(clean.Snapshot.Fingerprint, dirty.Snapshot.Fingerprint);
    }

    [Fact]
    public async Task Fingerprint_is_deterministic_for_unchanged_non_git_content()
    {
        Write("src/One.cs", "public class One { }\n");
        Write("src/Two.cs", "public class Two { }\n");
        var service = CreateService();

        var first = await service.DiscoverAsync(_root);
        var second = await service.DiscoverAsync(_root);

        Assert.Equal(first.Snapshot.Fingerprint, second.Snapshot.Fingerprint);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_inspection()
    {
        Directory.CreateDirectory(_root);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().DiscoverAsync(_root, source.Token));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    public void Containment_rejects_paths_outside_root(string candidate)
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<RepositoryDiscoveryException>(() => RepositoryDiscoveryService.ResolveContainedPath(Path.GetFullPath(_root), candidate));
    }

    private RepositoryDiscoveryService CreateService(RepositoryAnalysisLimits? limits = null) =>
        new(limits ?? new RepositoryAnalysisLimits(), TimeProvider.System);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteBytes(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void RunGit(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch (FileNotFoundException) { }
        }
        Directory.Delete(_root, true);
    }
}
