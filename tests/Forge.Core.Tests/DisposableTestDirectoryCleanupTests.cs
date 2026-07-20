using Forge.Testing;

namespace Forge.Core.Tests;

public sealed class DisposableTestDirectoryCleanupTests : IDisposable
{
    private readonly string parent = Path.Combine(
        Path.GetTempPath(), $"forge-cleanup-tests-{Guid.NewGuid():N}");

    public DisposableTestDirectoryCleanupTests() => Directory.CreateDirectory(parent);

    [Fact]
    public void Deletes_ordinary_and_nested_directories()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "repository", ".git", "objects"));
        File.WriteAllText(Path.Combine(root, "repository", ".git", "objects", "entry"), "git data");

        DisposableTestDirectoryCleanup.Delete(root, parent);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Clears_read_only_attributes_from_git_style_files_and_directories()
    {
        var root = CreateRoot();
        var gitDirectory = Directory.CreateDirectory(Path.Combine(root, ".git", "refs")).FullName;
        var file = Path.Combine(gitDirectory, "main");
        File.WriteAllText(file, "sha");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        File.SetAttributes(gitDirectory, File.GetAttributes(gitDirectory) | FileAttributes.ReadOnly);

        DisposableTestDirectoryCleanup.Delete(root, parent);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Already_missing_root_succeeds_silently()
    {
        var root = Path.Combine(parent, $"missing-{Guid.NewGuid():N}");

        DisposableTestDirectoryCleanup.Delete(root, parent);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Retries_a_transient_first_attempt_io_failure_within_the_bound()
    {
        var root = CreateRoot();
        var attempts = 0;

        DisposableTestDirectoryCleanup.Delete(root, parent,
            beforeAttempt: _ =>
            {
                attempts++;
                if (attempts == 1) throw new IOException("transient test failure");
            },
            wait: _ => { });

        Assert.Equal(2, attempts);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Rejects_a_root_outside_the_test_owned_parent()
    {
        var ownedParent = Directory.CreateDirectory(Path.Combine(parent, "owned")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(parent, "outside")).FullName;
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                DisposableTestDirectoryCleanup.Delete(outside, ownedParent));

            Assert.Contains("outside the test-owned temporary parent", exception.Message);
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            DisposableTestDirectoryCleanup.Delete(outside, parent);
            Directory.Delete(ownedParent, recursive: false);
        }
    }

    [Fact]
    public void Refuses_a_reparse_point_without_following_or_deleting_its_target_when_supported()
    {
        var root = CreateRoot();
        var target = Directory.CreateDirectory(Path.Combine(parent, $"target-{Guid.NewGuid():N}")).FullName;
        var link = Path.Combine(root, "linked-directory");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception linkException) when (
                linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var rejection = Assert.Throws<InvalidOperationException>(() =>
                DisposableTestDirectoryCleanup.Delete(root, parent));

            Assert.Contains("refuses reparse points", rejection.Message);
            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            if (Directory.Exists(root)) DisposableTestDirectoryCleanup.Delete(root, parent);
            if (Directory.Exists(target)) DisposableTestDirectoryCleanup.Delete(target, parent);
        }
    }

    [Fact]
    public async Task Body_exception_is_rethrown_unchanged_and_cleanup_is_not_attempted()
    {
        var root = CreateRoot();
        var original = new InvalidOperationException("provider-body-sensitive-marker");
        var cleanupCalls = 0;
        var messages = new List<string>();
        try
        {
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LiveSmokeCleanupCoordinator.ExecuteAsync(
                    root,
                    () => Task.FromException(original),
                    () => cleanupCalls++,
                    messages.Add));

            Assert.Same(original, caught);
            Assert.Equal(0, cleanupCalls);
            Assert.True(Directory.Exists(root));
            var message = Assert.Single(messages);
            Assert.Contains(root, message);
            Assert.DoesNotContain("provider-body-sensitive-marker", message);
        }
        finally
        {
            DisposableTestDirectoryCleanup.Delete(root, parent);
        }
    }

    [Fact]
    public async Task Successful_body_and_final_cleanup_failure_throws_cleanup_specific_exception()
    {
        var root = CreateRoot();
        var attempts = 0;
        try
        {
            var exception = await Assert.ThrowsAsync<LiveSmokeCleanupException>(() =>
                LiveSmokeCleanupCoordinator.ExecuteAsync(
                    root,
                    () => Task.CompletedTask,
                    () => DisposableTestDirectoryCleanup.Delete(root, parent,
                        beforeAttempt: _ =>
                        {
                            attempts++;
                            throw new IOException("persistent test cleanup failure");
                        },
                        wait: _ => { })));

            Assert.Equal(DisposableTestDirectoryCleanup.DefaultMaximumAttempts, attempts);
            Assert.IsType<IOException>(exception.InnerException);
            Assert.Contains("OpenAI smoke body completed successfully, but cleanup failed", exception.Message);
            Assert.Contains(root, exception.Message);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            DisposableTestDirectoryCleanup.Delete(root, parent);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: false);
    }

    private string CreateRoot()
    {
        var root = Path.Combine(parent, $"root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
