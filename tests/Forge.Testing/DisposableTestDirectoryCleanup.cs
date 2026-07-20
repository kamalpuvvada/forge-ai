namespace Forge.Testing;

public sealed class LiveSmokeCleanupException(string root, Exception innerException)
    : Exception($"The OpenAI smoke body completed successfully, but cleanup failed. Disposable evidence was preserved at: {root}", innerException);

public static class LiveSmokeCleanupCoordinator
{
    public static async Task ExecuteAsync(
        string root,
        Func<Task> body,
        Action cleanup,
        Action<string>? reportPreservedEvidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(cleanup);

        try
        {
            await body();
        }
        catch
        {
            try
            {
                reportPreservedEvidence?.Invoke(
                    $"OpenAI smoke evidence was preserved at the disposable test root: {root}");
            }
            catch
            {
                // Diagnostic output must never replace the original smoke-body exception.
            }
            throw;
        }

        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            throw new LiveSmokeCleanupException(root, exception);
        }
    }
}

public static class DisposableTestDirectoryCleanup
{
    public const int DefaultMaximumAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static void Delete(
        string root,
        string testOwnedParent,
        Action<int>? beforeAttempt = null,
        Action<TimeSpan>? wait = null,
        int maximumAttempts = DefaultMaximumAttempts)
    {
        var containedRoot = ValidateContainedRoot(root, testOwnedParent);
        if (maximumAttempts is < 1 or > DefaultMaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                beforeAttempt?.Invoke(attempt);
                DeleteOnce(containedRoot);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) && attempt < maximumAttempts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                (wait ?? (delay => Thread.Sleep(delay)))(RetryDelay);
            }
        }
    }

    private static string ValidateContainedRoot(string root, string testOwnedParent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(testOwnedParent);
        var parent = Path.GetFullPath(testOwnedParent);
        var candidate = Path.GetFullPath(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parentPrefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(parentPrefix, comparison))
            throw new InvalidOperationException("The disposable directory is outside the test-owned temporary parent.");
        return candidate;
    }

    private static void DeleteOnce(string root)
    {
        if (!Directory.Exists(root) && !File.Exists(root)) return;

        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureNotReparsePoint(directory);
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureContained(entry, root);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Disposable cleanup refuses reparse points.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    files.Add(entry);
            }
        }

        foreach (var file in files)
        {
            EnsureNotReparsePoint(file);
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }
        for (var index = directories.Count - 1; index >= 0; index--)
        {
            var directory = directories[index];
            EnsureNotReparsePoint(directory);
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Disposable cleanup refuses reparse points.");
    }

    private static void EnsureContained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, comparison))
            throw new InvalidOperationException("Disposable cleanup encountered a path outside its root.");
    }
}
