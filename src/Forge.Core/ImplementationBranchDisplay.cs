namespace Forge.Core;

public static class ImplementationBranchDisplay
{
    public const string SafeLabel = "forge/task-[internal-id-omitted]";

    public static string Format(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        return SafeLabel;
    }
}
