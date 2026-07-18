using Forge.Core;
using Forge.Infrastructure;
using UglyToad.PdfPig;

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

        Assert.Contains("Available estimated total: $0.00000000 USD", text);
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

    private static TaskPdfExporter Exporter()
    {
        var pricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["current-model"] = new(987654.321m, 987654.321m, 987654.321m)
        };
        return new TaskPdfExporter(new ModelCostResolver(new ModelCostCalculator(pricing)));
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
            new ModelPricingSnapshot(10m, 2m, 20m)), Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "legacy-model", "medium",
            Now, Now, false, "response-2", 100, 0, 10, 5, 0m, "invalid_plan"), Now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "unknown-model", "medium",
            Now, Now, false, null, null, null, null, null, null, "transport"), Now);
        return task;
    }
}
