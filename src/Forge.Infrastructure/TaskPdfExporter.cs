using System.Buffers;
using System.Globalization;
using System.Text;
using Forge.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Forge.Infrastructure;

public sealed class TaskPdfExporter(ModelCostResolver costResolver) : IEngineeringTaskPdfExporter
{
    private const decimal PageTop = 800m;
    private const decimal PageBottom = 48m;
    private const decimal Left = 48m;
    private const decimal BodyFontSize = 9.5m;
    private const decimal BodyLineHeight = 13m;
    private const int BodyCharactersPerLine = 92;

    public byte[] Export(EngineeringTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var writer = new FlowWriter(builder, regular, bold);

        writer.Title("Forge AI Engineering Task Report");
        writer.Field("Task ID", task.Id.ToString());
        writer.Field("Workflow status", task.Status.ToString());

        writer.Section("Original requirement");
        writer.Body(task.OriginalRequirement);

        writer.Section("Clarification questions and answers");
        if (task.ClarificationAnswers.Count == 0)
        {
            writer.Body("No clarification questions were recorded.");
        }
        else
        {
            for (var index = 0; index < task.ClarificationAnswers.Count; index++)
            {
                var answer = task.ClarificationAnswers[index];
                writer.Body($"Question {index + 1}: {answer.Question}");
                writer.Body($"Answer {index + 1}: {answer.Answer}");
                writer.Space(3);
            }
        }

        writer.Section("Approved requirement summary");
        writer.Body(task.RequirementSummary ?? "No approved requirement summary is available.");

        writer.Section("Model-call usage and estimated cost");
        writer.Field("Model-call count", task.ModelCalls.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < task.ModelCalls.Count; index++)
            WriteCall(writer, task.ModelCalls[index], index + 1);

        var total = costResolver.ResolveTotal(task.ModelCalls);
        writer.Section("Task cost estimate");
        writer.Field("Available estimated total", FormatCost(total.TotalEstimatedCostUsd));
        writer.Body(task.ModelCalls.Count == 0
            ? "No model calls were recorded for this task."
            : total.IsPartial
                ? $"This is a partial estimate. {total.UnavailableCallCount} model call(s) were excluded because their cost was unavailable."
                : "All recorded model calls have an available estimated cost.");
        writer.Body("All monetary values are estimates, not invoices.");

        return builder.Build();
    }

    internal static string NormalizeSupportedText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\u2018", "'", StringComparison.Ordinal).Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("\u201c", "\"", StringComparison.Ordinal).Replace("\u201d", "\"", StringComparison.Ordinal)
            .Replace("\u2013", "-", StringComparison.Ordinal)
            .Replace("\u2026", "...", StringComparison.Ordinal).Replace("\u2192", "->", StringComparison.Ordinal);
        var result = new StringBuilder(normalized.Length);
        var span = normalized.AsSpan();
        while (!span.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(span, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                result.Append('?');
                break;
            }
            if (rune.Value is '\n' or '\t' or 0x2014 || rune.Value is >= 32 and <= 126)
                result.Append(rune.ToString());
            else
                result.Append('?');
            span = span[consumed..];
        }
        return result.ToString();
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

    private void WriteCall(FlowWriter writer, ModelCallRecord call, int number)
    {
        var resolved = costResolver.Resolve(call);
        writer.Subsection($"Call {number}: {call.Stage} / {call.Model}");
        writer.Field("Result", call.Succeeded ? "succeeded" : $"failed ({call.FailureCategory ?? "unspecified"})");
        writer.Field("Total input tokens", FormatTokens(call.InputTokens));
        writer.Field("Cached-input tokens", FormatTokens(call.CachedInputTokens));
        writer.Field("Uncached-input tokens", FormatTokens(resolved.UncachedInputTokens));
        writer.Field("Output tokens", FormatTokens(call.OutputTokens));
        writer.Field("Estimated cost", resolved.EstimatedCostUsd is { } cost ? FormatCost(cost) : "unavailable");
        writer.Field("Pricing provenance", resolved.ProvenanceLabel);
        if (call.PricingSnapshot is { } snapshot)
        {
            writer.Field("Stored input rate", FormatRate(snapshot.InputPerMillionUsd));
            writer.Field("Stored cached-input rate", FormatRate(snapshot.CachedInputPerMillionUsd));
            writer.Field("Stored output rate", FormatRate(snapshot.OutputPerMillionUsd));
        }
        writer.Space(4);
    }

    private static string FormatTokens(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string FormatCost(decimal value) => $"${value.ToString("0.00000000", CultureInfo.InvariantCulture)} USD";
    private static string FormatRate(decimal value) => $"${value.ToString("0.########", CultureInfo.InvariantCulture)} USD per million tokens";

    private sealed class FlowWriter(
        PdfDocumentBuilder builder,
        PdfDocumentBuilder.AddedFont regular,
        PdfDocumentBuilder.AddedFont bold)
    {
        private PdfPageBuilder? _page;
        private decimal _y;

        public void Title(string value) => Lines(value, 17m, 22m, bold, 54, 12m);
        public void Section(string value)
        {
            Space(9);
            Lines(value, 13m, 18m, bold, 70, 5m);
        }
        public void Subsection(string value) => Lines(value, 11m, 16m, bold, 78, 3m);
        public void Field(string label, string value) => Body($"{label}: {value}");
        public void Body(string value) => Lines(value, BodyFontSize, BodyLineHeight, regular, BodyCharactersPerLine, 2m);
        public void Space(decimal points) => _y -= points;

        private void Lines(
            string value,
            decimal fontSize,
            decimal lineHeight,
            PdfDocumentBuilder.AddedFont font,
            int maximumCharacters,
            decimal after)
        {
            foreach (var line in Wrap(value, maximumCharacters))
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
