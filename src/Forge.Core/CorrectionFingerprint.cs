using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core;

public static class CorrectionFingerprint
{
    public static string ComputeContext(FailureAnalysisContext context) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", "forge-failure-analysis-context-v1");
        writer.WriteString("taskId", context.TaskId);
        writer.WriteString("failedAttemptId", context.FailedAttemptId);
        writer.WriteString("failedAttemptFingerprint", context.FailedAttemptFingerprint);
        WriteGuids(writer, "failedResultRevisionIds", context.FailedResultRevisionIds);
        writer.WriteString("verificationPlanId", context.VerificationPlanId);
        writer.WriteString("verificationPlanFingerprint", context.VerificationPlanFingerprint);
        writer.WriteString("implementationRevisionId", context.ImplementationRevisionId);
        writer.WriteString("implementationResultFingerprint", context.ImplementationResultFingerprint);
        writer.WriteString("approvedRequirementFingerprint", context.ApprovedRequirementFingerprint);
        writer.WriteString("approvedPlanFingerprint", context.ApprovedPlanFingerprint);
        writer.WriteString("originalBaseCommitSha", context.OriginalBaseCommitSha);
        writer.WritePropertyName("failureEvidence");
        writer.WriteStartArray();
        foreach (var evidence in context.FailureEvidence.OrderBy(item => item.ResultRevisionId))
        {
            writer.WriteStartObject();
            writer.WriteString("resultRevisionId", evidence.ResultRevisionId);
            writer.WriteString("testCaseId", evidence.TestCaseId);
            writer.WriteString("testCaseTitle", evidence.TestCaseTitle);
            writer.WriteString("result", evidence.Result.ToString());
            writer.WriteString("failureDetailsSha256", HashText(JsonSerializer.Serialize(evidence.FailureDetails)));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteOperations(writer, context.ApprovedOperations);
        writer.WriteString("createdAt", context.CreatedAt.ToUniversalTime());
        writer.WriteEndObject();
    });

    public static string ComputeAnalysis(Guid taskId, FailureAnalysis analysis) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", "forge-failure-analysis-v1");
        writer.WriteString("taskId", taskId);
        writer.WriteString("analysisId", analysis.AnalysisId);
        writer.WriteNumber("analysisNumber", analysis.AnalysisNumber);
        writer.WriteString("contextFingerprint", analysis.ContextFingerprint);
        writer.WriteString("generationCommandId", analysis.GenerationCommandId);
        writer.WriteString("failedAttemptId", analysis.FailedAttemptId);
        writer.WriteString("failedAttemptFingerprint", analysis.FailedAttemptFingerprint);
        WriteGuids(writer, "failureResultRevisionIds", analysis.FailureResultRevisionIds);
        writer.WriteString("verificationPlanId", analysis.VerificationPlanId);
        writer.WriteString("verificationPlanFingerprint", analysis.VerificationPlanFingerprint);
        writer.WriteString("implementationRevisionId", analysis.ImplementationRevisionId);
        writer.WriteString("implementationResultFingerprint", analysis.ImplementationResultFingerprint);
        writer.WriteString("approvedRequirementFingerprint", analysis.ApprovedRequirementFingerprint);
        writer.WriteString("approvedPlanFingerprint", analysis.ApprovedPlanFingerprint);
        writer.WriteString("originalBaseCommitSha", analysis.OriginalBaseCommitSha);
        writer.WriteString("classification", analysis.Classification.ToString());
        writer.WriteNumber("confidencePercent", analysis.ConfidencePercent);
        writer.WriteString("rootCauseSummary", analysis.RootCauseSummary);
        writer.WriteString("rationale", analysis.Rationale);
        WriteStrings(writer, "evidenceReferences", analysis.EvidenceReferences);
        WriteOperations(writer, analysis.AffectedApprovedOperations);
        writer.WriteString("correctionStrategy", analysis.CorrectionStrategy);
        writer.WriteString("expectedBehavior", analysis.ExpectedBehavior);
        writer.WriteString("verificationImpact", analysis.VerificationImpact);
        WriteStrings(writer, "risks", analysis.Risks);
        writer.WriteString("source", analysis.Source.ToString());
        writer.WriteString("model", analysis.Model);
        writer.WriteString("reasoningEffort", analysis.ReasoningEffort);
        WriteGuids(writer, "modelCallIds", analysis.ModelCallIds);
        writer.WriteString("status", analysis.Status.ToString());
        writer.WriteString("createdAt", analysis.CreatedAt.ToUniversalTime());
        writer.WriteEndObject();
    });

    public static string ComputeProposal(Guid taskId, CorrectionProposal proposal) => Hash(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("version", "forge-correction-proposal-v1");
        writer.WriteString("taskId", taskId);
        writer.WriteString("proposalId", proposal.ProposalId);
        writer.WriteNumber("proposalNumber", proposal.ProposalNumber);
        writer.WriteString("analysisId", proposal.AnalysisId);
        writer.WriteString("analysisFingerprint", proposal.AnalysisFingerprint);
        writer.WriteString("failedAttemptId", proposal.FailedAttemptId);
        writer.WriteString("failedAttemptFingerprint", proposal.FailedAttemptFingerprint);
        WriteGuids(writer, "failureResultRevisionIds", proposal.FailureResultRevisionIds);
        writer.WriteString("previousApprovedRevisionId", proposal.PreviousApprovedRevisionId);
        writer.WriteString("previousResultFingerprint", proposal.PreviousResultFingerprint);
        writer.WriteString("approvedRequirementFingerprint", proposal.ApprovedRequirementFingerprint);
        writer.WriteString("approvedPlanFingerprint", proposal.ApprovedPlanFingerprint);
        writer.WriteString("originalBaseCommitSha", proposal.OriginalBaseCommitSha);
        WriteOperations(writer, proposal.AffectedApprovedOperations);
        writer.WriteString("rootCauseSummary", proposal.RootCauseSummary);
        writer.WriteString("correctionStrategy", proposal.CorrectionStrategy);
        writer.WriteString("expectedBehavior", proposal.ExpectedBehavior);
        writer.WriteString("verificationImpact", proposal.VerificationImpact);
        WriteStrings(writer, "risks", proposal.Risks);
        writer.WriteString("createdAt", proposal.CreatedAt.ToUniversalTime());
        writer.WriteEndObject();
    });

    public static string ComputeOutput(ImplementationOutput output) =>
        HashText(JsonSerializer.Serialize(output));

    public static string ComputeApprovalCommandSemantic(Guid taskId, long expectedRowVersion,
        CorrectionProposal proposal) => HashText(string.Join("\n", new object?[]
        {
            taskId, expectedRowVersion, proposal.ProposalId, proposal.ProposalFingerprint,
            proposal.AnalysisId, proposal.AnalysisFingerprint, proposal.FailedAttemptId,
            proposal.FailedAttemptFingerprint, proposal.PreviousApprovedRevisionId,
            proposal.PreviousResultFingerprint, proposal.ApprovedRequirementFingerprint,
            proposal.ApprovedPlanFingerprint, proposal.OriginalBaseCommitSha
        }.Select(value => value?.ToString() ?? "NULL")));

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) { write(writer); writer.Flush(); }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void WriteOperations(Utf8JsonWriter writer, IEnumerable<ApprovedOperationReference> operations)
    {
        writer.WritePropertyName("affectedApprovedOperations");
        writer.WriteStartArray();
        foreach (var operation in operations.OrderBy(item => RepositoryPathRules.Normalize(item.Path), StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("path", RepositoryPathRules.Normalize(operation.Path));
            writer.WriteString("action", operation.Action.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteGuids(Utf8JsonWriter writer, string name, IEnumerable<Guid> values)
    {
        writer.WritePropertyName(name); writer.WriteStartArray();
        foreach (var value in values.Order()) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name); writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
