using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Forge.Core;

public static class ImplementationReviewFingerprint
{
    private const string PlanVersion = "forge-approved-plan-v1";
    private const string ResultVersion = "forge-implementation-review-v1";
    private const string LegacyRevisionVersion = "forge-legacy-initial-revision-v1";

    public static string ComputePlan(ImplementationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Hash(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("version", PlanVersion);
            writer.WriteString("title", plan.Title);
            writer.WriteString("objective", plan.Objective);
            writer.WriteString("repositoryUnderstanding", plan.RepositoryUnderstanding);
            writer.WritePropertyName("affectedFiles");
            writer.WriteStartArray();
            foreach (var file in plan.AffectedFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("action", file.Action.ToString());
                writer.WriteString("purpose", file.Purpose);
                WriteStrings(writer, "evidenceIds", file.EvidenceIds);
                writer.WriteNumber("confidence", file.Confidence);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("steps");
            writer.WriteStartArray();
            foreach (var step in plan.Steps)
            {
                writer.WriteStartObject();
                writer.WriteNumber("order", step.Order);
                writer.WriteString("description", step.Description);
                WriteStrings(writer, "affectedPaths", step.AffectedPaths);
                WriteStrings(writer, "evidenceIds", step.EvidenceIds);
                writer.WriteString("expectedResult", step.ExpectedResult);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteStrings(writer, "proposedValidationCommands", plan.ProposedValidationCommands);
            WriteStrings(writer, "risks", plan.Risks);
            WriteStrings(writer, "assumptions", plan.Assumptions);
            WriteStrings(writer, "unresolvedQuestions", plan.UnresolvedQuestions);
            writer.WritePropertyName("requirementCoverage");
            writer.WriteStartArray();
            foreach (var item in plan.RequirementCoverage)
            {
                writer.WriteStartObject();
                writer.WriteString("requirement", item.Requirement);
                WriteStrings(writer, "affectedPaths", item.AffectedPaths);
                writer.WritePropertyName("stepOrders");
                writer.WriteStartArray();
                foreach (var order in item.StepOrders) writer.WriteNumberValue(order);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("summary", plan.Summary);
            writer.WriteString("source", plan.Source.ToString());
            WriteNullableString(writer, "planningModel", plan.PlanningModel);
            writer.WriteString("createdAt", plan.CreatedAt.ToUniversalTime());
            writer.WriteString("repositoryFingerprint", plan.RepositoryFingerprint);
            writer.WriteEndObject();
        });
    }

    public static string ComputeResult(
        Guid taskId,
        Guid revisionId,
        int revisionNumber,
        ImplementationRevisionKind kind,
        string planFingerprint,
        ImplementationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Hash(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("version", ResultVersion);
            writer.WriteString("taskId", taskId);
            writer.WriteString("revisionId", revisionId);
            writer.WriteNumber("revisionNumber", revisionNumber);
            writer.WriteString("kind", kind.ToString());
            writer.WriteString("planFingerprint", planFingerprint);
            writer.WriteString("baseCommitSha", result.BaseCommitSha);
            writer.WriteString("source", result.Source.ToString());
            WriteNullableString(writer, "model", result.Model);
            writer.WriteString("branch", result.Branch);
            writer.WriteString("summary", result.Summary);
            WriteStrings(writer, "warnings", result.Warnings);
            writer.WritePropertyName("changedFiles");
            writer.WriteStartArray();
            foreach (var file in result.ChangedFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("action", file.Action.ToString());
                WriteNullableString(writer, "originalContentSha256", file.OriginalContentSha256);
                WriteNullableString(writer, "newContentSha256", file.NewContentSha256);
                writer.WriteNumber("originalBytes", file.OriginalBytes);
                writer.WriteNumber("newBytes", file.NewBytes);
                writer.WriteNumber("originalLines", file.OriginalLines);
                writer.WriteNumber("newLines", file.NewLines);
                writer.WriteNumber("additions", file.Additions);
                writer.WriteNumber("deletions", file.Deletions);
                writer.WriteNumber("fullDiffCharacters", file.FullDiffCharacters);
                writer.WriteString("diffPreviewSha256", HashText(file.DiffPreview));
                writer.WriteNumber("displayedDiffCharacters", file.DisplayedDiffCharacters);
                writer.WriteBoolean("diffTruncated", file.DiffTruncated);
                writer.WriteNumber("fullDiffUtf8Bytes", file.FullDiffUtf8Bytes);
                writer.WriteNumber("displayedDiffUtf8Bytes", file.DisplayedDiffUtf8Bytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("fullDiffCharacters", result.FullDiffCharacters);
            writer.WriteNumber("displayedDiffCharacters", result.DisplayedDiffCharacters);
            writer.WriteBoolean("diffTruncated", result.DiffTruncated);
            writer.WriteNumber("fullDiffUtf8Bytes", result.FullDiffUtf8Bytes);
            writer.WriteNumber("displayedDiffUtf8Bytes", result.DisplayedDiffUtf8Bytes);
            writer.WriteString("completedAt", result.CompletedAt.ToUniversalTime());
            writer.WriteBoolean("activeCheckoutVerified", result.ActiveCheckoutVerified);
            writer.WriteString("worktreeFingerprint", result.WorktreeFingerprint);
            writer.WriteNumber("worktreeFileCount", result.WorktreeFileCount);
            writer.WriteNumber("worktreeBytes", result.WorktreeBytes);
            writer.WriteEndObject();
        });
    }

    public static Guid CreateLegacyRevisionId(Guid taskId, string resultFingerprint)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{LegacyRevisionVersion}\n{taskId:D}\n{resultFingerprint}"));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
            writer.Flush();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}
