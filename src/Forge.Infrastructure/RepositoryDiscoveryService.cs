using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class RepositoryDiscoveryService(
    RepositoryAnalysisLimits limits,
    TimeProvider timeProvider) : IRepositoryDiscoveryService
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "build",
        "coverage", "TestResults", "packages"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".json", ".jsonc", ".ts", ".tsx", ".js", ".jsx",
        ".md", ".txt", ".xml", ".yml", ".yaml", ".toml", ".props", ".targets", ".config",
        ".css", ".scss", ".html", ".htm", ".sql", ".ps1", ".sh", ".cmd", ".bat"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".zip", ".7z", ".rar", ".png", ".jpg", ".jpeg", ".gif",
        ".webp", ".ico", ".pdf", ".woff", ".woff2", ".ttf", ".eot", ".mp3", ".mp4",
        ".mov", ".avi", ".db", ".sqlite", ".sqlite3"
    };

    public async Task<RepositorySnapshotReadResult> ReadSnapshotAsync(
        RepositorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(snapshot.NormalizedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return new RepositorySnapshotReadResult(false, []);

        if (snapshot.IsGitRepository)
        {
            var head = (await RunGitAsync(root, ["rev-parse", "HEAD"], cancellationToken))?.Output.Trim();
            var status = (await RunGitAsync(root, ["status", "--short"], cancellationToken))?.Output.TrimEnd() ?? string.Empty;
            var state = string.IsNullOrWhiteSpace(status) ? "clean" : "dirty";
            if (!string.Equals(head, snapshot.FullHeadSha, StringComparison.Ordinal) ||
                !string.Equals(state, snapshot.WorkingTreeStatus, StringComparison.Ordinal) ||
                snapshot.GitStatusHash is null ||
                !string.Equals(Hash(status), snapshot.GitStatusHash, StringComparison.Ordinal))
                return new RepositorySnapshotReadResult(false, []);
        }

        var textFiles = new List<RepositoryTextFile>(snapshot.Files.Count);
        foreach (var metadata in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ResolveContainedPath(root, metadata.RelativePath);
            FileInfo info;
            try { info = new FileInfo(fullPath); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return new RepositorySnapshotReadResult(false, []);
            }
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length != metadata.SizeBytes || info.LastWriteTimeUtc > snapshot.AnalyzedAt.UtcDateTime)
                return new RepositorySnapshotReadResult(false, []);

            string content;
            try { content = await File.ReadAllTextAsync(fullPath, cancellationToken); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or DecoderFallbackException)
            {
                return new RepositorySnapshotReadResult(false, []);
            }
            if (content.IndexOf('\0') >= 0) return new RepositorySnapshotReadResult(false, []);
            textFiles.Add(new RepositoryTextFile(metadata, content));
        }

        return new RepositorySnapshotReadResult(true, textFiles);
    }

    public async Task<RepositoryDiscoveryResult> DiscoverAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new RepositoryDiscoveryException("missing_path", "A repository path is required.");

        string root;
        try { root = Path.GetFullPath(repositoryPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new RepositoryDiscoveryException("unsafe_path", "The repository path is invalid or unsafe.", exception);
        }

        if (!Directory.Exists(root))
            throw new RepositoryDiscoveryException("missing_path", "The repository directory does not exist.");

        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new RepositoryDiscoveryException("unsafe_path", "Repository roots that are symbolic links or reparse points are not supported.");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new RepositoryDiscoveryException("inaccessible_path", "The repository directory is not accessible.", exception);
        }

        var warnings = new List<string>();
        var gitTopLevel = await RunGitAsync(root, ["rev-parse", "--show-toplevel"], cancellationToken);
        var isGit = gitTopLevel is { ExitCode: 0 } && PathsEqual(root, gitTopLevel.Output.Trim());
        string? branch = null;
        string? head = null;
        var workingTreeStatus = "unknown";
        string gitStatus = string.Empty;
        IReadOnlyList<string> candidates;
        var skippedDirectoryCount = 0;

        if (isGit)
        {
            branch = (await RunGitAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken))?.Output.Trim();
            head = (await RunGitAsync(root, ["rev-parse", "HEAD"], cancellationToken))?.Output.Trim();
            gitStatus = (await RunGitAsync(root, ["status", "--short"], cancellationToken))?.Output.TrimEnd() ?? string.Empty;
            workingTreeStatus = string.IsNullOrWhiteSpace(gitStatus) ? "clean" : "dirty";
            var listed = await RunGitAsync(root, ["ls-files"], cancellationToken);
            if (listed is null || listed.ExitCode != 0)
                throw new RepositoryDiscoveryException("inaccessible_path", "Git could not list repository files safely.");
            candidates = listed.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('/', Path.DirectorySeparatorChar))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            candidates = EnumerateFilesSafely(root, warnings, cancellationToken, out skippedDirectoryCount);
        }

        if (candidates.Count > limits.MaximumDiscoveredFiles)
        {
            warnings.Add($"File discovery stopped at the configured {limits.MaximumDiscoveredFiles:N0}-file limit; the snapshot is partial.");
            candidates = candidates.Take(limits.MaximumDiscoveredFiles).ToArray();
        }

        var metadata = new List<RepositoryFileMetadata>();
        var textFiles = new List<RepositoryTextFile>();
        var fingerprintEntries = new List<string>();
        long totalTextBytes = 0;
        var excludedCount = skippedDirectoryCount;
        var largeFileWarningAdded = false;
        var totalLimitReached = false;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(candidate);
            string fullPath;
            try { fullPath = ResolveContainedPath(root, relative); }
            catch (RepositoryDiscoveryException) { throw; }

            if (IsExcludedPath(relative) || IsSecretFile(relative) || IsGeneratedFile(relative))
            {
                excludedCount++;
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists) { excludedCount++; continue; }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { excludedCount++; continue; }
            }
            catch (UnauthorizedAccessException)
            {
                excludedCount++;
                warnings.Add($"An inaccessible file was excluded: {relative}.");
                continue;
            }

            fingerprintEntries.Add($"{relative}|{info.Length}");
            var extension = Path.GetExtension(relative);
            if (BinaryExtensions.Contains(extension) || !TextExtensions.Contains(extension) && !IsSpecialTextFile(relative))
            {
                excludedCount++;
                continue;
            }
            if (info.Length > limits.MaximumTextFileBytes)
            {
                excludedCount++;
                if (!largeFileWarningAdded)
                {
                    warnings.Add($"Text files larger than {limits.MaximumTextFileBytes / 1024:N0} KB were excluded.");
                    largeFileWarningAdded = true;
                }
                continue;
            }
            if (totalTextBytes + info.Length > limits.MaximumTotalTextBytes)
            {
                excludedCount++;
                if (!totalLimitReached)
                {
                    warnings.Add($"Text inspection stopped at the configured {limits.MaximumTotalTextBytes / (1024 * 1024):N0} MB total-text limit; the snapshot is partial.");
                    totalLimitReached = true;
                }
                continue;
            }

            string content;
            try { content = await File.ReadAllTextAsync(fullPath, cancellationToken); }
            catch (DecoderFallbackException) { excludedCount++; continue; }
            catch (UnauthorizedAccessException)
            {
                excludedCount++;
                warnings.Add($"An inaccessible file was excluded: {relative}.");
                continue;
            }
            if (content.IndexOf('\0') >= 0) { excludedCount++; continue; }

            totalTextBytes += info.Length;
            var fileMetadata = new RepositoryFileMetadata(
                relative,
                extension.ToLowerInvariant(),
                info.Length,
                CountLines(content),
                DetectRole(relative),
                IsTestPath(relative),
                DetectAssociation(relative),
                ExtractSymbols(extension, content));
            metadata.Add(fileMetadata);
            textFiles.Add(new RepositoryTextFile(fileMetadata, content));
            fingerprintEntries.Add($"{relative}|{Hash(content)}");
        }

        var projects = metadata.Where(file => IsProjectFile(file.RelativePath)).Select(file => file.RelativePath).ToArray();
        var tests = metadata.Where(file => file.IsTest).Select(file => TestLocation(file.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var languages = metadata.Select(file => DetectLanguage(file.Extension, file.RelativePath)).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var extensions = metadata.Select(file => string.IsNullOrWhiteSpace(file.Extension) ? "[none]" : file.Extension)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var analyzedAt = timeProvider.GetUtcNow();
        var fingerprintMaterial = string.Join("\n", new[] { head ?? string.Empty, gitStatus }.Concat(fingerprintEntries.Order(StringComparer.Ordinal)));
        var snapshot = new RepositorySnapshot(
            root,
            isGit,
            NullIfWhiteSpace(branch),
            head is { Length: >= 8 } ? head[..8] : NullIfWhiteSpace(head),
            NullIfWhiteSpace(head),
            workingTreeStatus,
            candidates.Count,
            metadata.Count,
            excludedCount,
            languages,
            extensions,
            projects,
            tests,
            warnings.Distinct().ToArray(),
            analyzedAt,
            Hash(fingerprintMaterial),
            metadata,
            isGit ? Hash(gitStatus) : null);
        return new RepositoryDiscoveryResult(snapshot, textFiles);
    }

    private IReadOnlyList<string> EnumerateFilesSafely(string root, List<string> warnings, CancellationToken cancellationToken, out int skippedDirectoryCount)
    {
        skippedDirectoryCount = 0;
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && results.Count <= limits.MaximumDiscoveredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory).OrderDescending())
                {
                    var info = new DirectoryInfo(child);
                    if (ExcludedDirectories.Contains(info.Name) || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedDirectoryCount++;
                        continue;
                    }
                    _ = ResolveContainedPath(root, Path.GetRelativePath(root, child));
                    pending.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    _ = ResolveContainedPath(root, Path.GetRelativePath(root, file));
                    results.Add(Path.GetRelativePath(root, file));
                    if (results.Count > limits.MaximumDiscoveredFiles) break;
                }
            }
            catch (UnauthorizedAccessException)
            {
                warnings.Add($"An inaccessible directory was skipped: {NormalizeRelativePath(Path.GetRelativePath(root, directory))}.");
            }
        }
        return results;
    }

    private static async Task<GitResult?> RunGitAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            return new GitResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    internal static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new RepositoryDiscoveryException("unsafe_path", "An inspected path escaped the repository root.");
        var fullPath = Path.GetFullPath(relativePath, root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new RepositoryDiscoveryException("unsafe_path", "An inspected path escaped the repository root.");
        return fullPath;
    }

    internal static bool IsSecretFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(name, @"(?i)(credential|private[-_. ]?key)") ||
               new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcludedPath(string relativePath) =>
        relativePath.Split('/', '\\').Any(ExcludedDirectories.Contains);

    private static bool IsGeneratedFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Contains(".min.", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecialTextFile(string relativePath) =>
        Path.GetFileName(relativePath).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    private static string DetectRole(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase) || name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)) return "documentation";
        if (IsProjectFile(path)) return "project manifest";
        if (IsTestPath(path)) return "test";
        if (name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) || name.Equals("main.tsx", StringComparison.OrdinalIgnoreCase)) return "entry point";
        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || name.Equals("vite.config.ts", StringComparison.OrdinalIgnoreCase)) return "configuration";
        return "source";
    }

    private static bool IsTestPath(string path) =>
        path.Split('/', '\\').Any(part => part.Equals("test", StringComparison.OrdinalIgnoreCase) || part.Equals("tests", StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase);

    private static string? DetectAssociation(string path)
    {
        var parts = path.Split('/', '\\');
        var srcOrTests = Array.FindIndex(parts, part => part.Equals("src", StringComparison.OrdinalIgnoreCase) || part.Equals("tests", StringComparison.OrdinalIgnoreCase));
        return srcOrTests >= 0 && srcOrTests + 1 < parts.Length ? parts[srcOrTests + 1] : parts.Length > 1 ? parts[0] : null;
    }

    private static IReadOnlyList<string> ExtractSymbols(string extension, string content)
    {
        var pattern = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? @"\b(?:class|interface|record|enum|struct)\s+([A-Za-z_][A-Za-z0-9_]*)"
            : extension is ".ts" or ".tsx" or ".js" or ".jsx"
                ? @"\b(?:class|interface|type|function|const)\s+([A-Za-z_$][A-Za-z0-9_$]*)"
                : null;
        return pattern is null ? [] : Regex.Matches(content, pattern).Select(match => match.Groups[1].Value).Distinct().Take(100).ToArray();
    }

    private static string? DetectLanguage(string extension, string path) => extension.ToLowerInvariant() switch
    {
        ".cs" or ".csproj" => "C#/.NET",
        ".ts" or ".tsx" => "TypeScript",
        ".js" or ".jsx" => "JavaScript",
        ".css" or ".scss" => "CSS",
        ".html" or ".htm" => "HTML",
        ".ps1" => "PowerShell",
        ".sql" => "SQL",
        ".json" when Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase) => "Node.js",
        _ => null
    };

    private static string TestLocation(string path)
    {
        var parts = path.Split('/', '\\');
        var index = Array.FindIndex(parts, part => part.Equals("tests", StringComparison.OrdinalIgnoreCase) || part.Equals("test", StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? string.Join('/', parts.Take(Math.Min(parts.Length, index + 2))) : path;
    }

    private static int CountLines(string content) => content.Length == 0 ? 0 : content.Count(character => character == '\n') + 1;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private sealed record GitResult(int ExitCode, string Output, string Error);
}
