using Forge.Core;
using Forge.Infrastructure;
using UglyToad.PdfPig;

namespace Forge.Core.Tests;

public sealed class RepositoryDisplayIdentifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Identifier_is_stable_distinct_bounded_and_contains_no_path_components()
    {
        const string firstPath = @"C:\Users\RecognizableUser\SecretClient\ForgeRepo";
        const string secondPath = @"D:\OtherUser\AnotherClient\ForgeRepo";

        var first = RepositoryDisplayIdentifier.Create(firstPath);
        var repeated = RepositoryDisplayIdentifier.Create(firstPath);
        var second = RepositoryDisplayIdentifier.Create(secondPath);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
        Assert.Matches("^Repository [0-9a-f]{16}$", first);
        Assert.DoesNotContain("RecognizableUser", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretClient", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fake_summary_and_task_and_plan_pdfs_never_embed_the_repository_path()
    {
        const string repository = @"C:\Users\RecognizableUser\SecretClient\ForgeRepo";
        const string recognizableUser = "RecognizableUser";
        const string recognizableDirectory = "SecretClient";
        var task = EngineeringTask.Create(repository,
            "Add a bounded report. Acceptance criteria: the report is available. Validation: inspect the report.", Now);
        var clarification = await new FakeClarificationEngine().EvaluateAsync(task);
        task.ApplyClarificationEvaluation(clarification, Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now) with { NormalizedRoot = repository };
        var evidence = PlanningWorkflowTests.Evidence();
        task.BeginRepositoryAnalysis(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), Now,
            TimeSpan.FromMinutes(30));

        var pricing = new ModelCostResolver(new ModelCostCalculator(new Dictionary<string, ModelPricing>()));
        var taskText = Extract(new TaskPdfExporter(pricing).Export(task));
        var planText = Extract(new ImplementationPlanPdfExporter(pricing).Export(task));
        var exposed = string.Join('\n', task.RequirementSummary, taskText, planText);

        Assert.Contains(RepositoryDisplayIdentifier.Create(repository), task.RequirementSummary);
        Assert.Contains(
            "Development note: requirement summary assembled by deterministic fake logic. At this summary-generation stage, no repository inspection or AI model call had occurred.",
            task.RequirementSummary);
        Assert.DoesNotContain(repository, exposed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recognizableUser, exposed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recognizableDirectory, exposed, StringComparison.OrdinalIgnoreCase);
    }

    private static string Extract(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }
}
