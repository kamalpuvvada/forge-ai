using System.Security.Cryptography;
using System.Text;

namespace Forge.Core;

public static class ImplementationOutputValidator
{
    public static void Validate(
        ImplementationContext context,
        ImplementationOutput output,
        ImplementationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(context.ApprovedPlan, context.Files, output, limits);
        if (!string.IsNullOrWhiteSpace(context.ContextFingerprint) &&
            !string.Equals(output.ContextFingerprint, context.ContextFingerprint, StringComparison.Ordinal))
            throw Invalid("The implementation output does not match the approved context fingerprint.");
        if (output.Source == ImplementationSource.OpenAI &&
            (string.IsNullOrWhiteSpace(output.ReasoningEffort) || output.ReasoningEffort.Length > 32))
            throw Invalid("The OpenAI implementation reasoning metadata is invalid.");
    }

    public static void Validate(
        ImplementationPlan plan,
        IReadOnlyList<ImplementationFileContext> files,
        ImplementationOutput output,
        ImplementationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(output.Source)) throw Invalid("The implementation source is invalid.");
        if (output.Source == ImplementationSource.DeterministicFake && output.Model is not null ||
            output.Source == ImplementationSource.OpenAI && string.IsNullOrWhiteSpace(output.Model))
            throw Invalid("The implementation source and model metadata are inconsistent.");
        if (output.Model?.Length > 160)
            throw Invalid("The implementation model identifier exceeds its allowed length.");
        if (output.Warnings is null || output.Operations is null)
            throw Invalid("The implementation output is incomplete.");

        Required(output.Summary, limits.MaximumSummaryCharacters, "The implementation summary");
        RejectSensitive(output.Summary, "The implementation summary contains sensitive content.");
        if (output.Warnings.Count > limits.MaximumWarnings)
            throw Invalid("The implementation output contains too many warnings.");
        foreach (var warning in output.Warnings)
        {
            Required(warning, limits.MaximumItemSummaryCharacters, "An implementation warning");
            RejectSensitive(warning, "An implementation warning contains sensitive content.");
        }

        var approved = plan.AffectedFiles
            .Where(file => file.Action is PlannedFileAction.Create or PlannedFileAction.Modify or PlannedFileAction.Delete)
            .ToDictionary(file => RepositoryPathRules.Normalize(file.Path), StringComparer.OrdinalIgnoreCase);
        if (approved.Count == 0) throw Invalid("The approved plan contains no implementable file operations.");
        if (approved.Count > limits.MaximumApprovedOperations || output.Operations.Count > limits.MaximumApprovedOperations)
            throw Invalid("The implementation exceeds the approved operation limit.");
        if (output.Operations.Count != approved.Count)
            throw Invalid("Every approved mutating path must have exactly one implementation operation.");

