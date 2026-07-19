using Forge.Core;

namespace Forge.Infrastructure;

public enum SafeDirectoryEntryKind
{
    Missing,
    Directory,
    Other
}

public readonly record struct SafeDirectoryEntry(
    SafeDirectoryEntryKind Kind,
    bool IsReparseOrLink);

public interface ISafeDirectoryFileSystem
{
    SafeDirectoryEntry Inspect(string path);
    void CreateDirectory(string path);
}

public sealed class PhysicalSafeDirectoryFileSystem : ISafeDirectoryFileSystem
{
    public SafeDirectoryEntry Inspect(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            var isLink = (attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
            return new SafeDirectoryEntry(
                isDirectory ? SafeDirectoryEntryKind.Directory : SafeDirectoryEntryKind.Other,
                isLink);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SafeDirectoryEntry(SafeDirectoryEntryKind.Missing, false);
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
}

public interface ISafeDirectoryAncestry
{
    string Normalize(string path);
    void EnsureCreated(string path);
    void EnsureExisting(string path);
    bool IsExistingSafe(string path);
}

public sealed class SafeDirectoryAncestry(
    ISafeDirectoryFileSystem? fileSystem = null) : ISafeDirectoryAncestry
{
    private readonly ISafeDirectoryFileSystem fileSystem = fileSystem ?? new PhysicalSafeDirectoryFileSystem();

    public string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!Path.IsPathFullyQualified(fullPath) || string.IsNullOrWhiteSpace(root))
                throw Unsafe("A safe absolute directory ancestry could not be established.");
            return PathsEqual(fullPath, root)
                ? root
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ImplementationException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Unsafe("A configured directory path is invalid.", exception);
        }
    }

    public void EnsureCreated(string path)
    {
        var normalized = Normalize(path);
        var chain = BuildChain(normalized);
        for (var index = 0; index < chain.Count; index++)
        {
            ValidateExistingPrefix(chain, index - 1);
            var entry = Inspect(chain[index]);
            if (entry.Kind == SafeDirectoryEntryKind.Missing)
            {
                ValidateExistingPrefix(chain, index - 1);
                try { fileSystem.CreateDirectory(chain[index]); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw Unsafe("A Forge-owned directory could not be created safely.", exception);
                }
                ValidateDirectoryEntry(chain[index], Inspect(chain[index]));
                ValidateExistingPrefix(chain, index);
            }
            else
            {
                ValidateDirectoryEntry(chain[index], entry);
            }
        }
        ValidateExistingPrefix(chain, chain.Count - 1);
    }

    public void EnsureExisting(string path)
    {
        var chain = BuildChain(Normalize(path));
        ValidateExistingPrefix(chain, chain.Count - 1);
    }

    public bool IsExistingSafe(string path)
    {
        try
        {
            EnsureExisting(path);
            return true;
        }
        catch (Exception exception) when (exception is ImplementationException or IOException or
                                          UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private IReadOnlyList<string> BuildChain(string normalized)
    {
        var root = Path.GetPathRoot(normalized)
            ?? throw Unsafe("A trusted lexical filesystem root could not be established.");
        var relative = Path.GetRelativePath(root, normalized);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment == ".."))
            throw Unsafe("A directory path escaped its trusted lexical filesystem root.");
        var chain = new List<string> { root };
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            chain.Add(current);
        }
        return chain;
    }

    private void ValidateExistingPrefix(IReadOnlyList<string> chain, int maximumIndex)
    {
        for (var index = 0; index <= maximumIndex; index++)
            ValidateDirectoryEntry(chain[index], Inspect(chain[index]));
    }

    private SafeDirectoryEntry Inspect(string path)
    {
        try { return fileSystem.Inspect(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unsafe("A directory ancestry could not be inspected safely.", exception);
        }
    }

    private static void ValidateDirectoryEntry(string path, SafeDirectoryEntry entry)
    {
        if (entry.Kind == SafeDirectoryEntryKind.Missing)
            throw Unsafe("A required directory ancestry segment is missing.");
        if (entry.Kind != SafeDirectoryEntryKind.Directory || entry.IsReparseOrLink)
            throw Unsafe("A directory ancestry contains an unsafe filesystem entry.");
        if (!Path.IsPathFullyQualified(path))
            throw Unsafe("A directory ancestry escaped its expected lexical chain.");
    }

    private static ImplementationException Unsafe(string message, Exception? inner = null) =>
        new("implementation_workspace_configuration", message, false, inner);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
