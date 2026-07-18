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
    public void Selected_tsx_relative_import_boosts_material_api_companion_over_documentation()
    {
        var limits = new RepositoryAnalysisLimits { MaximumEvidenceFiles = 2, MaximumEvidenceCharacters = 60_000 };
        var files = new[]
        {
            TextFile("web/src/App.tsx", "import { exportReport } from './api'\nexport function App() { return exportReport('task report') }"),
            TextFile("web/src/api.ts", "export const exportReport = (value: string) => value"),
            TextFile("README.md", "task report export export export documentation")
        };

        var result = Selector(limits).Select(Snapshot(files), files, "Export the task report from the UI", "Add task report export through the frontend API helper.", []);

        Assert.Equal(new[] { "web/src/api.ts", "web/src/App.tsx" }, result.Items.Select(item => item.RelativePath));
        Assert.Contains("selected local TypeScript/JavaScript import companion", result.Items[0].ReasonSelected);
        Assert.DoesNotContain(result.Items, item => item.RelativePath == "README.md");
    }

    [Fact]
    public void Extensionless_tsx_and_local_index_imports_resolve()
    {
        var files = new[]
        {
            TextFile("web/src/App.tsx", "import { Panel } from './components/Panel'\nimport { exportReport } from './services'\nexport const App = () => exportReport(Panel)"),
            TextFile("web/src/components/Panel.tsx", "export const Panel = 'task report panel'"),
            TextFile("web/src/services/index.ts", "export const exportReport = (value: unknown) => value")
        };

        var paths = Selector().Select(Snapshot(files), files, "Export task report panel", "Export the task report from the UI.", [])
            .Items.Select(item => item.RelativePath).ToArray();

        Assert.Contains("web/src/components/Panel.tsx", paths);
        Assert.Contains("web/src/services/index.ts", paths);
    }

    [Fact]
    public void Package_and_out_of_repository_traversal_imports_are_not_expanded()
    {
        var files = new[]
        {
            TextFile("web/src/App.tsx", "import React from 'react'\nimport { hidden } from '../../../outside/api'\nexport const App = () => 'task report export'"),
            TextFile("web/src/react.ts", "export const React = true"),
            TextFile("outside/api.ts", "export const hidden = 'secret'"),
            TextFile("src/ExportService.cs", "class ExportService { void ExportTaskReport() {} }")
        };

        var result = Selector().Select(Snapshot(files), files, "Export task report", "Export task report from the UI and service.", []);

        Assert.DoesNotContain(result.Items, item => item.ReasonSelected.Contains("import companion", StringComparison.Ordinal) &&
            (item.RelativePath is "web/src/react.ts" or "outside/api.ts"));
    }

    [Fact]
    public void Companion_expansion_preserves_file_and_character_limits_and_test_layers()
    {
        var limits = new RepositoryAnalysisLimits { MaximumEvidenceFiles = 4, MaximumEvidenceCharacters = 120, MaximumEvidenceCharactersPerFile = 60 };
        var files = new[]
        {
            TextFile("web/src/App.tsx", "import { exportReport } from './api'\nexport const App = () => 'task report export frontend'"),
            TextFile("web/src/api.ts", "export const exportReport = () => 'task report export api'"),
            TextFile("web/src/App.test.tsx", "test('task report export frontend', () => {})"),
            TextFile("tests/TaskExportTests.cs", "class TaskExportTests { void TaskReportExportBackend() {} }"),
            TextFile("README.md", "task report export documentation")
        };

        var result = Selector(limits).Select(Snapshot(files), files,
            "Export task report with backend and frontend tests", "Add task report export with backend and frontend tests.", []);

        Assert.True(result.Items.Count <= 4);
        Assert.True(result.TotalCharacters <= 120);
        Assert.Contains(result.Items, item => item.RelativePath == "web/src/api.ts");
        Assert.Contains(result.Items, item => item.RelativePath == "web/src/App.test.tsx");
        Assert.Contains(result.Items, item => item.RelativePath == "tests/TaskExportTests.cs");
    }

    [Fact]
    public void Csharp_symbol_relationship_boosts_service_and_test_companions()
    {
        var controllerMetadata = new RepositoryFileMetadata("src/Api/ReportController.cs", ".cs", 100, 2,
            "source", false, "src", ["ReportController"]);
        var serviceMetadata = new RepositoryFileMetadata("src/Core/IReportService.cs", ".cs", 80, 1,
            "source", false, "src", ["IReportService"]);
        var testMetadata = new RepositoryFileMetadata("tests/ReportControllerTests.cs", ".cs", 80, 1,
            "source", true, "tests", ["ReportControllerTests"]);
        var files = new[]
        {
            new RepositoryTextFile(controllerMetadata, "class ReportController { ReportController(IReportService service) {} void ExportTaskReport() {} }"),
            new RepositoryTextFile(serviceMetadata, "interface IReportService { void ExportTaskReport(); }"),
            new RepositoryTextFile(testMetadata, "class ReportControllerTests { ReportController controller; void ExportTaskReport() {} }")
        };

        var result = Selector().Select(Snapshot(files), files, "Export task reports through the API with tests.",
            "Add task report export through the controller and service with backend tests.", []);

        Assert.Contains(result.Items, item => item.RelativePath == "src/Core/IReportService.cs" &&
            item.ReasonSelected.Contains("selected C# symbol companion", StringComparison.Ordinal));
        Assert.Contains(result.Items, item => item.RelativePath == "tests/ReportControllerTests.cs" &&
            item.ReasonSelected.Contains("selected C# symbol companion", StringComparison.Ordinal));
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
        Assert.Contains(result.Items, item => item.RelativePath == "web/forge-web/src/api.ts");
        Assert.Contains(result.Items, item => item.RelativePath.StartsWith("src/Forge.Api/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item => item.RelativePath.StartsWith("src/Forge.Core/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Items, item => item.RelativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Items.Count(item => item.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) <= 1);
    }

    [Fact]
    public void Pricing_snapshot_correction_uses_identifier_tokens_and_cross_layer_phrases()
    {
        var files = new[]
        {
            TextFile("src/Forge.Core/ModelCallRecord.cs", "public sealed record ModelCallRecord(decimal EstimatedCostUsd, int CachedInputTokens);"),
            TextFile("src/Forge.Infrastructure/ModelCostCalculator.cs", "public sealed class ModelCostCalculator { decimal EstimateCost() => 0; }") ,
            TextFile("src/Forge.Infrastructure/SqliteEngineeringTaskRepository.cs", "class SqliteEngineeringTaskRepository { void PersistModelCallPricingSnapshot() {} }") ,
            TextFile("src/Forge.Api/Contracts/EngineeringTaskResponse.cs", "record ModelCallResponse(decimal EstimatedCostUsd, string LegacyFallbackLabel);"),
            TextFile("tests/Forge.Core.Tests/SqlitePersistenceTests.cs", "class SqlitePersistenceTests { void PricingSnapshotRoundTrips() {} }") ,
            TextFile("src/Forge.Infrastructure/OpenAIClarificationEngine.cs", "class OpenAIClarificationEngine { string model; string requirement; }") ,
            TextFile("src/Forge.Infrastructure/OpenAIPlanningEngine.cs", "class OpenAIPlanningEngine { string model; string task; string status; }")
        };
        const string correction = "The plan omits persistence of per-call pricing snapshots. Include the model-call domain record, cost capture, SQLite migration/persistence, API/export representation, and legacy fallback labels. Do not solve this only in the PDF exporter.";

        var result = Selector().SelectForPlanRevision(Snapshot(files), files, "Export the approved task report as a PDF.", correction);
        var paths = result.Items.Select(item => item.RelativePath).ToArray();

        Assert.Contains("src/Forge.Core/ModelCallRecord.cs", paths);
        Assert.Contains("src/Forge.Infrastructure/ModelCostCalculator.cs", paths);
        Assert.Contains("src/Forge.Infrastructure/SqliteEngineeringTaskRepository.cs", paths);
        Assert.Contains("src/Forge.Api/Contracts/EngineeringTaskResponse.cs", paths);
        Assert.Contains("tests/Forge.Core.Tests/SqlitePersistenceTests.cs", paths);
        Assert.True(paths.Count(path => path.Contains("Clarification", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PlanningEngine", StringComparison.OrdinalIgnoreCase)) <= 1);
        var lastTargetedIndex = new[]
        {
            "src/Forge.Core/ModelCallRecord.cs",
            "src/Forge.Infrastructure/ModelCostCalculator.cs",
            "src/Forge.Infrastructure/SqliteEngineeringTaskRepository.cs",
            "src/Forge.Api/Contracts/EngineeringTaskResponse.cs",
            "tests/Forge.Core.Tests/SqlitePersistenceTests.cs"
        }.Max(path => Array.IndexOf(paths, path));
        var adapterIndex = Array.FindIndex(paths, path => path.Contains("Clarification", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PlanningEngine", StringComparison.OrdinalIgnoreCase));
        Assert.True(adapterIndex < 0 || adapterIndex > lastTargetedIndex);
    }

    [Fact]
    public async Task Forge_pricing_snapshot_correction_selects_targeted_bounded_evidence()
    {
        const string correction = "The plan omits persistence of per-call pricing snapshots. Include the model-call domain record, cost capture, SQLite migration/persistence, API/export representation, and legacy fallback labels. Do not solve this only in the PDF exporter.";
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var limits = new RepositoryAnalysisLimits();
        var discovery = await new RepositoryDiscoveryService(limits, TimeProvider.System).DiscoverAsync(repositoryRoot);

        // Do not let this integration test's own verbatim correction fixture outrank the production files it verifies.
        var repositoryFiles = discovery.TextFiles.Where(file =>
            file.Metadata.RelativePath != "tests/Forge.Core.Tests/EvidenceSelectionTests.cs").ToArray();
        var result = Selector(limits).SelectForPlanRevision(discovery.Snapshot, repositoryFiles,
            "Export the approved task report as a PDF with model-call telemetry.", correction);
        output.WriteLine($"SUMMARY inspected={result.FilesInspected} selected={result.FilesSelected} characters={result.TotalCharacters}");
        foreach (var item in result.Items)
            output.WriteLine($"{item.Id} {item.RelativePath} score={item.Score} reason={item.ReasonSelected}");

        Assert.InRange(result.Items.Count, 5, 12);
        Assert.True(result.TotalCharacters <= 60_000);
        Assert.Contains(result.Items, item => item.RelativePath == "src/Forge.Core/ModelCallRecord.cs");
        Assert.Contains(result.Items, item => item.RelativePath is "src/Forge.Infrastructure/ModelCostCalculator.cs" or
            "src/Forge.Infrastructure/ModelCostResolver.cs");
        Assert.Contains(result.Items, item => item.RelativePath is "src/Forge.Infrastructure/SqliteEngineeringTaskRepository.cs" or
            "tests/Forge.Core.Tests/SqlitePersistenceTests.cs");
        Assert.Contains(result.Items, item => item.RelativePath == "src/Forge.Api/Contracts/EngineeringTaskResponse.cs");
        Assert.True(result.Items.Count(item => item.RelativePath.Contains("Clarification", StringComparison.OrdinalIgnoreCase) ||
            item.RelativePath.Contains("PlanningEngine", StringComparison.OrdinalIgnoreCase)) <= 1);
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
