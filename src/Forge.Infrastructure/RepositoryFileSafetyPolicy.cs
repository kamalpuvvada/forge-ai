using Forge.Core;

namespace Forge.Infrastructure;

public sealed class RepositoryFileSafetyPolicy
{
    public bool IsExcludedPath(string relativePath) =>
        ImplementationEligibilityPolicy.IsExcludedPath(relativePath);

    public bool IsBinaryOrUnsupported(string relativePath)
    {
        return ImplementationEligibilityPolicy.IsBinaryOrUnsupported(relativePath);
    }

    public bool IsSecretFile(string relativePath)
    {
        return ImplementationEligibilityPolicy.IsSecretFile(relativePath);
    }

    public bool IsGeneratedFile(string relativePath)
    {
        return ImplementationEligibilityPolicy.IsGeneratedFile(relativePath);
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

    private static ImplementationException Unsafe(string message) =>
        new("implementation_unsafe_path", message);
}
