using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class RepositoryFileSafetyPolicy
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "build",
        "coverage", "TestResults", "packages", "vendor", ".next", ".nuget", "bower_components"
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

    public bool IsExcludedPath(string relativePath) =>
        relativePath.Split('/', '\\').Any(ExcludedDirectories.Contains);

    public bool IsBinaryOrUnsupported(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return BinaryExtensions.Contains(extension) || !TextExtensions.Contains(extension) && !IsSpecialTextFile(relativePath);
    }

    public bool IsSecretFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".pypirc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".netrc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(name, @"(?i)(credential|private[-_. ]?key)") ||
               new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsGeneratedFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Contains(".min.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    public bool ContainsSensitiveValues(string content) =>
        SensitiveContentDetector.ContainsSensitiveValue(content);

    public string ResolveContainedPath(string root, string relativePath)
    {
        if (!RepositoryPathRules.IsSafeRelativePath(relativePath))
            throw Unsafe("The implementation contains an unsafe repository path.");
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(relativePath, normalizedRoot);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw Unsafe("The implementation path escaped the isolated worktree.");
        EnsureNoReparseAncestors(normalizedRoot, fullPath);
        return fullPath;
    }

    public void ValidateEligiblePath(string root, string relativePath, bool mayNotExist)
    {
        if (IsExcludedPath(relativePath) || IsGeneratedFile(relativePath) || IsSecretFile(relativePath) ||
            IsBinaryOrUnsupported(relativePath))
            throw new ImplementationException("implementation_unsupported_file",
                $"Approved path '{relativePath}' is not an eligible implementation text file.");
        var fullPath = ResolveContainedPath(root, relativePath);
        if (!mayNotExist && !File.Exists(fullPath))
            throw new ImplementationException("implementation_workspace_conflict",
                $"Approved path '{relativePath}' is missing from the isolated worktree.", true);
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw Unsafe($"Approved path '{relativePath}' is a symbolic link or reparse point.");
    }

    private static void EnsureNoReparseAncestors(string root, string fullPath)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw Unsafe("The isolated worktree root cannot be a symbolic link or reparse point.");
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(current) && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Unsafe("An implementation path contains a symbolic-link or reparse-point ancestor.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsSpecialTextFile(string relativePath) =>
        Path.GetFileName(relativePath).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);

    private static ImplementationException Unsafe(string message) =>
        new("implementation_unsafe_path", message);
}
