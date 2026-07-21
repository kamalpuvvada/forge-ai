using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core;

public static class VerificationFingerprint
{
    private const string RequirementVersion = "forge-approved-requirement-v1";
    private const string ContextVersion = "forge-verification-context-v1";
    private const string PlanVersion = "forge-verification-plan-v1";
    private const string AttemptVersion = "forge-manual-verification-attempt-v1";
    private const string ProviderResponseVersion = "forge-verification-provider-response-v2";

    public static string ComputeApprovedRequirement(EngineeringTask task) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", RequirementVersion);
        writer.WriteString("taskId", task.Id);
        writer.WriteString("summary", task.RequirementSummary);
        if (task.RequirementApprovedAt is { } approvedAt)
            writer.WriteString("approvedAt", approvedAt.ToUniversalTime());
        else
            writer.WriteNull("approvedAt");
        writer.WriteEndObject();
    });

    public static string ComputeContext(VerificationPlanContext context) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", ContextVersion);
        writer.WriteString("taskId", context.TaskId);
        writer.WriteString("approvedRequirementFingerprint", context.ApprovedRequirementFingerprint);
        writer.WriteString("approvedPlanFingerprint", context.ApprovedPlanFingerprint);
        writer.WriteString("implementationRevisionId", context.ImplementationRevisionId);
        writer.WriteString("implementationResultFingerprint", context.ImplementationResultFingerprint);
        writer.WriteString("createdAt", context.CreatedAt.ToUniversalTime());
        writer.WritePropertyName("commands");
        writer.WriteStartArray();
        foreach (var command in context.ApprovedValidationCommands)
        {
            writer.WriteStartObject();
            writer.WriteString("id", command.Id);
            writer.WriteString("sha256", HashText(command.Command));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("changedFiles");
        writer.WriteStartArray();
        foreach (var file in context.ImplementationResult.ChangedFiles)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteString("action", file.Action.ToString());
            WriteNullable(writer, "originalSha256", file.OriginalContentSha256);
            WriteNullable(writer, "newSha256", file.NewContentSha256);
            writer.WriteNumber("additions", file.Additions);
            writer.WriteNumber("deletions", file.Deletions);
            writer.WriteString("diffPreviewSha256", HashText(file.DiffPreview));
            writer.WriteBoolean("diffTruncated", file.DiffTruncated);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string ComputePlan(Guid taskId, VerificationPlan plan) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", PlanVersion);
        writer.WriteString("taskId", taskId);
        writer.WriteString("planId", plan.PlanId);
        writer.WriteNumber("planNumber", plan.PlanNumber);
        writer.WriteString("implementationRevisionId", plan.ImplementationRevisionId);
        writer.WriteString("implementationResultFingerprint", plan.ImplementationResultFingerprint);
        writer.WriteString("approvedRequirementFingerprint", plan.ApprovedRequirementFingerprint);
        writer.WriteString("approvedPlanFingerprint", plan.ApprovedPlanFingerprint);
        writer.WriteString("generationContextFingerprint", plan.GenerationContextFingerprint);
        writer.WriteString("generatedAt", plan.GeneratedAt.ToUniversalTime());
        writer.WriteString("source", plan.Source.ToString());
        WriteNullable(writer, "model", plan.Model);
        WriteNullable(writer, "reasoningEffort", plan.ReasoningEffort);
        writer.WriteString("summary", plan.Summary);
        writer.WriteString("scope", plan.Scope);
        WriteStrings(writer, "preconditions", plan.Preconditions);
        writer.WritePropertyName("testCases");
        writer.WriteStartArray();
        foreach (var testCase in plan.TestCases)
        {
            writer.WriteStartObject();
            writer.WriteString("testCaseId", testCase.TestCaseId);
            writer.WriteNumber("order", testCase.Order);
            writer.WriteString("title", testCase.Title);
            writer.WriteString("objective", testCase.Objective);
            writer.WriteString("category", testCase.Category.ToString());
            writer.WriteBoolean("isRequired", testCase.IsRequired);
            WriteStrings(writer, "preconditions", testCase.Preconditions);
            WriteStrings(writer, "testData", testCase.TestData);
            writer.WritePropertyName("steps");
            writer.WriteStartArray();
            foreach (var step in testCase.OrderedSteps)
            {
                writer.WriteStartObject();
                writer.WriteNumber("order", step.Order);
                writer.WriteString("instruction", step.Instruction);
                WriteNullable(writer, "approvedValidationCommandId", step.ApprovedValidationCommandId);
                writer.WriteString("expectedObservation", step.ExpectedObservation);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("expectedResult", testCase.ExpectedResult);
            WriteStrings(writer, "negativeOrEdgeCases", testCase.NegativeOrEdgeCases);
            WriteStrings(writer, "regressionScope", testCase.RegressionScope);
            WriteStrings(writer, "evidenceRequirements", testCase.EvidenceRequirements);
            WriteStrings(writer, "safetyNotes", testCase.SafetyNotes);
            if (testCase.OriginTestCaseId is { } origin) writer.WriteString("originTestCaseId", origin);
            else writer.WriteNull("originTestCaseId");
            writer.WritePropertyName("regressionFailureReportIds");
            writer.WriteStartArray();
            foreach (var id in testCase.RegressionFailureReportIds) writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteStrings(writer, "risks", plan.Risks);
        WriteStrings(writer, "limitations", plan.Limitations);
        WriteStrings(writer, "evidenceGuidance", plan.EvidenceGuidance);
        writer.WritePropertyName("modelCallIds");
        writer.WriteStartArray();
        foreach (var id in plan.ModelCallIds) writer.WriteStringValue(id);
        writer.WriteEndArray();
        if (plan.SupersedesPlanId is { } superseded) writer.WriteString("supersedesPlanId", superseded);
        else writer.WriteNull("supersedesPlanId");
        WriteNullable(writer, "regenerationReason", plan.RegenerationReason);
        writer.WriteEndObject();
    });

    public static string ComputeAttempt(Guid taskId, ManualVerificationAttempt attempt) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", AttemptVersion);
        writer.WriteString("taskId", taskId);
        writer.WriteString("attemptId", attempt.AttemptId);
        writer.WriteNumber("attemptNumber", attempt.AttemptNumber);
        writer.WriteString("verificationPlanId", attempt.VerificationPlanId);
        writer.WriteString("verificationPlanFingerprint", attempt.VerificationPlanFingerprint);
        writer.WriteString("implementationRevisionId", attempt.ImplementationRevisionId);
        writer.WriteString("implementationResultFingerprint", attempt.ImplementationResultFingerprint);
        writer.WriteString("startedAt", attempt.StartedAt.ToUniversalTime());
        if (attempt.CompletedAt is { } completedAt)
            writer.WriteString("completedAt", completedAt.ToUniversalTime());
        else
            writer.WriteNull("completedAt");
        writer.WriteString("status", attempt.Status.ToString());
        if (attempt.CompletionConfirmation is { } confirmation)
            writer.WriteBoolean("completionConfirmation", confirmation);
        else
            writer.WriteNull("completionConfirmation");
        WriteNullable(writer, "summary", attempt.Summary);
        writer.WritePropertyName("currentResults");
        writer.WriteStartArray();
        foreach (var result in CurrentResults(attempt).OrderBy(item => item.TestCaseId))
        {
            writer.WriteStartObject();
            writer.WriteString("resultRevisionId", result.ResultRevisionId);
            writer.WriteNumber("revisionNumber", result.RevisionNumber);
            writer.WriteString("testCaseId", result.TestCaseId);
            writer.WriteString("result", result.Result.ToString());
            writer.WriteString("recordedAt", result.RecordedAt.ToUniversalTime());
            WriteNullable(writer, "notes", result.Notes);
            WriteNullable(writer, "actualResult", result.ActualResult);
            WriteStrings(writer, "evidenceDescriptions", result.EvidenceDescriptions);
            WriteNullable(writer, "notApplicableReason", result.NotApplicableReason);
            if (result.FailureDetails is { } failure)
            {
                writer.WritePropertyName("failureDetails");
                writer.WriteStartObject();
                writer.WriteString("title", failure.Title);
                writer.WriteString("expectedResult", failure.ExpectedResult);
                writer.WriteString("actualResult", failure.ActualResult);
                WriteStrings(writer, "reproductionSteps", failure.ReproductionSteps);
                WriteStrings(writer, "environmentNotes", failure.EnvironmentNotes);
                WriteNullable(writer, "errorMessage", failure.ErrorMessage);
                WriteStrings(writer, "evidenceDescriptions", failure.EvidenceDescriptions);
                writer.WriteString("severity", failure.Severity.ToString());
                writer.WriteEndObject();
            }
            else writer.WriteNull("failureDetails");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("startedByCommandId", attempt.StartedByCommandId);
        if (attempt.CompletedByCommandId is { } completedBy) writer.WriteString("completedByCommandId", completedBy);
        else writer.WriteNull("completedByCommandId");
        writer.WriteEndObject();
    });

    public static string ComputeProviderResponse(
        Guid taskId,
        Guid generationCommandId,
        VerificationProviderResponseTelemetry response) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", ProviderResponseVersion);
        writer.WriteString("taskId", taskId);
        writer.WriteString("generationAttemptId", generationCommandId);
        writer.WriteNumber("formatVersion", response.FormatVersion ?? VerificationDataFormatVersions.Legacy);
        writer.WriteString("logicalCallId", response.LogicalCallId);
        writer.WriteString("startedAt", response.StartedAt.ToUniversalTime());
        writer.WriteString("receivedAt", response.ReceivedAt.ToUniversalTime());
        WriteNullable(writer, "providerResponseId", response.ProviderResponseId);
        WriteNullable(writer, "providerRequestId", response.ProviderRequestId);
        writer.WriteString("status", response.Status.ToString());
        WriteNullable(writer, "incompleteReason", response.IncompleteReason);
        if (response.UsageAvailable is { } legacyUsage) writer.WriteBoolean("usageAvailable", legacyUsage);
        else writer.WriteNull("usageAvailable");
        writer.WriteString("usageAvailability", response.EffectiveUsageAvailability.ToString());
        WriteNullableNumber(writer, "inputTokens", response.InputTokens);
        WriteNullableNumber(writer, "cachedInputTokens", response.CachedInputTokens);
        WriteNullableNumber(writer, "outputTokens", response.OutputTokens);
        WriteNullableNumber(writer, "reasoningTokens", response.ReasoningTokens);
        writer.WriteNumber("httpStatusCode", response.HttpStatusCode);
        writer.WriteString("dispatchDisposition", response.DispatchDisposition.ToString());
        writer.WriteEndObject();
    });

    public static IReadOnlyList<ManualCaseResultRevision> CurrentResults(ManualVerificationAttempt attempt) =>
        attempt.ResultRevisions
            .GroupBy(result => result.TestCaseId)
            .Select(group => group.OrderByDescending(result => result.RevisionNumber).First())
            .ToArray();

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void WriteStrings(Utf8JsonWriter writer, string property, IEnumerable<string> values)
    {
        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null) writer.WriteNull(property);
        else writer.WriteString(property, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string property, int? value)
    {
        if (value is null) writer.WriteNull(property);
        else writer.WriteNumber(property, value.Value);
    }
}