        var contexts = files.ToDictionary(file => RepositoryPathRules.Normalize(file.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalGenerated = 0;
        foreach (var operation in output.Operations)
        {
            Required(operation.Path, limits.MaximumRelativePathCharacters, "An implementation path");
            Required(operation.Summary, limits.MaximumItemSummaryCharacters, "An implementation-operation summary");
            RejectSensitive(operation.Summary, "An implementation-operation summary contains sensitive content.");
            var path = RepositoryPathRules.Normalize(operation.Path);
            ImplementationEligibilityPolicy.ValidatePath(path, operation.Action);
            if (!RepositoryPathRules.IsSafeRelativePath(operation.Path, limits.MaximumRelativePathCharacters))
                throw Invalid("The implementation output contains an unsafe repository path.");
            if (!seen.Add(path)) throw Invalid("The implementation output contains a duplicate path.");
            if (!approved.TryGetValue(path, out var planned))
                throw Invalid("The implementation output contains an undeclared path.");
            if (!contexts.TryGetValue(path, out var context))
                throw Invalid("The implementation output does not match the prepared file context.");
            if (!string.IsNullOrWhiteSpace(context.SourceContextIdentity) &&
                !string.Equals(operation.SourceContextIdentity, context.SourceContextIdentity, StringComparison.Ordinal))
                throw Invalid($"Implementation operation '{path}' does not match the approved source context.");

            var expectedAction = planned.Action switch
            {
                PlannedFileAction.Create => ImplementationOperationAction.Create,
                PlannedFileAction.Modify => ImplementationOperationAction.Modify,
                PlannedFileAction.Delete => ImplementationOperationAction.Delete,
                _ => throw Invalid("Inspect paths cannot be implementation operations.")
            };
            if (operation.Action != expectedAction)
                throw Invalid($"Implementation action for '{path}' does not match the approved plan.");

            if (operation.Action == ImplementationOperationAction.Create)
            {
                if (context.OriginalContent is not null || operation.OriginalContentSha256 is not null ||
                    operation.ExpectedOriginalUtf8Bytes != 0)
                    throw Invalid($"Create operation '{path}' unexpectedly references existing content.");
                ValidateGeneratedContent(operation, limits);
            }
            else
            {
                if (context.OriginalContent is null || string.IsNullOrWhiteSpace(context.OriginalContentSha256) ||
                    !string.Equals(operation.OriginalContentSha256, context.OriginalContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw Invalid($"Implementation operation '{path}' does not match the original content hash.");
                if (operation.ExpectedOriginalUtf8Bytes != context.OriginalUtf8Bytes)
                    throw Invalid($"Implementation operation '{path}' does not match the original content size.");
                if (operation.Action == ImplementationOperationAction.Delete)
                {
                    if (operation.Content is not null)
                        throw Invalid($"Delete operation '{path}' must not contain replacement content.");
                }
                else
                {
                    ValidateGeneratedContent(operation, limits);
                    if (string.Equals(operation.Content, context.OriginalContent, StringComparison.Ordinal))
                        throw Invalid($"Modify operation '{path}' did not change the file.");
                }
            }

            if (operation.Content is not null) totalGenerated = checked(totalGenerated + StrictUtf8ByteCount(operation.Content, operation.Path));
        }

        if (totalGenerated > limits.MaximumTotalGeneratedCharacters)
            throw Invalid("The implementation output exceeds the total generated-content limit.");
        if (!approved.Keys.All(seen.Contains))
            throw Invalid("The implementation output omitted an approved mutating path.");
    }

    public static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static void ValidateGeneratedContent(ImplementationOperation operation, ImplementationLimits limits)
    {
        if (operation.Content is null)
            throw Invalid($"{operation.Action} operation '{operation.Path}' requires replacement content.");
        if (operation.Content.Length > limits.MaximumGeneratedFileCharacters)
            throw Invalid($"Generated content for '{operation.Path}' exceeds its allowed length.");
        if (StrictUtf8ByteCount(operation.Content, operation.Path) > limits.MaximumGeneratedFileCharacters)
            throw Invalid($"Generated content for '{operation.Path}' exceeds its allowed UTF-8 byte length.");
        if (operation.Content.IndexOf('\0') >= 0)
            throw Invalid($"Generated content for '{operation.Path}' is not supported text.");
        if (SensitiveContentDetector.ContainsSensitiveValue(operation.Content))
            throw new ImplementationException("implementation_sensitive_content",
                $"Generated content for '{operation.Path}' contains a sensitive value and cannot be accepted.");
    }

    private static void Required(string value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid($"{label} is required.");
        if (value.Length > maximum) throw Invalid($"{label} exceeds its allowed length.");
    }

    private static ImplementationException Invalid(string message) => new("invalid_implementation", message);

    private static void RejectSensitive(string value, string message)
    {
        if (SensitiveContentDetector.ContainsSensitiveValue(value))
            throw new ImplementationException("implementation_sensitive_content", message);
    }

    private static int StrictUtf8ByteCount(string value, string path)
    {
        try { return new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ImplementationException("invalid_implementation",
                $"Generated content for '{path}' is not valid Unicode text.", false, exception);
        }
    }
}
