using Forge.Core;
using Forge.Infrastructure;
using UglyToad.PdfPig;

namespace Forge.Core.Tests;

public sealed class ImplementationPlanPdfExporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Proposed_pdf_contains_exact_label_complete_plan_and_safe_telemetry_without_mutation()
    {
        var task = PlannedTask();
        var before = Snapshot(task);

        var bytes = Exporter().Export(task);
        var text = Extract(bytes);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("PROPOSED PLAN \u2014 NOT APPROVED", text);
        Assert.DoesNotContain("APPROVED PLAN", text);
        Assert.Contains("Forge AI Implementation Plan", text);
        Assert.Contains(task.Id.ToString(), text);
        Assert.Contains("Approved plan PDF requirement", text);
        Assert.Contains("Implementation plan PDF", text);
        Assert.Contains("Export the complete persisted plan", text);
        Assert.Contains("Repository evidence identifies the export service.", text);
        Assert.Contains("src/App.cs", text);
        Assert.Contains("Modify", text);
        Assert.Contains("E1", text);
        Assert.Contains("Confidence: 90", text);
        Assert.Contains("Add the plan-specific exporter.", text);
        Assert.Contains("The plan can be exported without mutation.", text);
        Assert.Contains("Acceptance criterion", text);
        Assert.Contains("Step orders: 1", text);
        Assert.Contains("Proposed validation commands \u2014 NOT EXECUTED", text);
        Assert.Contains("NOT EXECUTED: dotnet test ForgeAI.slnx", text);
        Assert.Contains("Risk one", text);
        Assert.Contains("Assumption one", text);
        Assert.Contains("Question one", text);
        Assert.Contains("OpenAI", text);
        Assert.Contains("planning-model", text);
        Assert.Contains("Input tokens: 1,000", text);
        Assert.Contains("Cached-input tokens: 250", text);
        Assert.Contains("Uncached-input tokens: 750", text);
        Assert.Contains("Output tokens: 500", text);
        Assert.Contains("Estimated cost: $0.01800000 USD", text);
        Assert.Contains("stored pricing snapshot", text);
        Assert.Contains("Stored input rate: $10 USD per million tokens", text);
        Assert.Contains("estimates, not invoices", text);
        Assert.DoesNotContain(task.Repository, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-response-secret", text);
        Assert.Equal(before, Snapshot(task));
    }

    [Fact]
    public void Approved_pdf_uses_exact_approved_label()
    {
        var task = PlannedTask();
        task.ApproveImplementationPlan(Now.AddMinutes(1));

        var text = Extract(Exporter().Export(task));

        Assert.Contains("APPROVED PLAN", text);
        Assert.DoesNotContain("PROPOSED PLAN \u2014 NOT APPROVED", text);
    }

    [Theory]
    [InlineData(WorkflowStatus.Implementing)]
    [InlineData(WorkflowStatus.AwaitingImplementationReview)]
    [InlineData(WorkflowStatus.ImplementationApproved)]
    [InlineData(WorkflowStatus.Validating)]
    [InlineData(WorkflowStatus.Reviewing)]
    [InlineData(WorkflowStatus.Completed)]
    public void Persisted_approval_timestamp_is_semantic_for_every_later_state(WorkflowStatus status)
    {
        var approved = PlannedTask();
        approved.ApproveImplementationPlan(Now.AddMinutes(1));
        var task = EngineeringTask.Rehydrate(
            approved.Id, approved.Repository, approved.OriginalRequirement, approved.CurrentClarifiedRequirement,
            approved.ClarificationAnswers, approved.RequirementRevisionNotes, approved.ModelCalls,
            approved.CurrentPendingQuestion, approved.RequirementSummary, status, approved.CreatedAt,
            approved.UpdatedAt, approved.RequirementApprovedAt, approved.PlanApprovedAt,
            approved.RepositorySnapshot, approved.EvidenceItems, approved.EvidenceFilesInspected,
            approved.EvidenceFilesSelected, approved.TotalEvidenceCharacters, approved.ImplementationPlan,
            approved.RepositoryAnalyzedAt, approved.RepositoryFingerprint, approved.PlanCreatedAt,
            approved.PlanRevisionNotes);

        var text = Extract(Exporter().Export(task));

        Assert.Contains("APPROVED PLAN", text);
        Assert.DoesNotContain("PROPOSED PLAN \u2014 NOT APPROVED", text);
    }

    [Fact]
    public void Export_rejects_tasks_without_an_available_complete_plan()
    {
        var noPlan = EngineeringTask.Create("C:/repo", "Requirement", Now);
        Assert.Throws<WorkflowException>(() => Exporter().Export(noPlan));
    }

    [Fact]
    public void Long_plan_wraps_and_paginates_and_normalizes_supported_punctuation()
    {
        var task = PlannedTask();
        static string Repeat(string value, int count) => string.Join(' ', Enumerable.Repeat(value, count));
        var longPurpose = Repeat("Wrapped purpose with em dash \u2014 and smart ‘quotes’.", 7);
        var plan = task.ImplementationPlan! with
        {
            Objective = Repeat("Long objective detail.", 30),
            RepositoryUnderstanding = Repeat("Bounded repository understanding.", 32),
            Summary = Repeat("Concise plan summary detail.", 35),
            AffectedFiles = [task.ImplementationPlan.AffectedFiles[0] with { Purpose = longPurpose }],
            Steps = Enumerable.Range(1, 6).Select(order => new ImplementationStep(order,
                Repeat($"Step {order} wrapped description.", 18), ["src/App.cs"], ["E1"],
                Repeat("Expected persisted result.", 16) + (order == 6 ? " PLAN-TAIL-MARKER" : string.Empty))).ToArray(),
            Risks = Enumerable.Range(1, 4).Select(index => Repeat($"Risk {index} detail.", 25)).ToArray(),
            Assumptions = Enumerable.Range(1, 4).Select(index => Repeat($"Assumption {index} detail.", 22)).ToArray()
        };
        task.RequestPlanRevision("Use a long representative plan.", Now.AddMinutes(1));
        task.StoreEvidence(new EvidenceSelection([PlanningWorkflowTests.Evidence()], 1, 1, 20), Now.AddMinutes(1));
        task.StoreImplementationPlan(plan, Now.AddMinutes(2), TimeSpan.FromMinutes(30));

        var bytes = Exporter().Export(task);
        using var document = PdfDocument.Open(bytes);
        var text = string.Join('\n', document.GetPages().Select(page => page.Text));

        Assert.True(document.NumberOfPages > 1);
        Assert.Contains("em dash \u2014 and smart 'quotes'", text);
        Assert.Contains("PLAN-TAIL-MARKER", text);
    }

    private static ImplementationPlanPdfExporter Exporter() => new(new ModelCostResolver(
        new ModelCostCalculator(new Dictionary<string, ModelPricing>())));

    private static EngineeringTask PlannedTask()
    {
        var task = EngineeringTask.Create(@"C:\sensitive\repository-root", "Original requirement secret marker", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved plan PDF requirement"), Now);
        task.ApproveRequirementSummary(Now);
        task.BeginRepositoryAnalysis(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        var plan = PlanningWorkflowTests.Plan(snapshot, [evidence]) with
        {
            Title = "Implementation plan PDF",
            Objective = "Export the complete persisted plan.",
            RepositoryUnderstanding = "Repository evidence identifies the export service.",
            AffectedFiles = [new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Add safe plan export.", ["E1"], .9m)],
            Steps = [new ImplementationStep(1, "Add the plan-specific exporter.", ["src/App.cs"], ["E1"], "The plan can be exported without mutation.")],
            RequirementCoverage = [new RequirementCoverageItem("Acceptance criterion", ["src/App.cs"], [1])],
            ProposedValidationCommands = ["dotnet test ForgeAI.slnx"],
            Risks = ["Risk one"], Assumptions = ["Assumption one"], UnresolvedQuestions = ["Question one"],
            Summary = "Complete implementation plan export.", Source = PlanningSource.OpenAI, PlanningModel = "planning-model"
        };
        task.StoreImplementationPlan(plan, Now, TimeSpan.FromMinutes(30));
        task.RecordModelCall(new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "planning-model", "medium",
            Now, Now.AddSeconds(2), true, "provider-response-secret", 1_000, 250, 500, 100, 999m, null,
            new ModelPricingSnapshot(10m, 2m, 20m)), Now);
        return task;
    }

    private static string Extract(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }

    private static string Snapshot(EngineeringTask task) => System.Text.Json.JsonSerializer.Serialize(task);
}
