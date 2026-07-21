using System.Buffers;
using System.Globalization;
using System.Text;
using Forge.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Forge.Infrastructure;

public sealed class TaskPdfExporter(
    ModelCostResolver costResolver,
    TimeProvider? timeProvider = null) : IEngineeringTaskPdfExporter
{
    private const decimal PageTop = 800m;
    internal const decimal PageBottom = 48m;
    private const decimal Left = 48m;
    private const decimal BodyFontSize = 9.5m;
    internal const decimal BodyLineHeight = 13m;
    private const int BodyCharactersPerLine = 92;
    internal const int MaximumRenderedFieldCharacters = 50_000;
    internal const int MaximumSensitiveIdentityCharacters = 2_048;
    private const string FieldTruncationNotice = "[Text truncated by field limit]";
    internal static readonly int MaximumRenderedFieldContentCharacters =
        MaximumRenderedFieldCharacters - 1 - FieldTruncationNotice.Length;
    internal const int MaximumReportPages = 80;
    internal const int MaximumRenderedLines = 4_500;
    internal const int MaximumTotalRenderedCharacters = 300_000;
    internal const string AggregateTruncationNotice =
        "[Report truncated because the aggregate PDF resource limit was reached.]";
    private const int MaximumClarificationAnswers = 50;
    private const int MaximumRequirementRevisions = 25;
    private const int MaximumPlanRevisions = 25;
    private const int MaximumEvidenceEntries = 50;
    private const int MaximumAffectedFiles = 25;
    private const int MaximumImplementationSteps = 25;
    private const int MaximumRequirementCoverageEntries = 50;
    private const int MaximumRisks = 25;
    private const int MaximumAssumptions = 25;
    private const int MaximumUnresolvedQuestions = 25;
    private const int MaximumImplementationWarnings = 25;
    private const int MaximumChangedFiles = 25;
    private const int MaximumImplementationRevisions = 6;
    private const int MaximumModelCalls = 100;
    private const int MaximumJoinedValues = 100;
    private const int MaximumJoinedCharacters = 10_000;
    private const int MaximumJoinedContentCharacters = MaximumJoinedCharacters - 100;
    private static readonly string[] RecognizedUnixFilesystemRoots =
        ["/bin/", "/boot/", "/dev/", "/etc/", "/home/", "/lib/", "/mnt/", "/opt/", "/private/", "/root/", "/run/", "/srv/", "/tmp/", "/usr/", "/var/", "/Users/"];
    private static readonly HashSet<string> NaturalLanguagePathTerminators = new(StringComparer.OrdinalIgnoreCase)
        { "and", "or", "but", "then", "because", "with", "without", "followed", "do", "should", "must", "before", "after", "carefully", "review" };
    private const string HistoricalSummaryNote =
        "Any development note inside the approved requirement summary describes the state at summary-generation time. Later repository analysis, planning, and implementation stages are reported below.";

    public byte[] Export(EngineeringTask task, ImplementationReportRuntimeStatus? runtimeStatus = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var writer = new FlowWriter(builder, regular, bold, SensitiveIdentities(task), task.ImplementationWorkspace?.Token);

        writer.Title("Forge AI Engineering Task Report");
        writer.Section("Report metadata");
        writer.Field("Task ID", task.Id.ToString());
        writer.Field("Workflow status", task.Status.ToString());
        writer.Field("Exported-at UTC", FormatTimestamp((timeProvider ?? TimeProvider.System).GetUtcNow()));
        writer.Field("Repository", RepositoryDisplayIdentifier.Create(task.Repository));
        writer.Field("Created at", FormatTimestamp(task.CreatedAt));
        writer.Field("Updated at", FormatTimestamp(task.UpdatedAt));

        WriteChronology(writer, task);

        writer.Section("Original requirement");
        writer.Body(task.OriginalRequirement);

        writer.Section("Clarification questions and answers");
        if (task.ClarificationAnswers.Count == 0)
        {
            writer.Body("No clarification questions were recorded.");
        }
        else
        {
            var displayed = Math.Min(task.ClarificationAnswers.Count, MaximumClarificationAnswers);
            for (var index = 0; index < displayed; index++)
            {
                var answer = task.ClarificationAnswers[index];
                writer.Body($"Question {index + 1}: {answer.Question}");
                writer.Body($"Answer {index + 1}: {answer.Answer}");
                writer.Space(3);
            }
            writer.OmittedEntries(task.ClarificationAnswers.Count - displayed);
        }

        WriteRevisionHistory(writer, task);

        writer.Section("Approved requirement summary");
        writer.Body(task.RequirementSummary ?? "No approved requirement summary is available.");
        if (task.RequirementSummary is not null) writer.Body(HistoricalSummaryNote);

        if (task.RepositorySnapshot is { } snapshot) WriteRepositoryAnalysis(writer, task, snapshot);
        if (task.ImplementationPlan is { } plan) WriteImplementationPlan(writer, task, plan);
        if (task.ImplementationRevisions.Count > 0) WriteImplementationRevisionChronology(writer, task);
        if (HasImplementationAttempt(task, runtimeStatus)) WriteImplementationAttempt(writer, task, runtimeStatus);
        if (task.ImplementationResult is { } result) WriteImplementationReview(writer, task, result);
        if (task.VerificationPlans.Count > 0 || task.VerificationPlanGenerationAttempts.Count > 0)
            WriteVerificationChronology(writer, task);

        writer.Section("Model-call usage and estimated cost");
        writer.Field("Model-call count", task.ModelCalls.Count.ToString(CultureInfo.InvariantCulture));
        var displayedCalls = Math.Min(task.ModelCalls.Count, MaximumModelCalls);
        for (var index = 0; index < displayedCalls; index++)
            WriteCall(writer, task.ModelCalls[index], index + 1);
        writer.OmittedEntries(task.ModelCalls.Count - displayedCalls);

        var total = ResolveBoundedTotal(task.ModelCalls, displayedCalls);
        writer.Section("Task cost estimate");
        writer.Field("Available estimated subtotal", total.AvailableEstimatedSubtotalUsd is { } available
            ? FormatCost(available)
            : "unavailable");
        writer.Field("Complete estimated subtotal", total.CompleteEstimatedSubtotalUsd is { } complete
            ? FormatCost(complete)
            : "unavailable");
        writer.Field("Partial conservative estimated subtotal", total.PartialEstimatedSubtotalUsd is { } partial
            ? FormatCost(partial)
            : "unavailable");
        writer.Field("Combined available estimated subtotal", total.AvailableEstimatedSubtotalUsd is { } estimated
            ? FormatCost(estimated)
            : "unavailable");
        writer.Body(task.ModelCalls.Count == 0
            ? "No model calls were recorded for this task."
            : CostSummary(total));
        writer.Body("All monetary values are estimates, not invoices.");

        return builder.Build();
    }

    private BoundedTaskCost ResolveBoundedTotal(IReadOnlyList<ModelCallRecord> calls, int displayedCalls)
    {
        var resolved = costResolver.ResolveTotal(calls.Take(displayedCalls));
        return new BoundedTaskCost(
            resolved.TotalEstimatedCostUsd,
            resolved.AvailableCallCount,
            resolved.UnavailableCallCount,
            calls.Count - displayedCalls,
            resolved.Overflowed,
            resolved.CompleteEstimatedSubtotalUsd,
            resolved.PartialEstimatedSubtotalUsd,
            resolved.AvailableEstimatedSubtotalUsd,
            resolved.HasPartialEstimates);
    }

    private static string CostSummary(BoundedTaskCost total)
    {
        if (total.Overflowed)
        {
            var omitted = total.OmittedCallCount > 0
                ? $"\n{total.OmittedCallCount} additional model call(s) were omitted by the report limit."
                : string.Empty;
            return "The bounded estimated total is unavailable because its numeric range was exceeded." + omitted;
        }
        if (total.AvailableCallCount == 0 && total.UnavailableCallCount > 0)
            return "Estimated cost is unavailable for all displayed model calls.";
        if (total.HasPartialEstimates || total.UnavailableCallCount > 0 || total.OmittedCallCount > 0)
            return $"This is a partial estimate. {total.UnavailableCallCount} displayed model call(s) had unavailable cost; " +
                   $"{total.OmittedCallCount} additional model call(s) were omitted by the report limit.";
        return "All recorded model calls have an available estimated cost.";
    }

    private sealed record BoundedTaskCost(
        decimal? TotalEstimatedCostUsd,
        int AvailableCallCount,
        int UnavailableCallCount,
        int OmittedCallCount,
        bool Overflowed,
        decimal? CompleteEstimatedSubtotalUsd,
        decimal? PartialEstimatedSubtotalUsd,
        decimal? AvailableEstimatedSubtotalUsd,
        bool HasPartialEstimates);

    private static void WriteChronology(FlowWriter writer, EngineeringTask task)
    {
        writer.Section("Workflow chronology");
        writer.Field("Requirement summary generated", Recorded(task.RequirementSummary is not null));
        writer.Field("Requirement approved", RecordedAt(task.RequirementApprovedAt));
        writer.Field("Repository analysed", RecordedAt(task.RepositoryAnalyzedAt));
        writer.Field("Plan created", RecordedAt(task.PlanCreatedAt));
        writer.Field("Plan approved", RecordedAt(task.PlanApprovedAt));
        writer.Field("Implementation started", RecordedAt(task.ImplementationStartedAt));
        writer.Field("Implementation completed", RecordedAt(task.ImplementationCompletedAt));
        var approved = task.ImplementationRevisions.SingleOrDefault(revision =>
            revision.RevisionId == task.ApprovedImplementationRevisionId);
        writer.Field("Implementation approved", RecordedAt(approved?.ApprovedAt));
        writer.Field("Verification plan generated", RecordedAt(task.VerificationPlans.LastOrDefault()?.GeneratedAt));
        var currentAttempt = task.CurrentVerificationAttemptId is { } attemptId
            ? task.ManualVerificationAttempts.SingleOrDefault(attempt => attempt.AttemptId == attemptId)
            : null;
        writer.Field("Manual verification started", RecordedAt(currentAttempt?.StartedAt));
        writer.Field("Manual verification completed", RecordedAt(currentAttempt?.CompletedAt));
        writer.Field("Ready for delivery", task.Status == WorkflowStatus.ReadyForDelivery ? "recorded" : "not recorded");
    }

    private static void WriteVerificationChronology(FlowWriter writer, EngineeringTask task)
    {
        writer.Section("Manual verification chronology");
        writer.Banner(VerificationTrustLabels.ManualNotExecuted);
        foreach (var generation in task.VerificationPlanGenerationAttempts.Take(18))
        {
            writer.Subsection($"Verification-plan generation — {generation.Status}");
            writer.Field("Started at", FormatTimestamp(generation.StartedAt));
            writer.Field("Lease expires at", FormatTimestamp(generation.LeaseExpiresAt));
            writer.Field("Logical model-call attempts", generation.LogicalCallCount.ToString(CultureInfo.InvariantCulture));
            writer.Field("Definite physical requests", generation.PhysicalRequestCount.ToString(CultureInfo.InvariantCulture));
            writer.Field("Possibly dispatched requests", generation.PossiblyDispatchedRequestCount.ToString(CultureInfo.InvariantCulture));
            var definitelyUndispatched = generation.ModelCallIds.Count(callId => task.ModelCalls.Any(call =>
                call.Id == callId && call.VerificationDispatchDisposition ==
                    VerificationCallDispatchDisposition.DefinitelyNotDispatched));
            writer.Field("Definitely undispatched attempts", definitelyUndispatched.ToString(CultureInfo.InvariantCulture));
            foreach (var response in generation.ProviderResponses.Take(2))
            {
                writer.Field("Provider response ID", response.ProviderResponseId ?? "unavailable");
                writer.Field("Provider request ID", response.ProviderRequestId ?? "unavailable");
                writer.Field("Provider response status", response.Status.ToString());
                writer.Field("Provider HTTP status", response.HttpStatusCode.ToString(CultureInfo.InvariantCulture));
                writer.Field("Logical call started", FormatTimestamp(response.StartedAt));
                writer.Field("Provider response received", FormatTimestamp(response.ReceivedAt));
                writer.Field("Provider usage", response.EffectiveUsageAvailability.ToString());
                writer.Field("Input tokens", FormatTokens(response.InputTokens));
                writer.Field("Cached-input tokens", FormatTokens(response.CachedInputTokens));
                writer.Field("Output tokens", FormatTokens(response.OutputTokens));
                writer.Field("Reasoning tokens", FormatTokens(response.ReasoningTokens));
            }
        }
        foreach (var plan in task.VerificationPlans.Take(6))
        {
            writer.Subsection($"Verification plan {plan.PlanNumber} — {VerificationTrustLabels.ForgeGenerated}");
            writer.Field("Status", plan.Status.ToString());
            writer.Field("Implementation revision", plan.ImplementationRevisionId.ToString("D"));
            writer.Field("Implementation result fingerprint", plan.ImplementationResultFingerprint);
            writer.Field("Plan fingerprint", plan.PlanFingerprint);
            writer.Field("Summary", plan.Summary);
            writer.Field("Required cases", plan.TestCases.Count(testCase => testCase.IsRequired).ToString(CultureInfo.InvariantCulture));
        }
        foreach (var attempt in task.ManualVerificationAttempts.Take(18))
        {
            writer.Subsection($"Manual attempt {attempt.AttemptNumber} — {VerificationTrustLabels.UserReported}");
            writer.Field("Status", attempt.Status.ToString());
            writer.Field("Plan fingerprint", attempt.VerificationPlanFingerprint);
            writer.Field("Started", FormatTimestamp(attempt.StartedAt));
            writer.Field("Completed", attempt.CompletedAt is { } completed ? FormatTimestamp(completed) : "not recorded");
            writer.Field("Human confirmation", attempt.CompletionConfirmation == true ? "confirmed by user" : "not recorded");
            writer.Field("Summary", attempt.Summary ?? "not recorded");
            foreach (var result in attempt.ResultRevisions.Take(120))
            {
                writer.Body($"Case {result.TestCaseId:D}, revision {result.RevisionNumber}: {result.Result} — {VerificationTrustLabels.UserReported}");
                if (!string.IsNullOrWhiteSpace(result.ActualResult)) writer.Field("Actual result", result.ActualResult);
                if (!string.IsNullOrWhiteSpace(result.Notes)) writer.Field("Notes", result.Notes);
                foreach (var evidence in result.EvidenceDescriptions) writer.Field("Evidence description", evidence);
                if (result.FailureDetails is { } failure)
                {
                    writer.Field("Failure title", failure.Title);
                    writer.Field("Expected", failure.ExpectedResult);
                    writer.Field("Actual", failure.ActualResult);
                    writer.Field("Severity", failure.Severity.ToString());
                    if (!string.IsNullOrWhiteSpace(failure.ErrorMessage)) writer.Field("Reported error", failure.ErrorMessage);
                }
            }
        }
        if (task.Status == WorkflowStatus.ReadyForDelivery)
            writer.Body("Manual verification passed — user reported. ReadyForDelivery does not mean committed, pushed, or submitted as a pull request.");
        else if (task.Status == WorkflowStatus.ManualVerificationFailed)
            writer.Body("Manual verification failed or was blocked — user reported. Failure analysis and correction are not available in this slice.");
    }

    private static void WriteImplementationRevisionChronology(FlowWriter writer, EngineeringTask task)
    {
        writer.Section("Implementation revision chronology", FlowElementKind.SubsectionWithBody);
        WriteCollection(writer, task.ImplementationRevisions, MaximumImplementationRevisions, (revision, _) =>
        {
            var labels = new List<string>();
            if (task.ActiveImplementationRevisionId == revision.RevisionId) labels.Add("CURRENT");
            if (task.ApprovedImplementationRevisionId == revision.RevisionId) labels.Add("APPROVED");
            writer.Subsection($"Implementation revision {revision.RevisionNumber}" +
                              (labels.Count == 0 ? string.Empty : $" — {string.Join(" / ", labels)}"));
            writer.Field("Kind", revision.Kind.ToString());
            writer.Field("Approved plan fingerprint", revision.PlanFingerprint);
            writer.Field("Base commit SHA", revision.BaseCommitSha);
            writer.Field("Generation started", FormatTimestamp(revision.GenerationStartedAt));
            writer.Field("Generation completed", revision.GenerationCompletedAt is { } completed
                ? FormatTimestamp(completed)
                : "not recorded");
            writer.Field("Generation disposition", revision.GenerationState.ToString());
            writer.Field("Review disposition", revision.ReviewState.ToString());
            writer.Field("Persisted result fingerprint", revision.ResultFingerprint ?? "not recorded");
            writer.Field("Changed-file count", FormatNumber(revision.Result?.ChangedFiles.Count ?? 0));
            writer.Field("Approved at", revision.ApprovedAt is { } approved
                ? FormatTimestamp(approved)
                : "not approved");
            if (revision.Failure is { } failure)
            {
                writer.Field("Safe failure category", failure.Category);
                writer.Field("Safe failure message", failure.Message);
            }
        });
        if (task.Status == WorkflowStatus.ImplementationApproved)
            writer.Body("Implementation approval applies to the exact persisted review evidence above. It did not run validation or verify current physical workspace availability.");
    }

    private static void WriteRevisionHistory(FlowWriter writer, EngineeringTask task)
    {
        if (task.RequirementRevisionNotes.Count > 0)
        {
            writer.Section("Requirement revision history", FlowElementKind.SubsectionWithBody);
            WriteCollection(writer, task.RequirementRevisionNotes, MaximumRequirementRevisions, (revision, index) =>
            {
                writer.Subsection($"Requirement revision {index + 1}");
                writer.Field("Submitted at", FormatTimestamp(revision.SubmittedAt));
                writer.Field("Correction request", revision.Correction);
                writer.Field("Outcome", revision.Outcome.ToString());
                writer.Field("Resolved at", revision.ResolvedAt is { } resolved ? FormatTimestamp(resolved) : "not recorded");
                writer.Field("Historical note", revision.StatusNote ?? "No durable resolution note was recorded.");
                writer.Field("Previous requirement summary", revision.PreviousSummary);
            });
        }

        if (task.PlanRevisionNotes.Count > 0)
        {
            writer.Section("Plan revision history", FlowElementKind.SubsectionWithBody);
            WriteCollection(writer, task.PlanRevisionNotes, MaximumPlanRevisions, (revision, index) =>
            {
                writer.Subsection($"Plan revision {index + 1}");
                writer.Field("Submitted at", FormatTimestamp(revision.SubmittedAt));
                writer.Field("Correction request", revision.Correction);
                writer.Field("Outcome", revision.Outcome.ToString());
                writer.Field("Resolved at", revision.ResolvedAt is { } resolved ? FormatTimestamp(resolved) : "not recorded");
                writer.Field("Historical note", revision.StatusNote ?? "No durable resolution note was recorded.");
                writer.Field("Previous plan title", revision.PreviousPlanTitle);
                writer.Field("Previous repository fingerprint", revision.PreviousRepositoryFingerprint);
            });
        }
    }

    private static bool HasImplementationAttempt(
        EngineeringTask task,
        ImplementationReportRuntimeStatus? runtimeStatus) =>
        task.ImplementationStartedAt is not null || task.ImplementationWorkspace is not null ||
        task.ImplementationLease is not null || task.LastImplementationFailure is not null || runtimeStatus is not null ||
        task.ImplementationResult is not null;

    private static void WriteImplementationAttempt(
        FlowWriter writer,
        EngineeringTask task,
        ImplementationReportRuntimeStatus? runtimeStatus)
    {
        writer.Section("Implementation attempt");
        writer.Field("Started at", task.ImplementationStartedAt is { } started ? FormatTimestamp(started) : "not recorded");
        writer.Field("Workspace phase", task.ImplementationWorkspace?.Phase.ToString() ?? "not recorded");
        writer.Field("Attempt disposition", runtimeStatus?.Disposition.ToString() ?? PersistedDisposition(task));
        if (task.LastImplementationFailure is { } failure)
        {
            writer.Field("Failure category", failure.Category);
            writer.Field("Failure timestamp", FormatTimestamp(failure.OccurredAt));
            writer.Field("Recovery required", YesNo(failure.RecoveryRequired));
            writer.Field("Safe to resume", YesNo(failure.SafeToResume));
            writer.Field("Failure note", failure.Message);
        }
        else
        {
            writer.Field("Persisted failure", "none recorded");
        }

        writer.Field("Active checkout verified when implementation completed", CompletionEvidence(task));
        writer.Field("Valid non-reparse isolated worktree metadata observed at export time",
            runtimeStatus is null ? "not observed" : YesNo(runtimeStatus.WorkspaceObservedAvailable));
        if (!string.IsNullOrWhiteSpace(runtimeStatus?.SafeMessage))
            writer.Field("Read-only export-time observation", runtimeStatus.SafeMessage);
        if (task.ImplementationResult is null) writer.Body("No implementation result was persisted.");
    }

    private static string PersistedDisposition(EngineeringTask task) => task.LastImplementationFailure switch
    {
        { RecoveryRequired: true } => ImplementationAttemptDisposition.RecoveryRequired.ToString(),
        { SafeToResume: false } => ImplementationAttemptDisposition.TerminalIncompatible.ToString(),
        _ when task.ImplementationLease is not null => ImplementationAttemptDisposition.Active.ToString(),
        _ => "not observed"
    };

    private static string CompletionEvidence(EngineeringTask task)
    {
        if (task.ImplementationResult is { } result)
            return CompletionMetadataRecorded(result) ? YesNo(result.ActiveCheckoutVerified) : "not recorded";
        if (task.LastImplementationFailure is { ActiveCheckoutVerified: false }) return "no";
        return "not recorded";
    }

    private static bool CompletionMetadataRecorded(ImplementationResult result) =>
        !string.IsNullOrWhiteSpace(result.WorktreeFingerprint);

    private static void WriteRepositoryAnalysis(
        FlowWriter writer,
        EngineeringTask task,
        RepositorySnapshot snapshot)
    {
        writer.Section("Repository analysis");
        writer.Field("Analysed at", FormatTimestamp(task.RepositoryAnalyzedAt ?? snapshot.AnalyzedAt));
        writer.Field("Branch", snapshot.Branch ?? "unavailable");
        writer.Field("Full HEAD SHA", snapshot.FullHeadSha ?? "unavailable");
        writer.Field("Working-tree state at analysis", snapshot.WorkingTreeStatus);
        writer.Field("Discovered files", FormatNumber(snapshot.TotalDiscoveredFiles));
        writer.Field("Eligible text files", FormatNumber(snapshot.EligibleTextFileCount));
        writer.Field("Excluded files", FormatNumber(snapshot.ExcludedFileCount));
        writer.Field("Detected languages", Join(snapshot.DetectedLanguages));
        writer.Field("Detected extensions", Join(snapshot.DetectedExtensions));
        writer.Field("Selected-evidence count", FormatNumber(task.EvidenceFilesSelected));
        writer.Field("Selected-evidence characters", FormatNumber(task.TotalEvidenceCharacters));
        writer.Field("Files inspected for evidence", FormatNumber(task.EvidenceFilesInspected));
        writer.Subsection("Snapshot warnings");
        WriteStrings(writer, snapshot.Warnings, MaximumImplementationWarnings);

        writer.Subsection("Selected evidence metadata");
        if (task.EvidenceItems.Count == 0)
        {
            writer.Body("No selected evidence metadata is available.");
            return;
        }

        var displayedEvidence = Math.Min(task.EvidenceItems.Count, MaximumEvidenceEntries);
        for (var index = 0; index < displayedEvidence; index++)
        {
            var evidence = task.EvidenceItems[index];
            writer.Subsection($"Evidence {index + 1}: {evidence.Id}");
            writer.Field("Relative path", evidence.RelativePath);
            writer.Field("Line range", $"{evidence.StartLine}-{evidence.EndLine}");
            writer.Field("Selection reason", evidence.ReasonSelected);
            writer.Field("Score", evidence.Score.ToString(CultureInfo.InvariantCulture));
            writer.Field("Content hash", evidence.ContentHash);
        }
        writer.OmittedEntries(task.EvidenceItems.Count - displayedEvidence);
        writer.Body("Complete evidence excerpts are intentionally omitted from this report.");
    }

    private static void WriteImplementationPlan(
        FlowWriter writer,
        EngineeringTask task,
        ImplementationPlan plan)
    {
        var label = task.PlanApprovedAt is null ? "PROPOSED — NOT APPROVED" : "APPROVED";
        writer.Section("Implementation plan", FlowElementKind.BannerWithBody);
        writer.Banner(label);
        writer.Field("Plan approval status", label);
        writer.Field("Title", plan.Title);
        writer.Field("Source", plan.Source.ToString());
        writer.Field("Model", plan.PlanningModel ?? "none (deterministic Fake plan)");
        writer.Field("Created at", FormatTimestamp(task.PlanCreatedAt ?? plan.CreatedAt));
        writer.Field("Approved at", task.PlanApprovedAt is { } approved ? FormatTimestamp(approved) : "not approved");
        writer.Field("Objective", plan.Objective);
        writer.Field("Repository understanding", plan.RepositoryUnderstanding);
        writer.Field("Repository fingerprint", plan.RepositoryFingerprint);

        writer.Subsection("Affected files", FlowElementKind.SubsectionWithBody);
        WriteCollection(writer, plan.AffectedFiles, MaximumAffectedFiles, (file, index) =>
        {
            writer.Subsection($"Affected file {index + 1}: {file.Path}");
            writer.Field("Action", file.Action.ToString());
            writer.Field("Purpose", file.Purpose);
            writer.Field("Confidence", file.Confidence.ToString("P0", CultureInfo.InvariantCulture));
            writer.Field("Evidence IDs", Join(file.EvidenceIds));
        });

        writer.Subsection("Ordered implementation steps", FlowElementKind.SubsectionWithBody);
        WriteCollection(writer, plan.Steps, MaximumImplementationSteps, (step, _) =>
        {
            writer.Subsection($"Step {step.Order}");
            writer.Field("Description", step.Description);
            writer.Field("Expected result", step.ExpectedResult);
            writer.Field("Affected paths", Join(step.AffectedPaths));
            writer.Field("Evidence IDs", Join(step.EvidenceIds));
        });

        writer.Subsection("Requirement coverage", FlowElementKind.SubsectionWithBody);
        WriteCollection(writer, plan.RequirementCoverage, MaximumRequirementCoverageEntries, (coverage, index) =>
        {
            writer.Subsection($"Coverage {index + 1}");
            writer.Field("Requirement", coverage.Requirement);
            writer.Field("Affected paths", Join(coverage.AffectedPaths));
            writer.Field("Step orders", Join(coverage.StepOrders));
        });

        writer.Subsection("Proposed validation commands — NOT EXECUTED");
        WriteStrings(writer, plan.ProposedValidationCommands, MaximumImplementationSteps,
            value => $"NOT EXECUTED: {value}");
        writer.Subsection("Risks");
        WriteStrings(writer, plan.Risks, MaximumRisks);
        writer.Subsection("Assumptions");
        WriteStrings(writer, plan.Assumptions, MaximumAssumptions);
        writer.Subsection("Unresolved questions");
        WriteStrings(writer, plan.UnresolvedQuestions, MaximumUnresolvedQuestions);
        writer.Subsection("Plan summary");
        writer.Body(plan.Summary);
    }

    private static void WriteImplementationReview(
        FlowWriter writer,
        EngineeringTask task,
        ImplementationResult result)
    {
        writer.Section("Implementation review");
        if (task.Status == WorkflowStatus.ImplementationApproved)
            writer.Body("APPROVED PERSISTED IMPLEMENTATION REVIEW — validation was not run and no files were staged, committed, pushed, or submitted as a pull request.");
        else if (!result.ActiveCheckoutVerified)
            writer.Body("NOT ELIGIBLE FOR APPROVAL — Forge could not verify that the active checkout remained unchanged when implementation completed.");
        writer.Field("Implementation source", result.Source.ToString());
        writer.Field("Model", result.Model ?? "none (deterministic Fake implementation)");
        if (result.Source == ImplementationSource.OpenAI)
        {
            writer.Body("Provider output was treated as untrusted until strict parsing and deterministic validation completed.");
            writer.Field("Deterministic operation validation", "accepted");
        }
        writer.Field("Base commit SHA", result.BaseCommitSha);
        writer.Field("Isolated task branch", ImplementationBranchDisplay.Format(result.Branch));
        writer.Field("Completed at", FormatTimestamp(result.CompletedAt));
        writer.Field("Implementation summary", result.Summary);
        writer.Subsection("Implementation warnings");
        WriteStrings(writer, result.Warnings, MaximumImplementationWarnings);
        writer.Field("Full diff characters", FormatNumber(result.FullDiffCharacters));
        writer.Field("Displayed diff characters", FormatNumber(result.DisplayedDiffCharacters));
        writer.Field("Full diff UTF-8 bytes", FormatNumber(result.FullDiffUtf8Bytes));
        writer.Field("Displayed diff UTF-8 bytes", FormatNumber(result.DisplayedDiffUtf8Bytes));
        writer.Field("Overall diff truncated", YesNo(result.DiffTruncated));
        writer.Field("Active checkout verified when implementation completed", CompletionEvidence(task));
        writer.Field("Isolated worktree file count", CompletionMetadataRecorded(result)
            ? FormatNumber(result.WorktreeFileCount)
            : "not recorded");
        writer.Field("Isolated worktree byte count", CompletionMetadataRecorded(result)
            ? FormatNumber(result.WorktreeBytes)
            : "not recorded");

        writer.Subsection("Changed-file review");
        writer.Body("Line counts preserve Forge's split-style semantics; a trailing newline may contribute a final empty line.");
        WriteCollection(writer, result.ChangedFiles, MaximumChangedFiles, (file, index) =>
        {
            writer.Subsection($"Changed file {index + 1}: {file.Path}");
            writer.Field("Action", file.Action.ToString());
            writer.Field("Additions", FormatNumber(file.Additions));
            writer.Field("Deletions", FormatNumber(file.Deletions));
            writer.Field("Original SHA-256", file.OriginalContentSha256 ?? "not applicable");
            writer.Field("Generated SHA-256", file.NewContentSha256 ?? "not applicable");
            writer.Field("Original bytes", FormatNumber(file.OriginalBytes));
            writer.Field("Generated bytes", FormatNumber(file.NewBytes));
            writer.Field("Original lines", FormatNumber(file.OriginalLines));
            writer.Field("Generated lines", FormatNumber(file.NewLines));
            writer.Field("Full diff characters", FormatNumber(file.FullDiffCharacters));
            writer.Field("Displayed diff characters", FormatNumber(file.DisplayedDiffCharacters));
            writer.Field("Full diff UTF-8 bytes", FormatNumber(file.FullDiffUtf8Bytes));
            writer.Field("Displayed diff UTF-8 bytes", FormatNumber(file.DisplayedDiffUtf8Bytes));
            writer.Field("Diff truncated", YesNo(file.DiffTruncated));
            writer.Field("Bounded diff preview", file.DiffPreview);
        });

        writer.Subsection("Capability boundary");
        writer.Body("This implementation slice records no target build or test execution, staging, commit, push, or pull-request creation. Proposed plan commands remain unexecuted and are not external validation evidence.");
    }

    internal static string NormalizeSupportedText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\u2018", "'", StringComparison.Ordinal).Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("\u201c", "\"", StringComparison.Ordinal).Replace("\u201d", "\"", StringComparison.Ordinal)
            .Replace("\u2013", "-", StringComparison.Ordinal)
            .Replace("\u2026", "...", StringComparison.Ordinal).Replace("\u2192", "->", StringComparison.Ordinal);
        var normalizedResult = new StringBuilder(normalized.Length);
        var span = normalized.AsSpan();
        while (!span.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(span, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                normalizedResult.Append('?');
                break;
            }
            if (rune.Value is '\n' or '\t' or 0x2014 || rune.Value is >= 32 and <= 126)
                normalizedResult.Append(rune.ToString());
            else
                normalizedResult.Append('?');
            span = span[consumed..];
        }
        return normalizedResult.ToString();
    }

    internal static IReadOnlyList<string> Wrap(string? value, int maximumCharacters)
    {
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var result = new List<string>();
        foreach (var paragraph in NormalizeSupportedText(value).Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                result.Add(string.Empty);
                continue;
            }
            var remaining = paragraph.TrimEnd();
            while (remaining.Length > maximumCharacters)
            {
                var split = remaining.LastIndexOf(' ', maximumCharacters, maximumCharacters);
                if (split <= 0) split = maximumCharacters;
                result.Add(remaining[..split].TrimEnd());
                remaining = remaining[split..].TrimStart();
            }
            result.Add(remaining);
        }
        return result.Count == 0 ? [string.Empty] : result;
    }

    internal static string RedactAbsoluteLocalPaths(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        StringBuilder? result = null;
        var copiedThrough = 0;
        var index = 0;
        while (index < value.Length)
        {
            var replacementStart = index;
            var pathStart = index;
            char? quote = null;
            if (value[index] is '\'' or '"' && index + 1 < value.Length &&
                IsAbsoluteLocalPathStart(value, index + 1))
            {
                quote = value[index];
                pathStart = index + 1;
            }
            else if (!IsAbsoluteLocalPathStart(value, index))
            {
                index++;
                continue;
            }

            var pathEnd = FindAbsoluteLocalPathEnd(value, pathStart, quote);
            if (pathEnd <= pathStart)
            {
                index++;
                continue;
            }
            var replacementEnd = quote is not null && pathEnd < value.Length && value[pathEnd] == quote
                ? pathEnd + 1
                : pathEnd;
            result ??= new StringBuilder(Math.Min(value.Length, MaximumRenderedFieldCharacters));
            result.Append(value, copiedThrough, replacementStart - copiedThrough);
            result.Append("[absolute-local-path-omitted]");
            copiedThrough = replacementEnd;
            index = replacementEnd;
        }
        if (result is null) return value;
        result.Append(value, copiedThrough, value.Length - copiedThrough);
        return result.ToString();
    }

    private static bool IsAbsoluteLocalPathStart(string value, int index)
    {
        if (index < 0 || index >= value.Length || !HasPathTokenBoundary(value, index)) return false;
        if (index + 2 < value.Length && char.IsAsciiLetter(value[index]) && value[index + 1] == ':' &&
            IsDirectorySeparator(value[index + 2])) return true;
        if (index + 1 < value.Length && IsDirectorySeparator(value[index]) &&
            IsDirectorySeparator(value[index + 1]))
            return (index == 0 || value[index - 1] != ':') && LooksLikeUncPath(value, index + 2);
        if (value[index] != '/') return false;
        return RecognizedUnixFilesystemRoots.Any(root =>
            value.AsSpan(index).StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasPathTokenBoundary(string value, int index)
    {
        if (index == 0) return true;
        var previous = value[index - 1];
        return !char.IsLetterOrDigit(previous) && previous is not '_' and not '.' and not ':' and not '/' and not '\\';
    }

    private static bool LooksLikeUncPath(string value, int index)
    {
        var hostCharacters = 0;
        for (; index < value.Length; index++)
        {
            if (IsDirectorySeparator(value[index])) return hostCharacters > 0;
            if (char.IsWhiteSpace(value[index]) || value[index] is ',' or ';' or '"' or '\'') return false;
            hostCharacters++;
        }
        return false;
    }

    private static int FindAbsoluteLocalPathEnd(string value, int start, char? quote)
    {
        var parentheses = 0;
        var index = start;
        while (index < value.Length)
        {
            var character = value[index];
            if (quote is not null)
            {
                if (character == quote && !IsEscapedQuote(value, index)) return index;
                if (character is '\r' or '\n') return index;
                index++;
                continue;
            }
            if (index > start && char.IsWhiteSpace(value[index - 1]) &&
                IsAbsoluteLocalPathStart(value, index)) break;
            if (character is '\r' or '\n' or ',' or ';' or ']' or '}' or '"' or '\'') break;
            if (character == '(') { parentheses++; index++; continue; }
            if (character == ')')
            {
                if (parentheses == 0) break;
                parentheses--;
                index++;
                continue;
            }
            if (character is '!' or '?') break;
            if (character == '.' && IsSentenceTerminator(value, index)) break;
            if (char.IsWhiteSpace(character) && parentheses == 0 &&
                !ShouldContinuePathAcrossWhitespace(value, index)) break;
            index++;
        }
        while (index > start && char.IsWhiteSpace(value[index - 1])) index--;
        return index;
    }

    private static bool IsSentenceTerminator(string value, int index) =>
        index + 1 == value.Length || char.IsWhiteSpace(value[index + 1]) || value[index + 1] is ')' or ']' or '}';

    private static bool ShouldContinuePathAcrossWhitespace(string value, int whitespaceIndex)
    {
        const int maximumLookaheadCharacters = 256;
        const int maximumFinalFilenameCharacters = 96;
        const int maximumFinalFilenameTokens = 2;
        var start = whitespaceIndex;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        if (start >= value.Length) return false;

        var limit = Math.Min(value.Length, checked(start + maximumLookaheadCharacters));
        var candidateEnd = limit;
        for (var index = start; index < limit; index++)
        {
            var character = value[index];
            if ((index == start || char.IsWhiteSpace(value[index - 1])) &&
                IsAbsoluteLocalPathStart(value, index)) return false;
            if (IsPathLookaheadHardDelimiter(value, index))
            {
                candidateEnd = index;
                break;
            }
            if (IsDirectorySeparator(character)) return true;
        }

        candidateEnd = Math.Min(candidateEnd, checked(start + maximumFinalFilenameCharacters));
        var tokens = value.AsSpan(start, Math.Max(0, candidateEnd - start))
            .Trim().ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        var displayed = Math.Min(tokens.Length, maximumFinalFilenameTokens);
        for (var index = 0; index < displayed; index++)
        {
            var token = tokens[index];
            if (NaturalLanguagePathTerminators.Contains(token) || !IsPlausibleFilenameToken(token)) return false;
            if (HasFilenameExtension(token.AsSpan())) return true;
        }
        return false;
    }

    private static bool IsPathLookaheadHardDelimiter(string value, int index)
    {
        var character = value[index];
        return character is '\r' or '\n' or ',' or ';' or ']' or '}' or '"' or '\'' or '!' or '?' or ':' ||
               character == '.' && IsSentenceTerminator(value, index);
    }

    private static bool IsPlausibleFilenameToken(string token) =>
        token.Length is > 0 and <= 64 && token.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '(' or ')');

    private static bool HasFilenameExtension(ReadOnlySpan<char> token)
    {
        token = token.Trim();
        var separator = Math.Max(token.LastIndexOf('/'), token.LastIndexOf('\\'));
        var dot = token.LastIndexOf('.');
        return dot > separator + 1 && dot + 1 < token.Length &&
               token[(dot + 1)..].IndexOfAny(" \t()") < 0;
    }

    private static bool IsEscapedQuote(string value, int index)
    {
        var backslashes = 0;
        for (var cursor = index - 1; cursor >= 0 && value[cursor] == '\\'; cursor--) backslashes++;
        return backslashes % 2 != 0;
    }

    private static bool IsDirectorySeparator(char value) => value is '/' or '\\';

    private void WriteCall(FlowWriter writer, ModelCallRecord call, int number)
    {
        var resolved = costResolver.Resolve(call);
        var usageAvailable = ModelCallUsageEvidence.IsAvailable(call);
        var verificationUsage = call.Stage == ModelCallStage.VerificationPlanning
            ? call.ProviderUsageAvailability ?? VerificationUsage.Classify(call.InputTokens,
                call.CachedInputTokens, call.OutputTokens, call.ReasoningTokens)
            : (VerificationUsageAvailability?)null;
        writer.Subsection($"Call {number}: {call.Stage} / {call.Model}");
        writer.Field("Provider", call.Provider);
        writer.Field("Reasoning effort", call.ReasoningEffort);
        writer.Field("Started at", FormatTimestamp(call.StartedAt));
        writer.Field("Completed at", FormatTimestamp(call.CompletedAt));
        writer.Field("Client call ID", call.Id.ToString("D"));
        writer.Field("Provider request ID", call.ProviderRequestId ?? "unavailable");
        writer.Field("Provider response ID", call.ProviderResponseId ?? "unavailable");
        if (call.Stage == ModelCallStage.VerificationPlanning)
        {
            writer.Field("Dispatch disposition", call.VerificationDispatchDisposition?.ToString() ?? "legacy or unavailable");
            writer.Field("Provider HTTP status", call.ProviderHttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable");
            writer.Field("Provider usage reported", call.ProviderUsageAvailable switch
            {
                true => "yes",
                false => "no or incomplete",
                _ => "legacy or unavailable"
            });
            writer.Field("Verification usage completeness", verificationUsage!.Value.ToString());
        }
        writer.Field("Result", call.Succeeded ? "succeeded" : $"failed ({call.FailureCategory ?? "unspecified"})");
        if (verificationUsage == VerificationUsageAvailability.Partial) writer.Body("Usage partial; unavailable fields remain omitted.");
        else if (!usageAvailable) writer.Body("Usage unavailable");
        var preserveIndividualUsage = verificationUsage is VerificationUsageAvailability.Partial or VerificationUsageAvailability.Complete;
        writer.Field("Total input tokens", FormatTokens(usageAvailable || preserveIndividualUsage ? call.InputTokens : null));
        writer.Field("Cached-input tokens", FormatTokens(usageAvailable || preserveIndividualUsage ? call.CachedInputTokens : null));
        writer.Field("Uncached-input tokens", FormatTokens(resolved.UncachedInputTokens));
        writer.Field("Output tokens", FormatTokens(usageAvailable || preserveIndividualUsage ? call.OutputTokens : null));
        writer.Field("Reasoning tokens", FormatTokens(usageAvailable || preserveIndividualUsage ? call.ReasoningTokens : null));
        writer.Field(resolved.IsPartialEstimate ? "Conservative partial estimated cost" : "Estimated cost",
            resolved.EstimatedCostUsd is { } cost ? FormatCost(cost) : "unavailable");
        writer.Field("Pricing provenance", resolved.ProvenanceLabel);
        if (call.PricingSnapshot is { } snapshot)
        {
            writer.Field("Stored input rate", FormatRate(snapshot.InputPerMillionUsd));
            writer.Field("Stored cached-input rate", FormatRate(snapshot.CachedInputPerMillionUsd));
            writer.Field("Stored output rate", FormatRate(snapshot.OutputPerMillionUsd));
        }
        writer.Space(4);
    }

    private static void WriteCollection<T>(
        FlowWriter writer,
        IReadOnlyList<T> values,
        int maximumEntries,
        Action<T, int> write)
    {
        if (values.Count == 0) { writer.Body("None recorded."); return; }
        var displayed = Math.Min(values.Count, maximumEntries);
        for (var index = 0; index < displayed; index++) write(values[index], index);
        writer.OmittedEntries(values.Count - displayed);
    }

    private static void WriteStrings(
        FlowWriter writer,
        IReadOnlyList<string> values,
        int maximumEntries,
        Func<string, string>? format = null)
    {
        if (values.Count == 0) { writer.Body("None recorded."); return; }
        var displayed = Math.Min(values.Count, maximumEntries);
        for (var index = 0; index < displayed; index++)
            writer.Body($"- {(format ?? (item => item))(values[index])}");
        writer.OmittedEntries(values.Count - displayed);
    }

    private static IReadOnlyList<string> SensitiveIdentities(EngineeringTask task)
    {
        var identities = new[]
        {
            task.Repository,
            task.RepositorySnapshot?.NormalizedRoot,
            task.ImplementationWorkspace?.RepositoryIdentity,
            task.ImplementationWorkspace?.GitCommonDirectoryIdentity,
            task.ImplementationWorkspace?.OwnershipReference
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
        if (identities.Any(value => value.Length > MaximumSensitiveIdentityCharacters))
            throw new ImplementationException("implementation_report_identity_limit",
                "Persisted implementation identity metadata exceeds the safe report limit.");
        return identities.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Recorded(bool value) => value ? "recorded" : "not recorded";
    private static string RecordedAt(DateTimeOffset? value) => value is { } timestamp ? FormatTimestamp(timestamp) : "not recorded";
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string FormatTokens(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string FormatNumber(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string FormatCost(decimal value) => $"${value.ToString("0.00000000", CultureInfo.InvariantCulture)} USD";
    private static string FormatRate(decimal value) => $"${value.ToString("0.########", CultureInfo.InvariantCulture)} USD per million tokens";
    private static string Join<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0) return "none";
        var builder = new StringBuilder(Math.Min(MaximumJoinedCharacters, 256));
        var displayed = Math.Min(values.Count, MaximumJoinedValues);
        var index = 0;
        for (; index < displayed; index++)
        {
            var item = Convert.ToString(values[index], CultureInfo.InvariantCulture) ?? string.Empty;
            var separatorLength = index == 0 ? 0 : 2;
            if (item.Length > MaximumJoinedContentCharacters - builder.Length - separatorLength) break;
            if (separatorLength > 0) builder.Append(", ");
            builder.Append(item);
        }
        var omitted = values.Count - index;
        if (omitted > 0)
        {
            var marker = $"[{omitted.ToString(CultureInfo.InvariantCulture)} additional values omitted by report limit]";
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(marker);
        }
        return builder.ToString();
    }
    private static string YesNo(bool value) => value ? "yes" : "no";

    internal enum FlowElementKind
    {
        BodyLine,
        SubsectionWithBody,
        BannerWithBody
    }

    internal sealed class FlowWriter(
        PdfDocumentBuilder builder,
        PdfDocumentBuilder.AddedFont regular,
        PdfDocumentBuilder.AddedFont bold,
        IReadOnlyList<string> sensitiveIdentities,
        string? workspaceToken)
    {
        private PdfPageBuilder? _page;
        private decimal _y;
        private int _pageCount;
        private int _renderedLines;
        private int _renderedCharacters;
        private decimal _minimumRenderedY = decimal.MaxValue;
        private bool _stopped;

        internal int RenderedLines => _renderedLines;
        internal int RenderedCharacters => _renderedCharacters;
        internal int PageCount => _pageCount;
        internal decimal CursorY => _y;
        internal decimal MinimumRenderedY => _minimumRenderedY;

        public void Title(string value) => Lines(value, 17m, 22m, bold, 54, 0m, 12m, 0m);
        public void Banner(string value) => Lines(value, 13m, 18m, bold, 70, 0m, 7m, BodyLineHeight);
        public void Section(string value, FlowElementKind nextElement = FlowElementKind.BodyLine) =>
            Lines(value, 13m, 18m, bold, 70, 9m, 5m, ReservedHeight(nextElement));
        public void Subsection(string value, FlowElementKind nextElement = FlowElementKind.BodyLine) =>
            Lines(value, 11m, 16m, bold, 78, 0m, 3m, ReservedHeight(nextElement));
        public void Field(string label, string value) => Body($"{label}: {value}");
        public void Body(string value) => Lines(value, BodyFontSize, BodyLineHeight, regular, BodyCharactersPerLine, 0m, 2m, 0m);
        public void OmittedEntries(int count)
        {
            if (count > 0) Body($"[{count.ToString(CultureInfo.InvariantCulture)} additional entries omitted by report limit]");
        }
        public void Space(decimal points)
        {
            if (_stopped || _page is null) return;
            if (_pageCount == MaximumReportPages && _y - points - BodyLineHeight < PageBottom)
            {
                StopForBudget();
                return;
            }
            _y -= points;
        }

        private void Lines(
            string value,
            decimal fontSize,
            decimal lineHeight,
            PdfDocumentBuilder.AddedFont font,
            int maximumCharacters,
            decimal before,
            decimal after,
            decimal reservedFollowingHeight)
        {
            if (_stopped) return;
            var sanitizationWindow = checked(MaximumRenderedFieldCharacters + MaximumSensitiveIdentityCharacters);
            var fieldTruncated = value.Length > MaximumRenderedFieldCharacters;
            if (value.Length > sanitizationWindow) value = TruncateRuneSafe(value, sanitizationWindow);
            value = Sanitize(value);
            value = NormalizeSupportedText(value);
            var maximumContentCharacters = MaximumRenderedFieldContentCharacters;
            if (value.Length > maximumContentCharacters)
            {
                value = TruncateRuneSafe(value, maximumContentCharacters);
                fieldTruncated = true;
            }
            if (fieldTruncated) value = $"{value}\n{FieldTruncationNotice}";
            var lines = Wrap(value, maximumCharacters);
            var headingHeight = checked(before + lines.Count * lineHeight + after + reservedFollowingHeight);
            if (reservedFollowingHeight > 0 && !EnsureSpace(headingHeight)) return;
            _y -= before;
            foreach (var line in lines)
            {
                if (!TryReserveBudget(line) || !EnsureSpace(lineHeight)) return;
                _minimumRenderedY = Math.Min(_minimumRenderedY, _y);
                _page!.AddText(line, (double)fontSize, new PdfPoint((double)Left, (double)_y), font);
                _y -= lineHeight;
            }
            _y -= after;
        }

        private string Sanitize(string value)
        {
            foreach (var identity in sensitiveIdentities)
            {
                foreach (var variant in new[]
                         {
                             identity,
                             identity.Replace('\\', '/'),
                             identity.Replace("\\", "\\\\", StringComparison.Ordinal)
                         }.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(item => item.Length))
                    value = value.Replace(variant, "[protected-local-identity]", StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrWhiteSpace(workspaceToken))
                value = value.Replace(workspaceToken, "[internal-id-redacted]", StringComparison.OrdinalIgnoreCase);
            value = RedactAbsoluteLocalPaths(value);
            return SensitiveContentDetector.ContainsSensitiveValue(value)
                ? "[sensitive-content-omitted]"
                : value;
        }

        private bool TryReserveBudget(string line)
        {
            try
            {
                var nextLines = checked(_renderedLines + 1);
                var nextCharacters = checked(_renderedCharacters + line.Length);
                if (nextLines > MaximumRenderedLines - 1 ||
                    nextCharacters > MaximumTotalRenderedCharacters - AggregateTruncationNotice.Length)
                {
                    StopForBudget();
                    return false;
                }
                _renderedLines = nextLines;
                _renderedCharacters = nextCharacters;
                return true;
            }
            catch (OverflowException)
            {
                StopForBudget();
                return false;
            }
        }

        private bool EnsureSpace(decimal requiredHeight)
        {
            if (_stopped) return false;
            if (_page is not null && HasSpace(requiredHeight)) return true;
            if (_pageCount >= MaximumReportPages)
            {
                StopForBudget();
                return false;
            }
            _page = builder.AddPage(PageSize.A4);
            _pageCount++;
            _y = PageTop;
            if (HasSpace(requiredHeight)) return true;
            StopForBudget();
            return false;
        }

        private bool HasSpace(decimal requiredHeight) =>
            _y - requiredHeight - (_pageCount == MaximumReportPages ? BodyLineHeight : 0m) >= PageBottom;

        private void StopForBudget()
        {
            if (_stopped) return;
            _stopped = true;
            if (_page is null && _pageCount < MaximumReportPages)
            {
                _page = builder.AddPage(PageSize.A4);
                _pageCount++;
                _y = PageTop;
            }
            if (_page is not null && _y - BodyLineHeight < PageBottom && _pageCount < MaximumReportPages)
            {
                _page = builder.AddPage(PageSize.A4);
                _pageCount++;
                _y = PageTop;
            }
            if (_page is not null && _y - BodyLineHeight >= PageBottom)
            {
                _minimumRenderedY = Math.Min(_minimumRenderedY, _y);
                _page.AddText(AggregateTruncationNotice, (double)BodyFontSize,
                    new PdfPoint((double)Left, (double)_y), bold);
                _renderedLines = checked(_renderedLines + 1);
                _renderedCharacters = checked(_renderedCharacters + AggregateTruncationNotice.Length);
            }
        }

        private static decimal ReservedHeight(FlowElementKind kind) => kind switch
        {
            FlowElementKind.SubsectionWithBody => 16m + 3m + BodyLineHeight,
            FlowElementKind.BannerWithBody => 18m + 7m + BodyLineHeight,
            _ => BodyLineHeight
        };

        private static string TruncateRuneSafe(string value, int maximumCharacters)
        {
            if (value.Length <= maximumCharacters) return value;
            var length = maximumCharacters;
            if (length > 0 && char.IsHighSurrogate(value[length - 1]) &&
                length < value.Length && char.IsLowSurrogate(value[length])) length--;
            return value[..length];
        }
    }
}
