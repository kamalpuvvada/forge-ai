using System.Globalization;
using Forge.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Forge.Infrastructure;

public sealed class ImplementationPlanPdfExporter(ModelCostResolver costResolver) : IImplementationPlanPdfExporter
{
    public byte[] Export(EngineeringTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var plan = task.ImplementationPlan
            ?? throw new WorkflowException("A complete persisted implementation plan is required for plan PDF export.");
        var approvalLabel = task.Status switch
        {
            WorkflowStatus.AwaitingPlanApproval => "PROPOSED PLAN \u2014 NOT APPROVED",
            WorkflowStatus.PlanApproved => "APPROVED PLAN",
            _ => throw new WorkflowException("Plan PDF export is available only while awaiting plan approval or after plan approval.")
        };

        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var writer = new FlowWriter(builder, regular, bold,
            new[] { task.Repository, task.RepositorySnapshot?.NormalizedRoot }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        writer.Title("Forge AI Implementation Plan");
        writer.Banner(approvalLabel);
        writer.Field("Task ID", task.Id.ToString());
        writer.Field("Workflow status", task.Status.ToString());
        writer.Field("Plan approval status", approvalLabel);
        writer.Field("Plan timestamp", (task.PlanCreatedAt ?? plan.CreatedAt).ToString("O", CultureInfo.InvariantCulture));
        writer.Field("Plan source", plan.Source.ToString());
        writer.Field("Planning model", plan.PlanningModel ?? "none (deterministic Fake plan)");

        writer.Section("Approved requirement summary");
        writer.Body(task.RequirementSummary ?? "No approved requirement summary is available.");
        writer.Section("Plan title");
        writer.Body(plan.Title);
        writer.Section("Objective");
        writer.Body(plan.Objective);
        writer.Section("Repository understanding");
        writer.Body(plan.RepositoryUnderstanding);
        writer.Section("Plan summary");
        writer.Body(plan.Summary);

        writer.Section("Affected files");
        WriteCollection(writer, plan.AffectedFiles, (file, index) =>
        {
            writer.Subsection($"File {index + 1}: {file.Path}");
            writer.Field("Action", file.Action.ToString());
            writer.Field("Purpose", file.Purpose);
            writer.Field("Evidence IDs", Join(file.EvidenceIds));
            writer.Field("Confidence", file.Confidence.ToString("P0", CultureInfo.InvariantCulture));
        });

        writer.Section("Ordered implementation steps");
        WriteCollection(writer, plan.Steps, (step, _) =>
        {
            writer.Subsection($"Step {step.Order}");
            writer.Field("Description", step.Description);
            writer.Field("Expected result", step.ExpectedResult);
            writer.Field("Affected paths", Join(step.AffectedPaths));
            writer.Field("Evidence IDs", Join(step.EvidenceIds));
        });

        writer.Section("Requirement coverage");
        WriteCollection(writer, plan.RequirementCoverage, (coverage, index) =>
        {
            writer.Subsection($"Coverage {index + 1}");
            writer.Field("Requirement", coverage.Requirement);
            writer.Field("Affected paths", Join(coverage.AffectedPaths));
            writer.Field("Step orders", coverage.StepOrders.Count == 0 ? "none" : string.Join(", ", coverage.StepOrders));
        });

        writer.Section("Proposed validation commands \u2014 NOT EXECUTED");
        WriteStrings(writer, plan.ProposedValidationCommands, value => $"NOT EXECUTED: {value}");
        writer.Section("Risks");
        WriteStrings(writer, plan.Risks);
        writer.Section("Assumptions");
        WriteStrings(writer, plan.Assumptions);
        writer.Section("Unresolved questions");
        WriteStrings(writer, plan.UnresolvedQuestions);

        writer.Section("Planning-model call telemetry");
        var planningCalls = task.ModelCalls.Where(call => call.Stage == ModelCallStage.Planning).ToArray();
        if (planningCalls.Length == 0)
        {
            writer.Body("No planning model calls were recorded for this plan.");
        }
        else
        {
            for (var index = 0; index < planningCalls.Length; index++)
                WriteCall(writer, planningCalls[index], index + 1);
        }

        writer.Body("All monetary values are estimates, not invoices.");
        return builder.Build();
    }

    private void WriteCall(FlowWriter writer, ModelCallRecord call, int number)
    {
        var resolved = costResolver.Resolve(call);
        writer.Subsection($"Planning call {number}: {call.Model}");
        writer.Field("Result", call.Succeeded ? "succeeded" : $"failed ({call.FailureCategory ?? "unspecified"})");
        writer.Field("Input tokens", FormatTokens(call.InputTokens));
        writer.Field("Cached-input tokens", FormatTokens(call.CachedInputTokens));
        writer.Field("Uncached-input tokens", FormatTokens(resolved.UncachedInputTokens));
        writer.Field("Output tokens", FormatTokens(call.OutputTokens));
        writer.Field("Estimated cost", resolved.EstimatedCostUsd is { } cost
            ? $"${cost.ToString("0.00000000", CultureInfo.InvariantCulture)} USD"
            : "unavailable");
        writer.Field("Pricing provenance", resolved.ProvenanceLabel);
        if (call.PricingSnapshot is { } snapshot)
        {
            writer.Field("Stored input rate", FormatRate(snapshot.InputPerMillionUsd));
            writer.Field("Stored cached-input rate", FormatRate(snapshot.CachedInputPerMillionUsd));
            writer.Field("Stored output rate", FormatRate(snapshot.OutputPerMillionUsd));
        }
    }

    private static void WriteCollection<T>(FlowWriter writer, IReadOnlyList<T> values, Action<T, int> write)
    {
        if (values.Count == 0) { writer.Body("None recorded."); return; }
        for (var index = 0; index < values.Count; index++) write(values[index], index);
    }

    private static void WriteStrings(FlowWriter writer, IReadOnlyList<string> values, Func<string, string>? format = null)
    {
        if (values.Count == 0) { writer.Body("None recorded."); return; }
        foreach (var value in values) writer.Body($"- {(format ?? (item => item))(value)}");
    }

    private static string Join<T>(IReadOnlyList<T> values) => values.Count == 0 ? "none" : string.Join(", ", values);
    private static string FormatTokens(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string FormatRate(decimal value) => $"${value.ToString("0.########", CultureInfo.InvariantCulture)} USD per million tokens";

    private sealed class FlowWriter(
        PdfDocumentBuilder builder,
        PdfDocumentBuilder.AddedFont regular,
        PdfDocumentBuilder.AddedFont bold,
        IReadOnlyList<string> excludedRepositoryRoots)
    {
        private const decimal PageTop = 800m;
        private const decimal PageBottom = 48m;
        private const decimal Left = 48m;
        private PdfPageBuilder? _page;
        private decimal _y;

        public void Title(string value) => Lines(value, 17m, 22m, bold, 54, 10m);
        public void Banner(string value) => Lines(value, 13m, 18m, bold, 70, 8m);
        public void Section(string value) { Space(8); Lines(value, 13m, 18m, bold, 70, 4m); }
        public void Subsection(string value) => Lines(value, 11m, 16m, bold, 78, 2m);
        public void Field(string label, string value) => Body($"{label}: {value}");
        public void Body(string value) => Lines(value, 9.5m, 13m, regular, 92, 2m);
        private void Space(decimal points) => _y -= points;

        private void Lines(string value, decimal fontSize, decimal lineHeight, PdfDocumentBuilder.AddedFont font, int width, decimal after)
        {
            foreach (var excludedRepositoryRoot in excludedRepositoryRoots)
                value = value.Replace(excludedRepositoryRoot, "[repository root]", StringComparison.OrdinalIgnoreCase);
            foreach (var line in TaskPdfExporter.Wrap(value, width))
            {
                EnsurePage(lineHeight);
                _page!.AddText(line, (double)fontSize, new PdfPoint((double)Left, (double)_y), font);
                _y -= lineHeight;
            }
            _y -= after;
        }

        private void EnsurePage(decimal lineHeight)
        {
            if (_page is not null && _y - lineHeight >= PageBottom) return;
            _page = builder.AddPage(PageSize.A4);
            _y = PageTop;
        }
    }
}
