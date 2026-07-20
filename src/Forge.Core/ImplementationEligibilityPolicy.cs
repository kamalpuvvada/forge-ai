namespace Forge.Core;

/// <summary>Provider-independent eligibility rules for structured implementation operations.</summary>
public static class ImplementationEligibilityPolicy
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

    public static void ValidatePlan(ImplementationPlan plan, int maximumOperations = 10)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var operations = plan.AffectedFiles.Where(file => file.Action != PlannedFileAction.Inspect).ToArray();
        if (operations.Length > maximumOperations)
            throw Unsupported("The approved plan contains an invalid number of implementation operations.");
        foreach (var file in operations) ValidatePath(file.Path, file.Action);
    }

    public static void ValidatePath(string path, PlannedFileAction action)
    {
        if (!Enum.IsDefined(action) || action == PlannedFileAction.Inspect ||
            !RepositoryPathRules.IsSafeRelativePath(path, 300) ||
            IsExcludedPath(path) || IsGeneratedFile(path) || IsSecretFile(path) || IsBinaryOrUnsupported(path))
            throw Unsupported($"Approved path '{path}' is not an eligible implementation text file.");
    }

    public static void ValidatePath(string path, ImplementationOperationAction action) =>
        ValidatePath(path, action switch
        {
            ImplementationOperationAction.Create => PlannedFileAction.Create,
            ImplementationOperationAction.Modify => PlannedFileAction.Modify,
            ImplementationOperationAction.Delete => PlannedFileAction.Delete,
            _ => throw Unsupported($"Approved path '{path}' has an unsupported implementation action.")
        });

    public static bool IsExcludedPath(string relativePath) =>
        relativePath.Split('/', '\\').Any(ExcludedDirectories.Contains);

    public static bool IsBinaryOrUnsupported(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return BinaryExtensions.Contains(extension) || !TextExtensions.Contains(extension) && !IsSpecialTextFile(relativePath);
    }

    public static bool IsSecretFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        var compactName = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".pypirc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".netrc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               compactName.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
               new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsGeneratedFile(string relativePath)
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

    private static bool IsSpecialTextFile(string relativePath) =>
        Path.GetFileName(relativePath).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);

    private static ImplementationException Unsupported(string message) =>
        new("implementation_unsupported_file", message);
}
