using System.Security.Cryptography;
using System.Text;

namespace Forge.Core;

public static class ImplementationContextIdentity
{
    public const string SchemaVersion = "forge-implementation-context-v1";

    public static string ComputeSource(
        string baseCommitSha,
        string planFingerprint,
        string path,
        PlannedFileAction action,
        string? originalSha256,
        int originalUtf8Bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, SchemaVersion);
        Append(hash, baseCommitSha);
        Append(hash, planFingerprint);
        Append(hash, RepositoryPathRules.Normalize(path));
        Append(hash, action.ToString().ToLowerInvariant());
        Append(hash, originalSha256 ?? "ABSENT");
        Append(hash, originalUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeGlobal(ImplementationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, SchemaVersion);
        Append(hash, context.ApprovedRequirementSummary);
        Append(hash, context.PlanFingerprint);
        Append(hash, context.BaseCommitSha);
        foreach (var file in context.Files.OrderBy(file => RepositoryPathRules.Normalize(file.Path), StringComparer.Ordinal))
        {
            Append(hash, RepositoryPathRules.Normalize(file.Path));
            Append(hash, file.PlannedAction.ToString());
            Append(hash, file.OriginalContentSha256 ?? "ABSENT");
            Append(hash, file.OriginalUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, file.SourceContextIdentity);
            Append(hash, file.OriginalContent ?? string.Empty);
        }
        foreach (var item in (context.Evidence ?? []).OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Append(hash, item.Id);
            Append(hash, RepositoryPathRules.Normalize(item.RelativePath));
            Append(hash, item.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, item.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, item.Excerpt);
            Append(hash, item.ReasonSelected);
            Append(hash, item.Score.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, item.ContentHash);
        }
        foreach (var convention in context.ProjectConventions ?? []) Append(hash, convention);
        Append(hash, context.OmittedOptionalContextCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
