using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class GitProcessOptions
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string OwnedRoot { get; set; } = string.Empty;
    public string HooksDirectory { get; set; } = string.Empty;
    public string SafeHomeDirectory { get; set; } = string.Empty;
    public int MaximumOutputCharacters { get; set; } = 1_000_000;
    public int MaximumErrorCharacters { get; set; } = 8_000;
    public int TimeoutSeconds { get; set; } = 30;
}

public enum GitCommandKind
{
    ReadOnly,
    Mutating
}

public sealed record GitProcessResult(
    int ExitCode,
    string Output,
    string Error,
    bool OutputTruncated,
    bool ErrorTruncated);

public interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        int? maximumOutputCharacters = null,
        CancellationToken cancellationToken = default,
        GitCommandKind commandKind = GitCommandKind.ReadOnly);
}

public interface IGitExecutablePathResolver
{
    string Resolve(string? configuredPath);
}

public sealed class GitExecutablePathResolver : IGitExecutablePathResolver
{
    public string Resolve(string? configuredPath) => GitExecutableResolver.Resolve(configuredPath);
}

public static class GitExecutableResolver
{
    public static string Resolve(string? configuredPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "Git", "cmd", "git.exe"));
            candidates.Add(Path.Combine(programFiles, "Git", "bin", "git.exe"));
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"));

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.IsPathFullyQualified(directory)) candidates.Add(Path.Combine(directory, "git.exe"));
        }

        foreach (var candidate in candidates)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(candidate); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath) ||
                !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            EnsureNoReparsePoint(fullPath);
            return fullPath;
        }

        throw new ImplementationException("git_unavailable", "A trusted absolute Git executable could not be resolved.");
    }

    private static void EnsureNoReparsePoint(string executablePath)
    {
        var current = executablePath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ImplementationException("git_unavailable", "The configured Git executable is not a trusted regular file.");
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
}

public sealed class GitProcessRunner : IGitProcessRunner
{
    private static readonly string[] EnvelopeArguments =
    [
        "--no-pager",
        "-c", "core.fsmonitor=false",
        "-c", "submodule.recurse=false",
        "-c", "diff.external=",
        "-c", "diff.trustExitCode=false",
        "-c", "core.pager=",
        "-c", "pager.status=false",
        "-c", "maintenance.auto=false",
        "-c", "gc.auto=0",
        "-c", "credential.helper=",
        "-c", "credential.interactive=never",
        "-c", "core.askPass=",
        "-c", "protocol.file.allow=never"
    ];

    private readonly GitProcessOptions options;
    private readonly string executablePath;
    private readonly string ownedRoot;
    private readonly string hooksDirectory;
    private readonly string safeHomeDirectory;
    private readonly ISafeDirectoryAncestry ancestry;

    public GitProcessRunner(GitProcessOptions options)
        : this(options, new GitExecutablePathResolver(), new SafeDirectoryAncestry()) { }

    public GitProcessRunner(GitProcessOptions options, IGitExecutablePathResolver executableResolver)
        : this(options, executableResolver, new SafeDirectoryAncestry()) { }

    internal GitProcessRunner(GitProcessOptions options, ISafeDirectoryAncestry ancestry)
        : this(options, new GitExecutablePathResolver(), ancestry) { }

