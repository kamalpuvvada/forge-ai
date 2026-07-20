using Forge.Core;
using Forge.Infrastructure;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Forge.Core.Tests;

public sealed class TaskPdfExporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pdf_has_valid_signature_required_sections_cost_provenance_and_no_repository_path()
    {
        var task = ReportTask("Generate the approved report.", "Include model usage and pricing provenance.");
        var exporter = Exporter();

        var bytes = exporter.Export(task);
        using var document = PdfDocument.Open(bytes);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("Forge AI Engineering Task Report", text);
        Assert.Contains(task.Id.ToString(), text);
        Assert.Contains("Original requirement", text);
        Assert.Contains("Clarification questions and answers", text);
        Assert.Contains("Approved requirement summary", text);
        Assert.Contains("Model-call usage and estimated cost", text);
        Assert.Contains("stored pricing snapshot", text);
        Assert.Contains("legacy estimate \u2014 pricing snapshot unavailable", text);
        Assert.Contains("cost unavailable", text);
        Assert.Contains("partial estimate", text);
        Assert.Contains("estimates, not invoices", text);
        Assert.Contains("Reasoning effort: low", text);
        Assert.Contains("Provider request ID: request-1", text);
        Assert.Contains("Provider response ID: response-1", text);
        Assert.Contains("Reasoning tokens: 300", text);
        Assert.DoesNotContain(task.Repository, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654.321", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_call_pdf_states_that_no_model_calls_were_recorded()
    {
        var task = EngineeringTask.Create("C:/repository", "Export a task report.", Now);

        var bytes = Exporter().Export(task);
        using var document = PdfDocument.Open(bytes);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));

        Assert.Contains("Available estimated subtotal: $0.00000000 USD", text);
        Assert.Contains("No model calls were recorded for this task.", text);
        Assert.DoesNotContain("All recorded model calls have an available estimated cost.", text);
    }

    [Fact]
    public void Non_empty_fully_priced_pdf_preserves_complete_estimate_wording()
    {
        var task = EngineeringTask.Create("C:/repository", "Export a task report.", Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "current-model", "low",
            Now, Now, true, "response", 100, 0, 10, 0, 0m, null,
            new ModelPricingSnapshot(10m, 2m, 20m)), Now);

        var bytes = Exporter().Export(task);
        using var document = PdfDocument.Open(bytes);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));

        Assert.Contains("All recorded model calls have an available estimated cost.", text);
        Assert.DoesNotContain("No model calls were recorded for this task.", text);
    }

    [Fact]
    public void Long_content_wraps_and_paginates_without_omitting_tail_marker()
    {
        var longRequirement = string.Join(' ', Enumerable.Repeat("Long requirement content must remain visible.", 350)) + " TAIL-MARKER-XYZ";
        var task = ReportTask(longRequirement, string.Join(' ', Enumerable.Repeat("Approved summary detail.", 180)));

        var bytes = Exporter().Export(task);
        using var document = PdfDocument.Open(bytes);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));

        Assert.True(document.NumberOfPages > 1);
        Assert.Contains("TAIL-MARKER-XYZ", text);
    }

    [Fact]
    public void Text_fallback_and_wrapping_are_deterministic()
    {
        Assert.Equal("Smart 'quote', dash \u2014 and unsupported ?.",
            TaskPdfExporter.NormalizeSupportedText("Smart ‘quote’, dash — and unsupported 漢."));
        var wrapped = TaskPdfExporter.Wrap("abcdefghijk", 5);
        Assert.Equal(["abcde", "fghij", "k"], wrapped);
    }

    [Fact]
    public void Pre_analysis_report_records_unavailable_stages_without_later_audit_sections()
    {
        var task = EngineeringTask.Create(@"C:\sensitive\pre-analysis", "Export the current task state.", Now);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("Report metadata", text);
        Assert.Contains("Exported-at UTC: 2026-07-18T12:00:00Z", text);
        Assert.Contains("Requirement summary generated: not recorded", text);
        Assert.Contains("Repository analysed: not recorded", text);
        Assert.Contains("Plan created: not recorded", text);
        Assert.Contains("Implementation completed: not recorded", text);
        Assert.DoesNotContain("Repository analysis", text);
        Assert.DoesNotContain("Implementation plan", text);
        Assert.DoesNotContain("Implementation review", text);
        Assert.DoesNotContain(task.Repository, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Awaiting_plan_approval_report_contains_bounded_analysis_and_unapproved_plan_without_evidence_excerpt()
    {
        var task = PlannedAuditTask(approvePlan: false);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("AwaitingPlanApproval", text);
        Assert.Contains("Repository analysis", text);
        Assert.Contains("Selected evidence metadata", text);
        Assert.Contains("E1", text);
        Assert.Contains("src/GreetingService.cs", text);
        Assert.Contains("PROPOSED — NOT APPROVED", text);
        Assert.Contains("Proposed validation commands — NOT EXECUTED", text);
        Assert.Contains("NOT EXECUTED: inspect generated diff", text);
        Assert.DoesNotContain("COMPLETE-EVIDENCE-EXCERPT-MARKER", text);
        Assert.DoesNotContain("Implementation review", text);
    }

    [Fact]
    public void Awaiting_implementation_review_report_contains_complete_bounded_review_and_safe_runtime_status()
    {
        var task = ImplementedAuditTask();
        var runtime = new ImplementationReportRuntimeStatus(true,
            ImplementationAttemptDisposition.Completed, @"The persisted review at C:\unrelated\worktree is available.");

        var text = Extract(Exporter().Export(task, runtime));

        Assert.Contains("AwaitingImplementationReview", text);
        Assert.Contains("Implementation revision chronology", text);
        Assert.Contains("Implementation revision 1 — CURRENT", text);
        Assert.Contains("Generation disposition: Succeeded", text);
        Assert.Contains("Review disposition: Current", text);
        var revision = Assert.Single(task.ImplementationRevisions);
        Assert.Contains(revision.PlanFingerprint, text);
        Assert.Contains(revision.ResultFingerprint!, text);
        Assert.DoesNotContain(revision.RevisionId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Plan approval status: APPROVED", text);
        Assert.Contains(new string('a', 40), text);
        Assert.Contains(ImplementationBranchDisplay.SafeLabel, text);
        Assert.DoesNotContain("forge/task-0123456789abcdef0123456789abcdef", text, StringComparison.Ordinal);
        Assert.Contains("Implementation source: DeterministicFake", text);
        foreach (var path in new[] { "src/GreetingService.cs", "config/settings.json", "README.md" })
            Assert.Contains(path, text);
        foreach (var hash in new[] { new string('3', 64), new string('4', 64), new string('5', 64),
                     new string('6', 64), new string('7', 64), new string('8', 64) })
            Assert.Contains(hash, text);
        Assert.Contains("Additions: 2", text);
        Assert.Contains("Deletions: 1", text);
        Assert.Contains("Original bytes: 20", text);
        Assert.Contains("Generated bytes: 30", text);
        Assert.Contains("Original lines: 2", text);
        Assert.Contains("Generated lines: 3", text);
        Assert.Contains("DIFF-PREVIEW-GREETING", text);
        Assert.Contains("DIFF-PREVIEW-SETTINGS", text);
        Assert.Contains("DIFF-PREVIEW-README", text);
        Assert.Contains("Overall diff truncated: no", text);
        Assert.Contains("Diff truncated: no", text);
        Assert.Contains("Active checkout verified when implementation completed: yes", text);
        Assert.Contains("Valid non-reparse isolated worktree metadata observed at export time: yes", text);
        Assert.Contains("Attempt disposition: Completed", text);
        Assert.DoesNotContain("Active checkout verified at export time", text);
        Assert.DoesNotContain("Active checkout verified: yes", text);
        Assert.Contains("records no target build or test execution", text);
        Assert.Contains("Proposed plan commands remain unexecuted", text);
        Assert.DoesNotContain(task.Repository, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(task.RepositorySnapshot!.NormalizedRoot, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(task.ImplementationWorkspace!.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(task.ImplementationWorkspace.GitCommonDirectoryIdentity, text, StringComparison.Ordinal);
        Assert.DoesNotContain(task.ImplementationWorkspace.OwnershipReference, text, StringComparison.Ordinal);
        Assert.DoesNotContain("AbCdEf1234567890secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\unrelated\worktree", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COMPLETE-EVIDENCE-EXCERPT-MARKER", text);
        Assert.DoesNotContain("System.InvalidOperationException", text);
    }

    [Fact]
    public void OpenAI_implementation_pdf_records_provider_audit_and_validation_without_private_context()
    {
        var source = ImplementedAuditTask();
        source.RecordModelCall(new ModelCallRecord(
            Guid.Parse("11111111-1111-4111-8111-111111111111"), ModelCallStage.Implementation,
            "OpenAI", "gpt-5.6-sol", "high", Now, Now.AddSeconds(4), true, "response-safe",
            100, 20, 50, 10, 0.002m, null, new ModelPricingSnapshot(5m, .5m, 30m), "request-safe"), Now);
        var result = source.ImplementationResult! with
        {
            Source = ImplementationSource.OpenAI,
            Model = "gpt-5.6-sol",
            Summary = "OpenAI proposed the approved bounded operations.",
            Warnings = []
        };
        var task = CopyImplementationState(source, result, source.ImplementationWorkspace, null);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("Implementation source: OpenAI", text);
        Assert.Contains("Provider output was treated as untrusted", text);
        Assert.Contains("Deterministic operation validation: accepted", text);
        Assert.Contains("Call 1: Implementation / gpt-5.6-sol", text);
        Assert.Contains("Client call ID: 11111111-1111-4111-8111-111111111111", text);
        Assert.Contains("Provider request ID: request-safe", text);
        Assert.Contains("Provider response ID: response-safe", text);
        Assert.DoesNotContain("contextFingerprint", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceContextIdentity", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rationale", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_required_usage_is_explicitly_unavailable_in_pdf_without_zero_cost()
    {
        var task = EngineeringTask.Create("C:/repository", "Export truthful unavailable usage.", Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Implementation, "OpenAI", "gpt-5.6-sol", "high",
            Now, Now.AddSeconds(2), false, null, null, null, null, null, 0m,
            "implementation_provider_error"), Now);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("Usage unavailable", text, StringComparison.Ordinal);
        Assert.Contains("Estimated cost: unavailable", text, StringComparison.Ordinal);
        Assert.Contains("Available estimated subtotal: unavailable", text, StringComparison.Ordinal);
        Assert.Contains("Estimated cost is unavailable for all displayed model calls.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Estimated cost: $0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Available estimated subtotal: $0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.000000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_unavailable_nonzero_estimate_is_not_rendered_in_task_pdf()
    {
        var task = EngineeringTask.Create("C:/repository", "Ignore a stale legacy estimate.", Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "legacy-model", "medium",
            Now, Now.AddSeconds(1), false, null, null, null, null, null, 123.456789m,
            "provider_error"), Now);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("Usage unavailable", text);
        Assert.Contains("Estimated cost: unavailable", text);
        Assert.Contains("Available estimated subtotal: unavailable", text);
        Assert.DoesNotContain("123.456789", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_explicit_zero_usage_and_cost_remain_numeric_in_task_pdf()
    {
        var task = EngineeringTask.Create("C:/repository", "Render provider-confirmed zero usage.", Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "legacy-model", "medium",
            Now, Now.AddSeconds(1), true, "response", 0, 0, 0, 0, 0m, null), Now);

        var text = Extract(Exporter().Export(task));

        Assert.DoesNotContain("Usage unavailable", text);
        Assert.Contains("Total input tokens: 0", text);
        Assert.Contains("Output tokens: 0", text);
        Assert.Contains("Estimated cost: $0.00000000 USD", text);
        Assert.Contains("Available estimated subtotal: $0.00000000 USD", text);
        Assert.Contains("All recorded model calls have an available estimated cost.", text);
    }

    [Fact]
    public void Partial_task_pdf_excludes_legacy_unavailable_zero_from_available_subtotal()
    {
        var task = EngineeringTask.Create("C:/repository", "Render a truthful partial subtotal.", Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "legacy-model", "medium",
            Now, Now.AddSeconds(1), true, "response", 10, 0, 5, 0, 1.25m, null), Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "legacy-model", "medium",
            Now, Now.AddSeconds(2), false, null, null, null, null, null, 0m, "provider_error"), Now);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("Available estimated subtotal: $1.25000000 USD", text);
        Assert.Contains("This is a partial estimate. 1 displayed model call(s) had unavailable cost", text);
        Assert.Contains("Usage unavailable", text);
        Assert.DoesNotContain("Available estimated subtotal: $0", text);
    }

    [Fact]
    public void Uncertain_checkout_review_is_explicitly_not_eligible_for_approval()
    {
        var task = ImplementedAuditTask();
        task.RecordImplementationPostconditionFailure(new ImplementationFailure(
            "implementation_active_checkout_changed",
            "Forge could not verify that the active checkout remained unchanged.",
            true,
            Now.AddMinutes(7),
            ActiveCheckoutVerified: false), Now.AddMinutes(7));

        var text = Extract(Exporter().Export(task));

        Assert.Contains("NOT ELIGIBLE FOR APPROVAL", text);
        Assert.Contains("could not verify that the active checkout remained unchanged", text);
        Assert.Contains("Active checkout verified when implementation completed: no", text);
        Assert.DoesNotContain("APPROVED PERSISTED IMPLEMENTATION REVIEW", text);
    }

    [Fact]
    public void Approved_implementation_report_identifies_exact_revision_timestamp_and_persisted_evidence_boundary()
    {
        var task = ImplementedAuditTask();
        var current = Assert.Single(task.ImplementationRevisions);
        var commandId = Guid.NewGuid();
        task.ApproveImplementation(commandId, task.RowVersion, current.RevisionId,
            current.ResultFingerprint!, Now.AddMinutes(7));

        var text = Extract(Exporter().Export(task,
            new ImplementationReportRuntimeStatus(false, ImplementationAttemptDisposition.RecoveryRequired,
                "The isolated workspace was not observed at export time.")));

        var approved = Assert.Single(task.ImplementationRevisions);
        Assert.Contains("Workflow status: ImplementationApproved", text);
        Assert.Contains("Implementation revision 1 — CURRENT / APPROVED", text);
        Assert.Contains("Review disposition: Approved", text);
        Assert.Contains("Approved at: 2026-07-18T12:07:00Z", text);
        Assert.Contains(approved.ResultFingerprint!, text);
        Assert.Contains("approval applies to the exact persisted review evidence", text);
        Assert.Contains("validation was not run", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("APPROVED PERSISTED IMPLEMENTATION REVIEW", text);
        Assert.Contains("staged", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("committed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pushed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT EXECUTED", text);
        Assert.DoesNotContain(commandId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(task.ImplementationWorkspace!.Token, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_completed_review_gets_a_stable_synthetic_revision_in_pdf_without_approval()
    {
        var source = ImplementedAuditTask();
        var legacy = CopyImplementationState(source, source.ImplementationResult,
            source.ImplementationWorkspace, source.LastImplementationFailure);
        var firstRevision = Assert.Single(legacy.ImplementationRevisions);
        var first = Extract(Exporter().Export(legacy));
        var second = Extract(Exporter().Export(legacy));

        Assert.Equal(first, second);
        Assert.Contains("Implementation revision 1 — CURRENT", first);
        Assert.Contains(firstRevision.ResultFingerprint!, first);
        Assert.Contains("Approved at: not approved", first);
        Assert.Null(legacy.ApprovedImplementationRevisionId);
    }

    [Fact]
    public void Historical_fake_summary_is_preserved_and_contextualised_after_later_stages()
    {
        const string oldSummary =
            "Development note: assembled by deterministic fake logic. No repository inspection or AI model call occurred.";
        var planned = PlannedAuditTask(approvePlan: false);
        var task = EngineeringTask.Rehydrate(
            planned.Id, planned.Repository, planned.OriginalRequirement, planned.CurrentClarifiedRequirement,
            planned.ClarificationAnswers, planned.RequirementRevisionNotes, planned.ModelCalls,
            planned.CurrentPendingQuestion, oldSummary, planned.Status, planned.CreatedAt, planned.UpdatedAt,
            planned.RequirementApprovedAt, planned.PlanApprovedAt, planned.RepositorySnapshot, planned.EvidenceItems,
            planned.EvidenceFilesInspected, planned.EvidenceFilesSelected, planned.TotalEvidenceCharacters,
            planned.ImplementationPlan, planned.RepositoryAnalyzedAt, planned.RepositoryFingerprint, planned.PlanCreatedAt,
            planned.PlanRevisionNotes);

        var text = Extract(Exporter().Export(task));

        Assert.Equal(oldSummary, task.RequirementSummary);
        Assert.Contains("Development note: assembled by deterministic fake logic.", text);
        Assert.Contains("No repository inspection", text);
        Assert.Contains("Any development note inside the approved requirement summary", text);
        Assert.Contains("summary-generation time", text);
        Assert.Contains("Later repository analysis", text);
        Assert.Contains("Repository analysis", text);
    }

    [Theory]
    [InlineData(@"C:\Users\Alice Smith\repo", "Alice Smith")]
    [InlineData("C:/Users/Alice Smith/repo", "Alice Smith")]
    [InlineData(@"\\server\Shared Folder\repo", "Shared Folder")]
    [InlineData("//server/Shared Folder/repo", "Shared Folder")]
    [InlineData("/opt/acme/private/output.log", "private/output.log")]
    [InlineData("/home/user/project", "home/user/project")]
    [InlineData(@"C:\\Users\\Alice Smith\\repo", "Alice Smith")]
    public void Absolute_path_forms_are_fully_redacted_without_suffix_leakage(string path, string forbiddenSuffix)
    {
        var task = EngineeringTask.Create("C:/safe-repository", $"Inspect {path}", Now);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("[absolute-local-path-omitted]", text);
        Assert.DoesNotContain(forbiddenSuffix, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Forge\repo", @")\Forge\repo")]
    [InlineData("C:/Program Files (x86)/Forge/repo", ")/Forge/repo")]
    [InlineData("\"C:\\Program Files (x86)\\Forge\\repo\"", "Program Files")]
    [InlineData(@"C:\\Program Files (x86)\\Forge\\repo", @")\\Forge\\repo")]
    [InlineData(@"\\server\Shared Folder (Archive)\repo", @")\repo")]
    [InlineData("//server/Shared Folder (Archive)/repo", ")/repo")]
    [InlineData(@"C:\Forge\final report.txt", "final report.txt")]
    public void Parenthesized_absolute_paths_are_redacted_as_complete_tokens(string value, string forbiddenSuffix)
    {
        var sanitized = TaskPdfExporter.RedactAbsoluteLocalPaths($"Before {value}, after.");

        Assert.Equal("Before [absolute-local-path-omitted], after.", sanitized);
        Assert.DoesNotContain(forbiddenSuffix, sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com/docs/page")]
    [InlineData("http://localhost:5173/task")]
    [InlineData("./relative/file.txt")]
    [InlineData("../relative/file.txt")]
    [InlineData("src/Feature/File.cs")]
    [InlineData("/docs/guide")]
    [InlineData("file as ordinary prose")]
    [InlineData("ordinary/slash-delimited/prose")]
    [InlineData("--- a/src/File.cs\n+++ b/src/File.cs\n@@ -1,2 +1,3 @@\nindex abc1234..def5678\n+/docs/guide")]
    public void Non_filesystem_content_is_preserved_exactly(string value)
    {
        Assert.Equal(value, TaskPdfExporter.RedactAbsoluteLocalPaths(value));
    }

    [Fact]
    public void Multiple_absolute_paths_and_sentence_punctuation_are_redacted_without_suffixes()
    {
        var value = @"First C:\Program Files (x86)\Forge\repo, second /opt/acme/private/output.log; third \\server\Shared Folder (Archive)\repo.";

        var sanitized = TaskPdfExporter.RedactAbsoluteLocalPaths(value);

        Assert.Equal(3, CountOccurrences(sanitized, "[absolute-local-path-omitted]"));
        Assert.DoesNotContain("Program Files", sanitized);
        Assert.DoesNotContain("private/output.log", sanitized);
        Assert.DoesNotContain("Shared Folder", sanitized);
        Assert.EndsWith(".", sanitized);
    }

    [Theory]
    [InlineData(@"Use C:\Forge\repo and do not deploy.", "Use [absolute-local-path-omitted] and do not deploy.")]
    [InlineData(@"Read C:\Program Files\Forge\log.txt before continuing.", "Read [absolute-local-path-omitted] before continuing.")]
    [InlineData("The file is /opt/forge/output.log, but the command was not run.", "The file is [absolute-local-path-omitted], but the command was not run.")]
    [InlineData(@"Compare C:\Repo A\file.txt with C:\Repo B\file.txt.", "Compare [absolute-local-path-omitted] with [absolute-local-path-omitted].")]
    [InlineData(@"Path C:\Forge\repo; validation remains pending.", "Path [absolute-local-path-omitted]; validation remains pending.")]
    [InlineData(@"Use \\server\Shared Folder (Archive)\repo then review.", "Use [absolute-local-path-omitted] then review.")]
    [InlineData("Open \"C:\\Program Files (x86)\\Forge\\repo\" and review.", "Open [absolute-local-path-omitted] and review.")]
    public void Absolute_path_scanner_preserves_adjacent_material_prose(string value, string expected)
    {
        Assert.Equal(expected, TaskPdfExporter.RedactAbsoluteLocalPaths(value));
    }

    [Theory]
    [InlineData(@"C:\Research and Development\secret.txt")]
    [InlineData(@"C:\Program Files (x86)\Forge\repo")]
    [InlineData(@"\\server\Research and Development\secret.txt")]
    [InlineData(@"C:\Forge\my file.txt")]
    [InlineData(@"C:\Forge\final build output.json")]
    public void Absolute_path_scanner_redacts_structural_spaced_segments_without_suffix_leakage(string value)
    {
        Assert.Equal("[absolute-local-path-omitted]", TaskPdfExporter.RedactAbsoluteLocalPaths(value));
    }

    [Theory]
    [InlineData(@"Use C:\Forge\repo carefully review README.md.",
        "Use [absolute-local-path-omitted] carefully review README.md.", 1)]
    [InlineData(@"Use C:\Forge\repo and do not deploy.",
        "Use [absolute-local-path-omitted] and do not deploy.", 1)]
    [InlineData(@"Read C:\Program Files\Forge\log.txt before continuing.",
        "Read [absolute-local-path-omitted] before continuing.", 1)]
    [InlineData("The file is /opt/forge/output.log, but the command was not run.",
        "The file is [absolute-local-path-omitted], but the command was not run.", 1)]
    [InlineData(@"Compare C:\Repo A\file.txt with C:\Repo B\file.txt.",
        "Compare [absolute-local-path-omitted] with [absolute-local-path-omitted].", 2)]
    [InlineData(@"C:\Forge\repo and verify results.txt",
        "[absolute-local-path-omitted] and verify results.txt", 1)]
    [InlineData(@"C:\Forge\repo before opening README.md",
        "[absolute-local-path-omitted] before opening README.md", 1)]
    public void Absolute_path_scanner_preserves_material_prose_after_unquoted_paths(
        string value,
        string expected,
        int expectedMarkers)
    {
        var sanitized = TaskPdfExporter.RedactAbsoluteLocalPaths(value);

        Assert.Equal(expected, sanitized);
        Assert.Equal(expectedMarkers, CountOccurrences(sanitized, "[absolute-local-path-omitted]"));
    }

    [Fact]
    public void Every_sensitive_identity_category_is_safe_at_each_field_split_point()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        var identities = new[]
        {
            @"C:\SensitiveRepositoryBoundary\Project",
            @"C:\SensitiveWorktreeRootBoundary\Worktrees",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            "9876543210abcdef9876543210abcdef9876543210abcdef9876543210abcdef",
            $"refs/forge/tasks/{token}"
        };
        var categories = identities.Concat(new[]
        {
            token,
            $"forge/task-{token}",
            @"C:\SensitiveWorktreeRootBoundary\Worktrees\0123456789abcdef0123456789abcdef",
            @"C:\Program Files (x86)\Forge\secret-workspace",
            "/opt/forge/private/secret-workspace"
        }).ToArray();
        var forbidden = new[]
        {
            "0123456789ab", "cdef01234567", "SensitiveRepositoryBoundary", "SensitiveWorktreeRootBoundary",
            "abcdef012345", "9876543210ab", "secret-workspace", "forge/private"
        };
        var boundary = TaskPdfExporter.MaximumRenderedFieldContentCharacters;

        foreach (var identity in categories)
        {
            foreach (var start in new[]
                     {
                         boundary,
                         boundary - 1,
                         boundary - identity.Length / 2,
                         boundary - Math.Max(1, identity.Length - 1),
                         boundary - identity.Length
                     }.Distinct())
            {
                var value = new string('q', start - 1) + " " + identity + new string('z', 3_000);
                var probe = CreateFlowProbe(writer => writer.Body(value), identities, token);
                var text = Extract(probe.Bytes);

                Assert.DoesNotContain(identity, text, StringComparison.OrdinalIgnoreCase);
                foreach (var fragment in forbidden.Where(identity.Contains))
                    Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(1, CountOccurrences(text, "[Text truncated by field limit]"));
                Assert.True(probe.Writer.RenderedCharacters <= TaskPdfExporter.MaximumRenderedFieldCharacters);
            }
        }
    }

    [Fact]
    public void Split_sensitive_identities_are_redacted_across_aggregate_report_surfaces()
    {
        var source = ImplementedAuditTask();
        var workspace = source.ImplementationWorkspace!;
        var boundary = TaskPdfExporter.MaximumRenderedFieldContentCharacters;
        static string Crossing(string identity, int boundary) =>
            new string('q', boundary - identity.Length / 2) + identity + new string('z', 3_000);

        var changedFile = source.ImplementationResult!.ChangedFiles[0] with
        {
            DiffPreview = Crossing(source.Repository, boundary)
        };
        var result = source.ImplementationResult with
        {
            Warnings = [Crossing(workspace.OwnershipReference, boundary)],
            ChangedFiles = [changedFile]
        };
        var revisions = new[]
        {
            new RequirementRevisionNote("Correction", "Previous summary", Now,
                RequirementRevisionOutcome.Approved, Now,
                Crossing(workspace.GitCommonDirectoryIdentity, boundary))
        };
        var task = EngineeringTask.Rehydrate(source.Id, source.Repository, source.OriginalRequirement,
            source.CurrentClarifiedRequirement, source.ClarificationAnswers, revisions, source.ModelCalls,
            source.CurrentPendingQuestion, Crossing(workspace.Token, boundary), source.Status, source.CreatedAt,
            source.UpdatedAt, source.RequirementApprovedAt, source.PlanApprovedAt, source.RepositorySnapshot,
            source.EvidenceItems, source.EvidenceFilesInspected, source.EvidenceFilesSelected,
            source.TotalEvidenceCharacters, source.ImplementationPlan, source.RepositoryAnalyzedAt,
            source.RepositoryFingerprint, source.PlanCreatedAt, source.PlanRevisionNotes, workspace, result,
            source.LastImplementationFailure, source.ImplementationStartedAt, source.ImplementationCompletedAt,
            source.ImplementationLease, source.RowVersion);
        var runtime = new ImplementationReportRuntimeStatus(true, ImplementationAttemptDisposition.Completed,
            Crossing(workspace.RepositoryIdentity, boundary));

        var text = Extract(Exporter().Export(task, runtime));

        foreach (var forbidden in new[]
                 {
                     workspace.Token, workspace.OwnershipReference, workspace.GitCommonDirectoryIdentity,
                     workspace.RepositoryIdentity, source.Repository
                 })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive\\audit-repository", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, CountOccurrences(text, "[Text truncated by field limit]"));
    }

    [Fact]
    public void Unexpectedly_long_sensitive_identity_rejects_report_generation_safely()
    {
        var source = ImplementedAuditTask();
        var workspace = source.ImplementationWorkspace! with
        {
            RepositoryIdentity = new string('a', TaskPdfExporter.MaximumSensitiveIdentityCharacters + 1)
        };
        var task = CopyImplementationState(source, source.ImplementationResult, workspace, null);

        var exception = Assert.Throws<ImplementationException>(() => Exporter().Export(task));

        Assert.Equal("implementation_report_identity_limit", exception.Category);
        Assert.DoesNotContain(workspace.RepositoryIdentity, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Absolute_path_at_field_truncation_boundary_is_redacted_before_rendering()
    {
        const string replacement = "[absolute-local-path-omitted]";
        var requirement = new string('x', TaskPdfExporter.MaximumRenderedFieldContentCharacters -
                                           replacement.Length - 1) +
                          @" C:\Program Files (x86)\Forge\repository-tail" + new string('z', 3_000);
        var probe = CreateFlowProbe(writer => writer.Body(requirement));
        var text = Extract(probe.Bytes);

        Assert.Contains("[absolute-local-path-omitted]", text);
        Assert.Contains("[Text truncated by field limit]", text);
        Assert.DoesNotContain("Program Files", text);
        Assert.DoesNotContain(@")\Forge", text);
    }

    [Fact]
    public void Legitimate_urls_relative_paths_and_slash_content_survive_every_dynamic_audit_surface()
    {
        var source = ImplementedAuditTask();
        var snapshot = source.RepositorySnapshot! with { Warnings = ["SNAP https://example.com/docs/page"] };
        var evidence = source.EvidenceItems[0] with
        {
            RelativePath = "src/Feature/File.cs",
            ReasonSelected = "EVID ./relative/file.txt"
        };
        var plan = source.ImplementationPlan! with
        {
            Title = "TITLE https://example.com/title",
            Objective = "OBJECTIVE /docs/guide",
            RepositoryUnderstanding = "UNDERSTANDING ../relative/file.txt",
            AffectedFiles = [source.ImplementationPlan.AffectedFiles[0] with { Purpose = "PURPOSE src/Feature/File.cs" }],
            Steps = [source.ImplementationPlan.Steps[0] with
            {
                Description = "STEP https://example.com/step",
                ExpectedResult = "EXPECTED ./relative/result.txt"
            }],
            ProposedValidationCommands = ["VALIDATE http://localhost:5173/task"],
            Risks = ["RISK /docs/guide"],
            Assumptions = ["ASSUME ../relative/file.txt"],
            UnresolvedQuestions = ["QUESTION https://example.com/question"],
            RequirementCoverage = [source.ImplementationPlan.RequirementCoverage[0] with
                { Requirement = "COVERAGE src/Feature/File.cs" }],
            Summary = "PLAN-SUMMARY https://example.com/summary"
        };
        const string diff = "--- a/src/Feature/File.cs\n+++ b/src/Feature/File.cs\n@@ -1 +1 @@\n+/docs/guide";
        var changed = source.ImplementationResult!.ChangedFiles[0] with { Path = "src/Feature/File.cs", DiffPreview = diff };
        var result = source.ImplementationResult with
        {
            Summary = "IMPLEMENTATION https://example.com/implementation",
            Warnings = ["IMPLEMENTATION-WARNING ./relative/file.txt"],
            ChangedFiles = [changed]
        };
        var call = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Implementation,
            "PROVIDER https://example.com/provider", "MODEL http://localhost:5173/model", "low", Now, Now,
            true, null, 1, 0, 1, 0, 0m, null);
        var task = EngineeringTask.Rehydrate(source.Id, source.Repository,
            "REQUIREMENT https://example.com/requirement", source.CurrentClarifiedRequirement,
            [new ClarificationAnswer("CLARIFICATION ./relative/question.txt", "ANSWER ../relative/answer.txt", Now)],
            [new RequirementRevisionNote("REQ-REVISION /docs/guide", "PREVIOUS https://example.com/previous", Now)],
            [call], null, "APPROVED-SUMMARY https://example.com/approved", source.Status, source.CreatedAt,
            source.UpdatedAt, source.RequirementApprovedAt, source.PlanApprovedAt, snapshot, [evidence], 1, 1,
            evidence.Excerpt.Length, plan, source.RepositoryAnalyzedAt, source.RepositoryFingerprint,
            source.PlanCreatedAt, [new PlanRevisionNote("PLAN-REVISION ./relative/plan.txt", Now,
                "Previous plan", source.RepositoryFingerprint!, source.ImplementationPlan)],
            source.ImplementationWorkspace, result, source.LastImplementationFailure, source.ImplementationStartedAt,
            source.ImplementationCompletedAt, source.ImplementationLease, source.RowVersion);
        var runtime = new ImplementationReportRuntimeStatus(true, ImplementationAttemptDisposition.Completed,
            "RUNTIME https://example.com/runtime");

        var text = Extract(Exporter().Export(task, runtime));

        foreach (var marker in new[] { "REQUIREMENT https://example.com/requirement", "CLARIFICATION ./relative/question.txt",
                     "ANSWER ../relative/answer.txt", "APPROVED-SUMMARY https://example.com/approved",
                     "REQ-REVISION /docs/guide", "PLAN-REVISION ./relative/plan.txt", "SNAP https://example.com/docs/page",
                     "EVID ./relative/file.txt", "TITLE https://example.com/title", "OBJECTIVE /docs/guide",
                     "VALIDATE http://localhost:5173/task", "IMPLEMENTATION https://example.com/implementation",
                     "IMPLEMENTATION-WARNING ./relative/file.txt", "RUNTIME https://example.com/runtime",
                     "PROVIDER https://example.com/provider", "MODEL http://localhost:5173/model", "+/docs/guide" })
            Assert.Contains(marker, text);
    }

    [Fact]
    public void Every_dynamic_audit_surface_is_sanitized_while_relative_diff_metadata_is_preserved()
    {
        var source = ImplementedAuditTask();
        const string token = "0123456789abcdef0123456789abcdef";
        var snapshot = source.RepositorySnapshot! with
        {
            Warnings = [@"warning C:\Users\Alice Smith\snapshot"],
            DetectedLanguages = ["C#"],
            DetectedExtensions = [".cs"]
        };
        var evidence = new EvidenceItem("E-PATH", "/home/user/project/secret.cs", 1, 1, "omitted excerpt",
            @"selected from 'C:\Users\Alice Smith\evidence'", 5, new string('e', 64));
        var plan = source.ImplementationPlan! with
        {
            Title = @"Plan C:\Users\Alice Smith\plan",
            Objective = "Read /opt/acme/private/objective.log",
            RepositoryUnderstanding = @"UNC \\server\Shared Folder\understanding",
            AffectedFiles = [new PlannedFileChange("src/Safe.cs", PlannedFileAction.Modify,
                "Purpose C:/Users/Alice Smith/purpose", ["E-PATH"], .9m)],
            Steps = [new ImplementationStep(1, "Step //server/Shared Folder/step", ["src/Safe.cs"], ["E-PATH"],
                @"Expected C:\\Users\\Alice Smith\\expected")],
            ProposedValidationCommands = ["inspect /home/user/project/output"],
            Risks = [@"Risk C:\Users\Alice Smith\risk"],
            Assumptions = ["Assume /opt/acme/private/assumption"],
            UnresolvedQuestions = ["Question //server/Shared Folder/question"],
            RequirementCoverage = [new RequirementCoverageItem("Coverage C:/Users/Alice Smith/coverage", ["src/Safe.cs"], [1])],
            Summary = @"Summary \\server\Shared Folder\summary"
        };
        var preview = "--- a/src/Safe.cs\n+++ b/src/Safe.cs\n@@ -1 +1 @@\nindex abc1234..def5678 100644\n+safe relative diff";
        var review = source.ImplementationResult!.ChangedFiles[0] with
        {
            Path = @"C:\Users\Alice Smith\changed.cs",
            DiffPreview = preview,
            DisplayedDiffCharacters = preview.Length,
            FullDiffCharacters = preview.Length,
            DisplayedDiffUtf8Bytes = System.Text.Encoding.UTF8.GetByteCount(preview),
            FullDiffUtf8Bytes = System.Text.Encoding.UTF8.GetByteCount(preview)
        };
        var result = source.ImplementationResult with
        {
            Branch = $"forge/task-{token}",
            Summary = "Implementation /opt/acme/private/result",
            Warnings = [@"warning C:\Users\Alice Smith\warning"],
            ChangedFiles = [review],
            FullDiffCharacters = review.FullDiffCharacters,
            DisplayedDiffCharacters = review.DisplayedDiffCharacters,
            FullDiffUtf8Bytes = review.FullDiffUtf8Bytes,
            DisplayedDiffUtf8Bytes = review.DisplayedDiffUtf8Bytes
        };
        var workspace = source.ImplementationWorkspace! with { Token = token, Branch = result.Branch };
        var calls = new[]
        {
            new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Implementation,
                @"provider C:\Users\Alice Smith\provider", "model /home/user/project/model", "low", Now, Now,
                true, null, 1, 0, 1, 0, 0m, null)
        };
        var task = EngineeringTask.Rehydrate(source.Id, source.Repository,
            @"Original C:\Users\Alice Smith\requirement", source.CurrentClarifiedRequirement,
            [new ClarificationAnswer(@"Question \\server\Shared Folder\question",
                "Answer //server/Shared Folder/answer", Now)],
            [new RequirementRevisionNote("Correction /opt/acme/private/correction", @"Previous C:\Users\Alice Smith\summary", Now)],
            calls, null, @"Summary C:\\Users\\Alice Smith\\summary", source.Status, source.CreatedAt, source.UpdatedAt,
            source.RequirementApprovedAt, source.PlanApprovedAt, snapshot, [evidence], 1, 1, evidence.Excerpt.Length,
            plan, source.RepositoryAnalyzedAt, source.RepositoryFingerprint, source.PlanCreatedAt,
            [new PlanRevisionNote("Correction //server/Shared Folder/plan", Now, "Previous /home/user/project/plan",
                source.RepositoryFingerprint!, source.ImplementationPlan!)], workspace, result, null,
            source.ImplementationStartedAt, source.ImplementationCompletedAt);
        var runtime = new ImplementationReportRuntimeStatus(true, ImplementationAttemptDisposition.Completed,
            @"Runtime C:\Users\Alice Smith\runtime");

        var text = Extract(Exporter().Export(task, runtime));

        foreach (var forbidden in new[] { "Alice Smith", "Shared Folder", "/opt/acme", "/home/user", token })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ImplementationBranchDisplay.SafeLabel, text);
        Assert.Contains("--- a/src/Safe.cs", text);
        Assert.Contains("+++ b/src/Safe.cs", text);
        Assert.Contains("@@ -1 +1 @@", text);
        Assert.Contains("abc1234..def5678", text);
        Assert.Contains(new string('3', 64), text);
    }

    [Fact]
    public void Aggregate_and_collection_budgets_bound_pathological_legacy_reports()
    {
        var answers = Enumerable.Range(1, 100).Select(index =>
            new ClarificationAnswer($"Question {index} {new string('q', 2_200)}",
                $"Answer {index} {new string('a', 2_200)}", Now)).ToArray();
        var revisions = Enumerable.Range(1, 40).Select(index =>
            new RequirementRevisionNote($"Correction {index}", $"Previous {index}", Now)).ToArray();
        var task = EngineeringTask.Rehydrate(Guid.NewGuid(), "C:/safe", new string('r', 200_000),
            "legacy", answers, revisions, [], null, new string('s', 200_000),
            WorkflowStatus.AwaitingRequirementApproval, Now, Now, null, null);

        var bytes = Exporter().Export(task);
        using var document = PdfDocument.Open(bytes);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));

        Assert.InRange(document.NumberOfPages, 1, TaskPdfExporter.MaximumReportPages);
        Assert.Contains("[Text truncated by field limit]", text);
        Assert.Contains("[50 additional entries omitted by report limit]", text);
        Assert.Contains("[Report truncated because the aggregate PDF resource limit was reached.]", text);
        Assert.Equal(1, CountOccurrences(text, TaskPdfExporter.AggregateTruncationNotice));
        Assert.DoesNotContain("Task cost estimate", text);
    }

    [Fact]
    public void Model_call_aggregation_is_bounded_partial_and_rejects_out_of_range_estimates()
    {
        var source = EngineeringTask.Create("C:/safe", "Export bounded model costs.", Now);
        var calls = Enumerable.Range(1, 105).Select(index => new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Implementation, "OpenAI", $"legacy-model-{index}", "low", Now, Now,
            true, $"response-{index}", 1, 0, 1, 0,
            index <= 2 ? decimal.MaxValue : index == 3 ? null : 1m, null)).ToArray();
        var task = EngineeringTask.Rehydrate(source.Id, source.Repository, source.OriginalRequirement,
            source.CurrentClarifiedRequirement, source.ClarificationAnswers, source.RequirementRevisionNotes,
            calls, null, source.RequirementSummary, source.Status, source.CreatedAt, source.UpdatedAt,
            source.RequirementApprovedAt, source.PlanApprovedAt);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("[5 additional entries omitted by report limit]", text);
        Assert.Contains("Call 100:", text);
        Assert.DoesNotContain("Call 101:", text);
        Assert.Contains("legacy estimate \u2014 pricing snapshot unavailable", text);
        Assert.Contains("cost unavailable", text);
        Assert.Contains("Available estimated subtotal: $97.00000000 USD", text);
        Assert.Contains("This is a partial estimate. 3 displayed model call(s) had unavailable cost", text);
    }

    [Fact]
    public void Final_notice_uses_reserved_character_and_line_capacity_exactly_once()
    {
        var characterProbe = CreateFlowProbe(writer =>
        {
            var remaining = TaskPdfExporter.MaximumTotalRenderedCharacters -
                            TaskPdfExporter.AggregateTruncationNotice.Length;
            while (remaining > 0)
            {
                var count = Math.Min(49_000, remaining);
                writer.Body(new string('x', count));
                remaining -= count;
            }
            writer.Body("character-overflow-trigger");
        });
        var lineProbe = CreateFlowProbe(writer =>
        {
            writer.Body(string.Join('\n', Enumerable.Repeat("x", TaskPdfExporter.MaximumRenderedLines - 1)));
            writer.Section("line-overflow-heading");
        });

        Assert.Equal(TaskPdfExporter.MaximumTotalRenderedCharacters, characterProbe.Writer.RenderedCharacters);
        Assert.Equal(1, CountOccurrences(Extract(characterProbe.Bytes), TaskPdfExporter.AggregateTruncationNotice));
        Assert.InRange(PdfPages(characterProbe.Bytes), 1, TaskPdfExporter.MaximumReportPages);
        Assert.Equal(TaskPdfExporter.MaximumRenderedLines, lineProbe.Writer.RenderedLines);
        Assert.Equal(1, CountOccurrences(Extract(lineProbe.Bytes), TaskPdfExporter.AggregateTruncationNotice));
        Assert.InRange(PdfPages(lineProbe.Bytes), 1, TaskPdfExporter.MaximumReportPages);
        Assert.DoesNotContain("line-overflow-heading", Extract(lineProbe.Bytes));
    }

    [Fact]
    public void Exact_page_80_exhaustion_keeps_one_counted_final_notice_on_page_80()
    {
        var probe = CreateFlowProbe(writer =>
        {
            var group = 0;
            while (writer.PageCount < TaskPdfExporter.MaximumReportPages)
            {
                writer.Section($"MEASURED-SECTION-{++group}");
                writer.Body($"MEASURED-BODY-{group}");
            }

            var bodyLines = (int)Math.Floor((writer.CursorY - TaskPdfExporter.PageBottom -
                                             TaskPdfExporter.BodyLineHeight - 2m) /
                                            TaskPdfExporter.BodyLineHeight);
            Assert.True(bodyLines > 0);
            writer.Body(string.Join('\n', Enumerable.Repeat("PAGE-80-FILLER", bodyLines)));
            var finalMeasuredGap = writer.CursorY - TaskPdfExporter.PageBottom - TaskPdfExporter.BodyLineHeight;
            Assert.InRange(finalMeasuredGap, 0m, TaskPdfExporter.BodyLineHeight - 1m);
            writer.Space(finalMeasuredGap);

            Assert.Equal(TaskPdfExporter.PageBottom + TaskPdfExporter.BodyLineHeight, writer.CursorY);
            writer.Body("ONE-LINE-AFTER-EXACT-EXHAUSTION");
        });

        using var document = PdfDocument.Open(probe.Bytes);
        Assert.Equal(TaskPdfExporter.MaximumReportPages, document.NumberOfPages);
        Assert.Equal(1, CountOccurrences(string.Join('\n', document.GetPages().Select(page => page.Text)),
            TaskPdfExporter.AggregateTruncationNotice));
        Assert.Contains(TaskPdfExporter.AggregateTruncationNotice, document.GetPage(80).Text);
        Assert.DoesNotContain("ONE-LINE-AFTER-EXACT-EXHAUSTION", document.GetPage(80).Text);
        Assert.Equal(TaskPdfExporter.PageBottom + TaskPdfExporter.BodyLineHeight, probe.Writer.MinimumRenderedY);
        Assert.True(probe.Writer.MinimumRenderedY >= TaskPdfExporter.PageBottom);
        Assert.True(probe.Writer.RenderedLines <= TaskPdfExporter.MaximumRenderedLines);
        Assert.True(probe.Writer.RenderedCharacters <= TaskPdfExporter.MaximumTotalRenderedCharacters);
    }

    [Theory]
    [InlineData("section-banner")]
    [InlineData("section-subsection")]
    [InlineData("subsection-body")]
    [InlineData("changed-file-metadata")]
    [InlineData("model-call-metadata")]
    public void Heading_groups_keep_the_actual_next_element_on_the_same_page(string scenario)
    {
        var probe = CreateFlowProbe(writer =>
        {
            writer.Body(string.Join('\n', Enumerable.Repeat("filler", scenario.StartsWith("section") ? 54 : 56)));
            switch (scenario)
            {
                case "section-banner":
                    writer.Section("SECTION-MARKER", TaskPdfExporter.FlowElementKind.BannerWithBody);
                    writer.Banner("BANNER-MARKER");
                    writer.Body("BANNER-BODY");
                    break;
                case "section-subsection":
                    writer.Section("SECTION-MARKER", TaskPdfExporter.FlowElementKind.SubsectionWithBody);
                    writer.Subsection("SUBSECTION-MARKER");
                    writer.Body("SUBSECTION-BODY");
                    break;
                case "subsection-body":
                    writer.Subsection("SUBSECTION-MARKER");
                    writer.Body("SUBSECTION-BODY");
                    break;
                case "changed-file-metadata":
                    writer.Subsection("Changed file 1: src/File.cs");
                    writer.Field("Action", "Modify");
                    break;
                default:
                    writer.Subsection("Call 1: Planning / model");
                    writer.Field("Provider", "OpenAI");
                    break;
            }
        });
        var expected = scenario switch
        {
            "section-banner" => ("SECTION-MARKER", "BANNER-MARKER"),
            "section-subsection" => ("SECTION-MARKER", "SUBSECTION-MARKER"),
            "subsection-body" => ("SUBSECTION-MARKER", "SUBSECTION-BODY"),
            "changed-file-metadata" => ("Changed file 1: src/File.cs", "Action: Modify"),
            _ => ("Call 1: Planning / model", "Provider: OpenAI")
        };

        AssertMarkersSharePage(probe.Bytes, expected.Item1, expected.Item2);
    }

    [Fact]
    public void Aggregate_truncation_during_model_call_rendering_emits_one_notice_and_omits_tail_sections()
    {
        var source = EngineeringTask.Create("C:/safe", "Export model telemetry.", Now);
        var calls = Enumerable.Range(1, 100).Select(index => new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Implementation, $"PROVIDER-{index}-" + new string('p', 49_900),
            $"model-{index}", "low", Now, Now, true, null, 1, 0, 1, 0, 1m, null)).ToArray();
        var task = EngineeringTask.Rehydrate(source.Id, source.Repository, source.OriginalRequirement,
            source.CurrentClarifiedRequirement, source.ClarificationAnswers, source.RequirementRevisionNotes,
            calls, null, source.RequirementSummary, source.Status, source.CreatedAt, source.UpdatedAt,
            source.RequirementApprovedAt, source.PlanApprovedAt);

        var bytes = Exporter().Export(task);
        var text = Extract(bytes);

        Assert.InRange(PdfPages(bytes), 1, TaskPdfExporter.MaximumReportPages);
        Assert.Equal(1, CountOccurrences(text, TaskPdfExporter.AggregateTruncationNotice));
        Assert.Contains("Call 1:", text);
        Assert.DoesNotContain("Task cost estimate", text);
    }

    [Fact]
    public void Implementation_attempt_and_failure_states_are_reported_without_claiming_a_result()
    {
        var implementing = AttemptTask(null);
        var recoverable = AttemptTask(new ImplementationFailure("implementation_interrupted",
            "Retry after review.", false, Now.AddMinutes(6), SafeToResume: true));
        var recoveryRequired = AttemptTask(new ImplementationFailure("implementation_recovery_required",
            "Explicit recovery is required.", true, Now.AddMinutes(6), SafeToResume: false,
            ActiveCheckoutVerified: false));

        var implementingText = Extract(Exporter().Export(implementing,
            new ImplementationReportRuntimeStatus(true, ImplementationAttemptDisposition.Active, null)));
        var recoverableText = Extract(Exporter().Export(recoverable,
            new ImplementationReportRuntimeStatus(true, ImplementationAttemptDisposition.SafeResume, "Safe resume observation.")));
        var recoveryText = Extract(Exporter().Export(recoveryRequired,
            new ImplementationReportRuntimeStatus(false, ImplementationAttemptDisposition.RecoveryRequired, "Workspace not observed.")));

        Assert.Contains("Implementation attempt", implementingText);
        Assert.Contains("Attempt disposition: Active", implementingText);
        Assert.Contains("No implementation result was persisted.", implementingText);
        Assert.DoesNotContain("Implementation review", implementingText);
        Assert.Contains("Attempt disposition: SafeResume", recoverableText);
        Assert.Contains("Safe to resume: yes", recoverableText);
        Assert.Contains("No implementation result was persisted.", recoverableText);
        Assert.Contains("Attempt disposition: RecoveryRequired", recoveryText);
        Assert.Contains("Failure category: implementation_recovery_required", recoveryText);
        Assert.Contains("Recovery required: yes", recoveryText);
        Assert.Contains("Active checkout verified when implementation completed: no", recoveryText);
        Assert.Contains("Valid non-reparse isolated worktree metadata observed at export time: no", recoveryText);
    }

    [Fact]
    public void Completed_result_with_unavailable_workspace_preserves_completion_evidence()
    {
        var task = ImplementedAuditTask();

        var text = Extract(Exporter().Export(task, new ImplementationReportRuntimeStatus(false,
            ImplementationAttemptDisposition.RecoveryRequired, "The workspace was not observed.")));

        Assert.Contains("Implementation review", text);
        Assert.Contains("Active checkout verified when implementation completed: yes", text);
        Assert.Contains("Valid non-reparse isolated worktree metadata observed at export time: no", text);
        Assert.Contains("Attempt disposition: RecoveryRequired", text);
    }

    [Fact]
    public void Missing_checkout_certainty_rehydrates_false_changes_fingerprint_and_cannot_produce_approved_pdf()
    {
        var source = ImplementedAuditTask();
        var node = JsonNode.Parse(JsonSerializer.Serialize(source.ImplementationResult, JsonOptions))!.AsObject();
        node.Remove("activeCheckoutVerified");
        var legacy = node.Deserialize<ImplementationResult>(JsonOptions)!;
        var task = CopyImplementationState(source, legacy, source.ImplementationWorkspace!, null);
        var revision = Assert.Single(task.ImplementationRevisions);
        var verifiedFingerprint = ImplementationReviewFingerprint.ComputeResult(
            task.Id, revision.RevisionId, revision.RevisionNumber, revision.Kind,
            revision.PlanFingerprint, legacy with { ActiveCheckoutVerified = true });

        var text = Extract(Exporter().Export(task, new ImplementationReportRuntimeStatus(false,
            ImplementationAttemptDisposition.RecoveryRequired, null)));

        Assert.False(legacy.ActiveCheckoutVerified);
        Assert.Equal(revision.ResultFingerprint, ImplementationReviewFingerprint.ComputeResult(
            task.Id, revision.RevisionId, revision.RevisionNumber, revision.Kind,
            revision.PlanFingerprint, legacy));
        Assert.NotEqual(verifiedFingerprint, revision.ResultFingerprint);
        var exception = Assert.Throws<ImplementationException>(() => task.ApproveImplementation(
            Guid.NewGuid(), task.RowVersion, revision.RevisionId, revision.ResultFingerprint!, Now.AddMinutes(7)));
        Assert.Equal("implementation_review_ineligible", exception.Category);
        Assert.Equal(WorkflowStatus.AwaitingImplementationReview, task.Status);
        Assert.Contains("NOT ELIGIBLE FOR APPROVAL", text);
        Assert.Contains("Active checkout verified when implementation completed: no", text);
        Assert.DoesNotContain("Active checkout verified when implementation completed: yes", text);
        Assert.DoesNotContain("APPROVED PERSISTED IMPLEMENTATION REVIEW", text);
    }

    [Fact]
    public void Requirement_and_plan_revision_outcomes_are_durable_and_explicit_in_report()
    {
        var requirementTask = EngineeringTask.Create("C:/safe", "Initial requirement", Now);
        requirementTask.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Initial summary"), Now);
        requirementTask.RequestRequirementRevision("Clarify the output.", Now.AddMinutes(1));
        requirementTask.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Replacement summary"), Now.AddMinutes(2));
        requirementTask.ApproveRequirementSummary(Now.AddMinutes(3));

        var accepted = PlannedAuditTask(approvePlan: false);
        accepted.RequestPlanRevision("Use a corrected title and summary.", Now.AddMinutes(4));
        accepted.StoreImplementationPlan(accepted.PlanRevisionNotes[^1].PreviousPlan with
        {
            Title = "Corrected audit title",
            Summary = "Corrected audit summary.",
            CreatedAt = Now.AddMinutes(5)
        }, Now.AddMinutes(5), TimeSpan.FromMinutes(30));
        accepted.ResolvePlanRevisionAccepted(Now.AddMinutes(5).AddSeconds(1));

        var rejected = PlannedAuditTask(approvePlan: false);
        var priorEvidence = new EvidenceSelection(rejected.EvidenceItems, rejected.EvidenceFilesInspected,
            rejected.EvidenceFilesSelected, rejected.TotalEvidenceCharacters);
        rejected.RequestPlanRevision("Exactly three Modify actions.", Now.AddMinutes(4));
        rejected.RestoreRejectedPlanRevision(priorEvidence, Now.AddMinutes(5));

        var requirementText = Extract(Exporter().Export(requirementTask));
        var acceptedText = Extract(Exporter().Export(accepted));
        var rejectedText = Extract(Exporter().Export(rejected));

        Assert.Contains("Requirement revision history", requirementText);
        Assert.Contains("Outcome: Approved", requirementText);
        Assert.Contains("replacement requirement summary was approved", requirementText);
        Assert.Contains("Plan revision history", acceptedText);
        Assert.Contains("Outcome: Accepted", acceptedText);
        Assert.Contains("corrected implementation plan was generated", acceptedText);
        Assert.Contains("Outcome: RejectedAndPreviousProposalRestored", rejectedText);
        Assert.Contains("previous proposed plan was restored", rejectedText);
        Assert.Contains("not approved automatically", rejectedText);
        Assert.Contains("PROPOSED — NOT APPROVED", rejectedText);
    }

    [Fact]
    public void Plan_revision_history_is_bounded_with_one_exact_omission_marker()
    {
        var source = PlannedAuditTask(approvePlan: false);
        var notes = Enumerable.Range(1, 30).Select(index => new PlanRevisionNote(
            $"Bounded correction {index}", Now.AddMinutes(index), source.ImplementationPlan!.Title,
            source.RepositoryFingerprint!, source.ImplementationPlan,
            PlanRevisionOutcome.Accepted, Now.AddMinutes(index).AddSeconds(1), "Corrected plan generated."))
            .ToArray();
        var task = EngineeringTask.Rehydrate(source.Id, source.Repository, source.OriginalRequirement,
            source.CurrentClarifiedRequirement, source.ClarificationAnswers, source.RequirementRevisionNotes,
            source.ModelCalls, source.CurrentPendingQuestion, source.RequirementSummary, source.Status,
            source.CreatedAt, source.UpdatedAt, source.RequirementApprovedAt, source.PlanApprovedAt,
            source.RepositorySnapshot, source.EvidenceItems, source.EvidenceFilesInspected,
            source.EvidenceFilesSelected, source.TotalEvidenceCharacters, source.ImplementationPlan,
            source.RepositoryAnalyzedAt, source.RepositoryFingerprint, source.PlanCreatedAt, notes);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("Plan revision history", text);
        Assert.Contains("Bounded correction 25", text);
        Assert.DoesNotContain("Bounded correction 26", text);
        Assert.Equal(1, CountOccurrences(text, "[5 additional entries omitted by report limit]"));
    }

    [Fact]
    public void Section_heading_and_first_body_line_are_kept_on_the_same_page()
    {
        var task = ReportTask(string.Join(' ', Enumerable.Repeat("pagination filler", 500)), "SUMMARY-BODY-MARKER");

        using var document = PdfDocument.Open(Exporter().Export(task));
        var page = Assert.Single(document.GetPages(), candidate =>
            candidate.Text.Contains("Approved requirement summary", StringComparison.Ordinal));

        Assert.Contains("SUMMARY-BODY-MARKER", page.Text);
    }

    private static TaskPdfExporter Exporter()
    {
        var pricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["current-model"] = new(987654.321m, 987654.321m, 987654.321m)
        };
        return new TaskPdfExporter(new ModelCostResolver(new ModelCostCalculator(pricing)), new FixedTimeProvider(Now));
    }

    private static EngineeringTask ReportTask(string original, string summary)
    {
        var task = EngineeringTask.Create(@"C:\sensitive\approved-repository", original, Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Ask("Which content is required?"), Now);
        task.AnswerCurrentQuestion("Requirement, clarification, and model usage.", Now.AddMinutes(1));
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize(summary), Now.AddMinutes(2));
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "current-model", "low",
            Now, Now, true, "response-1", 1_000, 250, 500, 300, 999m, null,
            new ModelPricingSnapshot(10m, 2m, 20m), "request-1"), Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "legacy-model", "medium",
            Now, Now, false, "response-2", 100, 0, 10, 5, 0m, "invalid_plan"), Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "unknown-model", "medium",
            Now, Now, false, null, null, null, null, null, null, "transport"), Now);
        return task;
    }

    private static EngineeringTask PlannedAuditTask(bool approvePlan)
    {
        var task = EngineeringTask.Create(@"C:\sensitive\audit-repository", "Generate the bounded audit report.", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved audit requirement."), Now);
        task.ApproveRequirementSummary(Now.AddMinutes(1));
        task.BeginRepositoryAnalysis(Now.AddMinutes(2));
        var snapshot = AuditSnapshot();
        var evidence = new[]
        {
            new EvidenceItem("E1", "src/GreetingService.cs", 1, 2,
                "COMPLETE-EVIDENCE-EXCERPT-MARKER", "Direct implementation evidence", 100, new string('e', 64)),
            new EvidenceItem("E2", "config/settings.json", 1, 1,
                "settings excerpt", "Configuration evidence", 90, new string('c', 64)),
            new EvidenceItem("E3", "README.md", 1, 1,
                "readme excerpt", "Documentation evidence", 80, new string('b', 64))
        };
        task.StoreRepositorySnapshot(snapshot, Now.AddMinutes(2));
        task.StoreEvidence(new EvidenceSelection(evidence, 4, 3, evidence.Sum(item => item.Excerpt.Length)), Now.AddMinutes(2));
        var plan = new ImplementationPlan(
            "Three-file audit implementation",
            "Apply the approved changes in an isolated worktree.",
            "The selected evidence identifies the approved files.",
            [
                new("src/GreetingService.cs", PlannedFileAction.Modify, "Update the greeting.", ["E1"], .95m),
                new("config/settings.json", PlannedFileAction.Modify, "Update configuration.", ["E2"], .9m),
                new("README.md", PlannedFileAction.Modify, "Document the result.", ["E3"], .9m)
            ],
            [new(1, "Apply the three approved changes.",
                ["src/GreetingService.cs", "config/settings.json", "README.md"], ["E1", "E2", "E3"],
                "Only approved files contain generated changes.")],
            ["inspect generated diff"], ["Review remains required."], ["The repository remains stable."], [],
            [new("Three approved files are changed.",
                ["src/GreetingService.cs", "config/settings.json", "README.md"], [1])],
            "Produce a bounded implementation review.", PlanningSource.DeterministicFake, null,
            Now.AddMinutes(3), snapshot.Fingerprint);
        task.StoreImplementationPlan(plan, Now.AddMinutes(3), TimeSpan.FromMinutes(30));
        if (approvePlan) task.ApproveImplementationPlan(Now.AddMinutes(4));
        return task;
    }

    private static EngineeringTask ImplementedAuditTask()
    {
        var task = PlannedAuditTask(approvePlan: true);
        const string token = "0123456789abcdef0123456789abcdef";
        var workspace = new ImplementationWorkspace(
            token, $"forge/task-{token}", new string('a', 40),
            ImplementationWorkspacePhase.Reserved, Now.AddMinutes(5), Now.AddMinutes(5), false,
            new string('1', 64), new string('2', 64), "refs/forge/tasks/audit-ownership");
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddMinutes(5), Now.AddMinutes(5), Now.AddMinutes(10));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(5));
        var reviews = new[]
        {
            Review("src/GreetingService.cs", new string('3', 64), new string('4', 64), "DIFF-PREVIEW-GREETING"),
            Review("config/settings.json", new string('5', 64), new string('6', 64), "DIFF-PREVIEW-SETTINGS"),
            Review("README.md", new string('7', 64), new string('8', 64), "DIFF-PREVIEW-README")
        };
        var fullCharacters = reviews.Sum(review => review.FullDiffCharacters);
        var fullBytes = reviews.Sum(review => review.FullDiffUtf8Bytes);
        task.StoreImplementationResult(new ImplementationResult(
            ImplementationSource.DeterministicFake, null, workspace.BaseCommitSha, workspace.Branch,
            "Deterministic implementation completed for review.",
            ["Changes are not AI-authored.", "api_key=AbCdEf1234567890secret"], reviews,
            fullCharacters, fullCharacters, false, Now.AddMinutes(6), fullBytes, fullBytes, true,
            new string('9', 64), 4, 555), lease.AttemptId, lease.OwnerId, Now.AddMinutes(6));
        return task;
    }

    private static EngineeringTask AttemptTask(ImplementationFailure? failure)
    {
        var task = PlannedAuditTask(approvePlan: true);
        const string token = "fedcba9876543210fedcba9876543210";
        var workspace = new ImplementationWorkspace(token, $"forge/task-{token}", new string('a', 40),
            ImplementationWorkspacePhase.Reserved, Now.AddMinutes(5), Now.AddMinutes(5), true,
            new string('1', 64), new string('2', 64), $"refs/forge/tasks/{token}");
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddMinutes(5), Now.AddMinutes(5), Now.AddMinutes(10));
        task.BeginImplementation(workspace, lease, Now.AddMinutes(5));
        if (failure is not null)
            task.RecordImplementationFailure(failure, lease.AttemptId, lease.OwnerId, failure.OccurredAt);
        return task;
    }

    private static EngineeringTask CopyImplementationState(
        EngineeringTask source,
        ImplementationResult? result,
        ImplementationWorkspace? workspace,
        ImplementationFailure? failure) => EngineeringTask.Rehydrate(
        source.Id, source.Repository, source.OriginalRequirement, source.CurrentClarifiedRequirement,
        source.ClarificationAnswers, source.RequirementRevisionNotes, source.ModelCalls,
        source.CurrentPendingQuestion, source.RequirementSummary, source.Status, source.CreatedAt, source.UpdatedAt,
        source.RequirementApprovedAt, source.PlanApprovedAt, source.RepositorySnapshot, source.EvidenceItems,
        source.EvidenceFilesInspected, source.EvidenceFilesSelected, source.TotalEvidenceCharacters,
        source.ImplementationPlan, source.RepositoryAnalyzedAt, source.RepositoryFingerprint, source.PlanCreatedAt,
        source.PlanRevisionNotes, workspace, result, failure, source.ImplementationStartedAt,
        source.ImplementationCompletedAt, source.ImplementationLease, source.RowVersion);

    private static ChangedFileReview Review(string path, string originalHash, string generatedHash, string marker)
    {
        var preview = $"diff --git a/{path} b/{path}\n{marker}";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(preview);
        return new ChangedFileReview(path, ImplementationOperationAction.Modify, originalHash, generatedHash,
            20, 30, 2, 3, 2, 1, preview, preview.Length, preview.Length, false, bytes, bytes);
    }

    private static RepositorySnapshot AuditSnapshot()
    {
        var files = new[]
        {
            Metadata("src/GreetingService.cs", ".cs"), Metadata("config/settings.json", ".json"),
            Metadata("README.md", ".md"), Metadata("ManualTarget.csproj", ".csproj")
        };
        return new RepositorySnapshot(@"C:\sensitive\audit-repository", true, "main", "aaaaaaaa",
            new string('a', 40), "clean", 4, 4, 0, ["C#", "JSON", "Markdown"],
            [".cs", ".json", ".md", ".csproj"], ["ManualTarget.csproj"], [], ["Snapshot warning."],
            Now.AddMinutes(2), new string('f', 64), files, new string('d', 64));
    }

    private static RepositoryFileMetadata Metadata(string path, string extension) =>
        new(path, extension, 20, 2, "source", false, null, []);

    private static string Extract(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static int PdfPages(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return document.NumberOfPages;
    }

    private static (byte[] Bytes, TaskPdfExporter.FlowWriter Writer) CreateFlowProbe(
        Action<TaskPdfExporter.FlowWriter> write,
        IReadOnlyList<string>? sensitiveIdentities = null,
        string? workspaceToken = null)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var writer = new TaskPdfExporter.FlowWriter(builder, regular, bold,
            sensitiveIdentities ?? [], workspaceToken);
        write(writer);
        return (builder.Build(), writer);
    }

    private static void AssertMarkersSharePage(byte[] bytes, string first, string second)
    {
        using var document = PdfDocument.Open(bytes);
        var page = Assert.Single(document.GetPages(), candidate => candidate.Text.Contains(first, StringComparison.Ordinal));
        Assert.Contains(second, page.Text);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
