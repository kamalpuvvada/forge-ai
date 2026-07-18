using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class EvidenceSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Filename_symbol_and_content_signals_rank_relevant_file_first()
    {
        var files = new[]
        {
            TextFile("src/ExportService.cs", "public class ExportService { public void ExportReport() { } }"),
            TextFile("src/Unrelated.cs", "public class Unrelated { }")
        };

        var result = Selector().Select(Snapshot(files), files, "Export task report", "Support report export", []);

        Assert.Equal("src/ExportService.cs", result.Items[0].RelativePath);
        Assert.Contains("path", result.Items[0].ReasonSelected);
        Assert.True(result.Items[0].Score > result.Items[1].Score);
    }

    [Fact]
    public void Snippet_preserves_line_range_and_has_no_duplicates()
    {
        var content = string.Join('\n', Enumerable.Range(1, 20).Select(index => index == 10 ? "export report implementation" : $"line {index}"));
        var files = new[] { TextFile("src/report.ts", content) };

        var result = Selector().Select(Snapshot(files), files, "report export", "report export", []);

        var item = Assert.Single(result.Items);
        Assert.InRange(10, item.StartLine, item.EndLine);
        Assert.Contains("export report implementation", item.Excerpt);
        Assert.Equal(1, result.FilesSelected);
    }

    [Fact]
    public void Evidence_count_and_total_characters_are_bounded()
    {
        var limits = new RepositoryAnalysisLimits { MaximumEvidenceFiles = 2, MaximumEvidenceCharacters = 40, MaximumEvidenceCharactersPerFile = 30 };
        var files = Enumerable.Range(1, 5).Select(index => TextFile($"src/report{index}.cs", new string('r', 100))).ToArray();

        var result = Selector(limits).Select(Snapshot(files), files, "report", "report", []);

        Assert.True(result.Items.Count <= 2);
        Assert.True(result.TotalCharacters <= 40);
        Assert.Equal(result.Items.Count, result.Items.Select(item => item.RelativePath).Distinct().Count());
    }

    [Fact]
    public void Sensitive_values_and_unsafe_paths_never_enter_evidence_or_plan()
    {
        const string credential = "super-secret-credential";
        var files = new[]
        {
            TextFile("src/report.json", $"{{\n  \"reportToken\": \"{credential}\",\n  \"feature\": \"report export\"\n}}"),
            TextFile("../outside.cs", $"password={credential}\nreport export")
        };
        var snapshot = Snapshot(files);

        var evidence = Selector().Select(snapshot, files, "report export", "report export", []);
        var plan = new FakePlanningEngine().CreatePlan(new PlanningContext("report export", "report export", snapshot, evidence.Items, Now));
        var serializedPlan = System.Text.Json.JsonSerializer.Serialize(plan);

        Assert.DoesNotContain(credential, string.Join(' ', evidence.Items.Select(item => item.Excerpt)));
        Assert.Contains("[REDACTED]", evidence.Items[0].Excerpt);
        Assert.DoesNotContain(credential, serializedPlan);
        Assert.DoesNotContain(evidence.Items, item => item.RelativePath.Contains(".."));
    }

    private static DeterministicEvidenceSelectionService Selector(RepositoryAnalysisLimits? limits = null) => new(limits ?? new RepositoryAnalysisLimits());

    private static RepositoryTextFile TextFile(string path, string content) => new(
        new RepositoryFileMetadata(path, Path.GetExtension(path), content.Length, content.Count(character => character == '\n') + 1,
            "source", path.Contains("test", StringComparison.OrdinalIgnoreCase), "src", ExtractSymbols(content)),
        content);

    private static IReadOnlyList<string> ExtractSymbols(string content) => content.Contains("ExportService") ? ["ExportService", "ExportReport"] : [];

    private static RepositorySnapshot Snapshot(IReadOnlyList<RepositoryTextFile> files) => new(
        "C:/repo", false, null, null, null, "unknown", files.Count, files.Count, 0,
        ["C#/.NET"], [".cs"], ["Forge.slnx"], [], [], Now, "fingerprint", files.Select(file => file.Metadata).ToArray());
}
