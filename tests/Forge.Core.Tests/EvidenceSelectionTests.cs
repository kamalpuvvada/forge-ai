using Forge.Core;
using Forge.Infrastructure;
using Xunit.Abstractions;

namespace Forge.Core.Tests;

public sealed class EvidenceSelectionTests(ITestOutputHelper output)
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
        Assert.DoesNotContain(result.Items, item => item.RelativePath == "src/Unrelated.cs");
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
    public async Task Sensitive_values_and_unsafe_paths_never_enter_evidence_or_plan()
    {
        const string credential = "super-secret-credential";
        var files = new[]
        {
            TextFile("src/report.json", $"{{\n  \"reportToken\": \"{credential}\",\n  \"feature\": \"report export\"\n}}"),
            TextFile("../outside.cs", $"password={credential}\nreport export")
        };
        var snapshot = Snapshot(files);

        var evidence = Selector().Select(snapshot, files, "report export", "report export", []);
        var plan = (await new FakePlanningEngine().CreatePlanAsync(new PlanningContext(
            "report export", "report export", [], [], snapshot, evidence.Items, Now))).Plan;
        var serializedPlan = System.Text.Json.JsonSerializer.Serialize(plan);

        Assert.DoesNotContain(credential, string.Join(' ', evidence.Items.Select(item => item.Excerpt)));
        Assert.Contains("[REDACTED]", evidence.Items[0].Excerpt);
        Assert.DoesNotContain(credential, serializedPlan);
        Assert.DoesNotContain(evidence.Items, item => item.RelativePath.Contains(".."));
    }

    [Fact]
    public void Export_requirement_selects_cross_layer_companions_and_deprioritizes_generic_docs_and_clarification()
    {
        var files = new[]
        {
            TextFile("web/forge-web/src/ExportButton.tsx", "export function ExportButton() { return 'task report export'; }"),
            TextFile("src/Forge.Api/Controllers/TasksController.cs", "class TasksController { void ExportTaskReport() {} }"),
            TextFile("src/Forge.Core/EngineeringTask.cs", "class EngineeringTask { string ReportData = string.Empty; }"),
            TextFile("src/Forge.Infrastructure/ReportRenderer.cs", "class ReportRenderer { void RenderTaskReport() {} }"),
            TextFile("tests/Forge.Api.Tests/TaskExportTests.cs", "class TaskExportTests { void ExportTaskReport() {} }"),
            TextFile("src/Forge.Infrastructure/OpenAIClarificationEngine.cs", "class OpenAIClarificationEngine { object taskModel = new(); }"),
            TextFile("README.md", "Forge task model status and current repository documentation")
        };

        var result = Selector().Select(Snapshot(files), files,
            "Export the approved task report in a portable document format from the task screen.",
            "Provide task report export through the web screen and API with focused tests.", []);
        var paths = result.Items.Select(item => item.RelativePath).ToArray();

        Assert.Contains("web/forge-web/src/ExportButton.tsx", paths);
        Assert.Contains("src/Forge.Api/Controllers/TasksController.cs", paths);
        Assert.Contains("src/Forge.Core/EngineeringTask.cs", paths);
        Assert.Contains("tests/Forge.Api.Tests/TaskExportTests.cs", paths);
        Assert.DoesNotContain("README.md", paths);
        Assert.DoesNotContain("src/Forge.Infrastructure/OpenAIClarificationEngine.cs", paths);
    }

    [Fact]
    public void Strong_multiword_phrases_beat_weak_generic_terms()
    {
        var files = new[]
        {
            TextFile("src/GenericTaskService.cs", "task service request response model status"),
            TextFile("src/ReportExportPipeline.cs", "build the report export pipeline")
        };

        var result = Selector().Select(Snapshot(files), files, "Add report export to the task service", "Report export", []);

        Assert.Equal("src/ReportExportPipeline.cs", result.Items[0].RelativePath);
        Assert.Contains("strong requirement phrase", result.Items[0].ReasonSelected);
    }

    [Fact]
    public async Task Forge_repository_pdf_export_requirement_selects_diverse_bounded_evidence()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var limits = new RepositoryAnalysisLimits();
        var discovery = await new RepositoryDiscoveryService(limits, TimeProvider.System).DiscoverAsync(repositoryRoot);
        var result = Selector(limits).Select(discovery.Snapshot, discovery.TextFiles,
            "Add browser-downloadable task PDF export from the task screen.",
            "The approved task report includes requirement, plan, evidence, and telemetry in a portable document exposed through the UI and API.", []);
        output.WriteLine($"SUMMARY inspected={result.FilesInspected} selected={result.FilesSelected} characters={result.TotalCharacters}");
        foreach (var item in result.Items)
            output.WriteLine($"{item.Id} {item.RelativePath} score={item.Score} reason={item.ReasonSelected}");

        Assert.InRange(result.Items.Count, 4, 12);
        Assert.True(result.TotalCharacters <= 60_000);
        Assert.Contains(result.Items, item => item.RelativePath.StartsWith("web/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item => item.RelativePath.StartsWith("src/Forge.Api/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item => item.RelativePath.StartsWith("src/Forge.Core/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item => item.RelativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Items.Count(item => item.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) <= 1);
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