    internal GitProcessRunner(
        GitProcessOptions options,
        IGitExecutablePathResolver executableResolver,
        ISafeDirectoryAncestry ancestry)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(executableResolver);
        this.ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
        executablePath = executableResolver.Resolve(options.ExecutablePath);
        ownedRoot = NormalizeOwnedRoot(options.OwnedRoot);
        hooksDirectory = NormalizeOwnedDirectory(
            string.IsNullOrWhiteSpace(options.HooksDirectory) ? Path.Combine(ownedRoot, ".empty-hooks") : options.HooksDirectory);
        safeHomeDirectory = NormalizeOwnedDirectory(
            string.IsNullOrWhiteSpace(options.SafeHomeDirectory) ? Path.Combine(ownedRoot, ".git-home") : options.SafeHomeDirectory);
    }

    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        int? maximumOutputCharacters = null,
        CancellationToken cancellationToken = default,
        GitCommandKind commandKind = GitCommandKind.ReadOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        InitializeOperationalDirectories();

        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ConfigureSanitizedEnvironment(startInfo);
        foreach (var argument in EnvelopeArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.hooksPath={hooksDirectory}");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process? process = null;
        Task<BoundedText>? outputTask = null;
        Task<BoundedText>? errorTask = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            ValidateOperationalDirectories();
            process = Process.Start(startInfo)
                ?? throw new ImplementationException("git_unavailable", "Git could not be started safely.");
            outputTask = ReadBoundedAsync(process.StandardOutput,
                maximumOutputCharacters ?? options.MaximumOutputCharacters);
            errorTask = ReadBoundedAsync(process.StandardError, options.MaximumErrorCharacters);

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), linked.Token);
                await process.StandardInput.FlushAsync(linked.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(linked.Token);
            var output = await outputTask;
            var error = await errorTask;
            return new GitProcessResult(process.ExitCode, output.Text, error.Text, output.Truncated, error.Truncated);
        }
        catch (OperationCanceledException exception)
        {
            KillTree(process);
            CloseInput(process);
            await DrainAfterTerminationAsync(process, outputTask, errorTask);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new ImplementationException("git_timeout", "A bounded Git operation timed out safely.",
                    commandKind == GitCommandKind.Mutating, exception);
            if (commandKind == GitCommandKind.Mutating)
                throw new ImplementationException("git_cancelled", "A mutating Git operation was cancelled and requires workspace recovery.", true, exception);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ImplementationException)
        {
            KillTree(process);
            CloseInput(process);
            await DrainAfterTerminationAsync(process, outputTask, errorTask);
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            KillTree(process);
            CloseInput(process);
            await DrainAfterTerminationAsync(process, outputTask, errorTask);
            throw new ImplementationException("git_unavailable", "Git could not complete a safe repository operation.",
                commandKind == GitCommandKind.Mutating, exception);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void ConfigureSanitizedEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "WINDIR");
        CopyEnvironment(startInfo, "TEMP");
        CopyEnvironment(startInfo, "TMP");
        startInfo.Environment["HOME"] = safeHomeDirectory;
        startInfo.Environment["USERPROFILE"] = safeHomeDirectory;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = string.Empty;
        startInfo.Environment["PAGER"] = string.Empty;
        startInfo.Environment["GIT_ASKPASS"] = string.Empty;
        startInfo.Environment["SSH_ASKPASS"] = string.Empty;
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["GIT_EXTERNAL_DIFF"] = string.Empty;
        startInfo.Environment["GIT_SSH_COMMAND"] = string.Empty;
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        startInfo.Environment["GIT_PROTOCOL_FROM_USER"] = "0";
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }

    private string NormalizeOwnedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = ownedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ImplementationException("implementation_workspace_configuration", "A Git safety directory escaped the Forge-owned root.");
        return fullPath;
    }

    private static string NormalizeOwnedRoot(string configured)
    {
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "ForgeAI", $"process-{Environment.ProcessId}")
            : configured;
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void InitializeOperationalDirectories()
    {
        ancestry.EnsureCreated(ownedRoot);
        ancestry.EnsureExisting(ownedRoot);
        ancestry.EnsureCreated(hooksDirectory);
        ancestry.EnsureExisting(ownedRoot);
        ancestry.EnsureCreated(safeHomeDirectory);
        ValidateOperationalDirectories();
    }

    private void ValidateOperationalDirectories()
    {
        ancestry.EnsureExisting(ownedRoot);
        ancestry.EnsureExisting(hooksDirectory);
        ancestry.EnsureExisting(safeHomeDirectory);
        VerifyOwnedEmptyDirectory(hooksDirectory, requireEmpty: true);
        VerifyOwnedEmptyDirectory(safeHomeDirectory, requireEmpty: true);
    }

    private void EnsureOwnedRootIsSafe()
    {
        ancestry.EnsureExisting(ownedRoot);
    }

    private void VerifyOwnedEmptyDirectory(string path, bool requireEmpty)
    {
        EnsureOwnedRootIsSafe();
        ancestry.EnsureExisting(path);
        var relative = Path.GetRelativePath(ownedRoot, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ImplementationException("implementation_workspace_configuration", "A Git safety directory escaped the Forge-owned root.");
        var current = ownedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".") continue;
            current = Path.Combine(current, segment);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new ImplementationException("implementation_workspace_configuration", "A Forge-owned Git safety directory contains a reparse point.");
        }
        if (requireEmpty && Directory.EnumerateFileSystemEntries(path).Any())
            throw new ImplementationException("implementation_workspace_configuration", "The Forge-owned Git safety directory must remain empty.");
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        if (maximumCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16_384));
        var buffer = new char[4_096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            if (read == 0) break;
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return new BoundedText(builder.ToString(), truncated);
    }

    private static void KillTree(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process raced with termination; draining below confirms all redirected streams close.
        }
    }

    private static void CloseInput(Process? process)
    {
        try { process?.StandardInput.Close(); }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
    }

    private static async Task DrainAfterTerminationAsync(
        Process? process,
        Task<BoundedText>? outputTask,
        Task<BoundedText>? errorTask)
    {
        var drains = new List<Task>();
        if (process is not null) drains.Add(WaitForExitSafelyAsync(process));
        if (outputTask is not null) drains.Add(outputTask);
        if (errorTask is not null) drains.Add(errorTask);
        try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException) { }
    }

    private static async Task WaitForExitSafelyAsync(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None); }
        catch (InvalidOperationException) { }
    }

    private sealed record BoundedText(string Text, bool Truncated);
}
