namespace Forge.Core;

public enum FakeImplementationContentStyle
{
    SlashLine,
    HashLine,
    SqlLine,
    CommandLine,
    CssBlock,
    XmlBlock,
    MarkdownBlock,
    JsonObject
}

public static class FakeImplementationCapabilityMatrix
{
    private static readonly IReadOnlyDictionary<string, FakeImplementationContentStyle> Styles =
        new Dictionary<string, FakeImplementationContentStyle>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = FakeImplementationContentStyle.SlashLine,
            [".ts"] = FakeImplementationContentStyle.SlashLine,
            [".tsx"] = FakeImplementationContentStyle.SlashLine,
            [".js"] = FakeImplementationContentStyle.SlashLine,
            [".jsx"] = FakeImplementationContentStyle.SlashLine,
            [".jsonc"] = FakeImplementationContentStyle.SlashLine,
            [".css"] = FakeImplementationContentStyle.CssBlock,
            [".scss"] = FakeImplementationContentStyle.CssBlock,
            [".html"] = FakeImplementationContentStyle.XmlBlock,
            [".htm"] = FakeImplementationContentStyle.XmlBlock,
            [".xml"] = FakeImplementationContentStyle.XmlBlock,
            [".csproj"] = FakeImplementationContentStyle.XmlBlock,
            [".props"] = FakeImplementationContentStyle.XmlBlock,
            [".targets"] = FakeImplementationContentStyle.XmlBlock,
            [".config"] = FakeImplementationContentStyle.XmlBlock,
            [".slnx"] = FakeImplementationContentStyle.XmlBlock,
            [".md"] = FakeImplementationContentStyle.MarkdownBlock,
            [".ps1"] = FakeImplementationContentStyle.HashLine,
            [".sh"] = FakeImplementationContentStyle.HashLine,
            [".yml"] = FakeImplementationContentStyle.HashLine,
            [".yaml"] = FakeImplementationContentStyle.HashLine,
            [".toml"] = FakeImplementationContentStyle.HashLine,
            [".txt"] = FakeImplementationContentStyle.HashLine,
            [".sql"] = FakeImplementationContentStyle.SqlLine,
            [".cmd"] = FakeImplementationContentStyle.CommandLine,
            [".bat"] = FakeImplementationContentStyle.CommandLine,
            [".sln"] = FakeImplementationContentStyle.HashLine,
            [".json"] = FakeImplementationContentStyle.JsonObject
        };

    private static readonly HashSet<string> RejectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "packages.lock.json", "pnpm-lock.yaml", "yarn.lock"
    };
    private static readonly HashSet<string> RejectedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "build", "coverage",
        "TestResults", "packages", "vendor", ".next", ".nuget", "bower_components"
    };

    public static void ValidatePlan(ImplementationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var file in plan.AffectedFiles.Where(file => file.Action != PlannedFileAction.Inspect))
            _ = GetStyle(file.Path, file.Action);
    }

    public static FakeImplementationContentStyle GetStyle(string path, PlannedFileAction action)
    {
        if (!RepositoryPathRules.IsSafeRelativePath(path, 300))
            throw Unsupported(path);
        var name = Path.GetFileName(path);
        if (RejectedNames.Contains(name) || path.Split('/', '\\').Any(RejectedDirectories.Contains) ||
            name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".min.", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)) throw Unsupported(path);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            return FakeImplementationContentStyle.HashLine;
        if (name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
            return FakeImplementationContentStyle.HashLine;
        if (!Styles.TryGetValue(Path.GetExtension(path), out var style)) throw Unsupported(path);
        if (!Enum.IsDefined(action) || action == PlannedFileAction.Inspect) throw Unsupported(path);
        return style;
    }

    public static FakeImplementationContentStyle GetStyle(string path, ImplementationOperationAction action) =>
        GetStyle(path, action switch
        {
            ImplementationOperationAction.Create => PlannedFileAction.Create,
            ImplementationOperationAction.Modify => PlannedFileAction.Modify,
            ImplementationOperationAction.Delete => PlannedFileAction.Delete,
            _ => throw Unsupported(path)
        });

    private static ImplementationException Unsupported(string path) => new(
        "implementation_terminal_incompatibility",
        $"Deterministic Fake implementation does not support the approved action for '{path}'.");
}
