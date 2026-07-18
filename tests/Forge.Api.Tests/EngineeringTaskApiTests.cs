using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Contracts;
using Forge.Api.Controllers;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Api.Tests;

public sealed class EngineeringTaskApiTests : IClassFixture<FakeModeFactory>
{
    private readonly HttpClient _client;
    private readonly FakeModeFactory _factory;

    public EngineeringTaskApiTests(FakeModeFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Complete_requirement_returns_immediate_summary()
    {
        var response = await CreateAsync("""
            Add administrator audit logging.
            Acceptance criteria: administrator changes are recorded.
            Validation: run the audit logging tests.
            """);

        Assert.Equal("AwaitingRequirementApproval", response.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("currentPendingQuestion").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("requirementSummary").GetString()));
    }

    [Fact]
    public async Task Answering_returns_exactly_one_next_question()
    {
        var created = await CreateAsync("Add administrator audit logging.");
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.PostAsJsonAsync($"/api/tasks/{id}/answers", new { answer = "Record create, update and delete events." });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Clarifying", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("currentPendingQuestion").GetString()));
        Assert.Equal(1, body.GetProperty("clarificationAnswers").GetArrayLength());
    }

    [Fact]
    public async Task Corrected_summary_can_be_approved()
    {
        var created = await CreateAsync("""
            Add audit logging. Acceptance criteria: record changes. Validation: run tests.
            """);
        var id = created.GetProperty("id").GetGuid();

        var correction = await _client.PostAsJsonAsync($"/api/tasks/{id}/requirement-revision", new { correction = "Only administrator changes." });
        correction.EnsureSuccessStatusCode();
        var corrected = await correction.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingRequirementApproval", corrected.GetProperty("status").GetString());
        Assert.Equal(1, corrected.GetProperty("requirementRevisionNotes").GetArrayLength());

        var approval = await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null);
        approval.EnsureSuccessStatusCode();
        var approved = await approval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ReadyForPlanning", approved.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invalid_workflow_action_returns_problem_details_conflict()
    {
        var created = await CreateAsync("Add logging.");
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_conflict", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Provider_failure_returns_safe_problem_details()
    {
        await using var factory = new ProviderFailureFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/tasks", new { repository = "C:/repo", requirement = "Add logging" });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("AI provider failure", text);
        Assert.DoesNotContain("sensitive-test-value", text);
    }

    [Fact]
    public async Task Approved_requirement_can_be_analyzed_planned_approved_and_read_back()
    {
        var created = await CreateAsync("""
            Add report export. Acceptance criteria: export is available. Validation: run focused tests.
            """, _factory.TargetRepositoryPath);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();

        var analysisResponse = await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null);
        analysisResponse.EnsureSuccessStatusCode();
        var analyzed = await analysisResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Planning", analyzed.GetProperty("status").GetString());
        Assert.True(analyzed.GetProperty("repositorySnapshot").GetProperty("eligibleTextFileCount").GetInt32() > 0);
        Assert.True(analyzed.GetProperty("evidenceItems").GetArrayLength() > 0);

        var planResponse = await _client.PostAsync($"/api/tasks/{id}/plan", null);
        Assert.True(planResponse.IsSuccessStatusCode, await planResponse.Content.ReadAsStringAsync());
        var planned = await planResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPlanApproval", planned.GetProperty("status").GetString());
        Assert.True(planned.GetProperty("implementationPlan").GetProperty("isDeterministicFake").GetBoolean());
        Assert.Equal("DeterministicFake", planned.GetProperty("implementationPlan").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, planned.GetProperty("implementationPlan").GetProperty("planningModel").ValueKind);
        Assert.True(planned.GetProperty("implementationPlan").GetProperty("orderedSteps")[0].TryGetProperty("expectedResult", out _));
        var coverage = Assert.Single(planned.GetProperty("implementationPlan").GetProperty("requirementCoverage").EnumerateArray());
        Assert.True(coverage.GetProperty("affectedPaths").GetArrayLength() > 0);
        Assert.True(coverage.GetProperty("stepOrders").GetArrayLength() > 0);
        Assert.Equal(0, planned.GetProperty("telemetry").GetProperty("totalCalls").GetInt32());
        Assert.Equal(0m, planned.GetProperty("telemetry").GetProperty("totalEstimatedCostUsd").GetDecimal());

        var approvalResponse = await _client.PostAsync($"/api/tasks/{id}/plan-approval", null);
        approvalResponse.EnsureSuccessStatusCode();
        var approved = await approvalResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PlanApproved", approved.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("planApprovedAt").ValueKind);

        var persisted = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("PlanApproved", persisted.GetProperty("status").GetString());
        Assert.True(persisted.GetProperty("evidenceItems").GetArrayLength() > 0);
        Assert.Equal("DeterministicFake", persisted.GetProperty("implementationPlan").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Fake_plan_correction_endpoint_preserves_previous_plan_and_requires_explicit_approval()
    {
        var created = await CreateAsync("""
            Add report export with pricing telemetry. Acceptance criteria: export is available. Validation: run focused tests.
            """, _factory.TargetRepositoryPath);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        var initialPlanResponse = await _client.PostAsync($"/api/tasks/{id}/plan", null);
        initialPlanResponse.EnsureSuccessStatusCode();
        var initialPlan = await initialPlanResponse.Content.ReadFromJsonAsync<JsonElement>();
        var previousTitle = initialPlan.GetProperty("implementationPlan").GetProperty("title").GetString();

        var correctionResponse = await _client.PostAsJsonAsync($"/api/tasks/{id}/plan-revision", new
        {
            correction = "Include model call pricing persistence and SQLite tests."
        });
        Assert.True(correctionResponse.IsSuccessStatusCode, await correctionResponse.Content.ReadAsStringAsync());
        var revised = await correctionResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("AwaitingPlanApproval", revised.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, revised.GetProperty("planApprovedAt").ValueKind);
        var history = Assert.Single(revised.GetProperty("planRevisionNotes").EnumerateArray());
        Assert.Equal(previousTitle, history.GetProperty("previousPlanTitle").GetString());
        Assert.Contains("src/", history.GetProperty("previousAffectedPaths")[0].GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revised", revised.GetProperty("implementationPlan").GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, revised.GetProperty("telemetry").GetProperty("totalCalls").GetInt32());

        var approval = await _client.PostAsync($"/api/tasks/{id}/plan-approval", null);
        approval.EnsureSuccessStatusCode();
        Assert.Equal("PlanApproved", (await approval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Plan_correction_endpoint_rejects_invalid_state_and_empty_input_safely()
    {
        var created = await CreateAsync("Add report export.", _factory.TargetRepositoryPath);
        var id = created.GetProperty("id").GetGuid();

        var conflict = await _client.PostAsJsonAsync($"/api/tasks/{id}/plan-revision", new { correction = "Add persistence." });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictProblem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workflow_conflict", conflictProblem.GetProperty("code").GetString());

        var empty = await _client.PostAsJsonAsync($"/api/tasks/{id}/plan-revision", new { correction = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.DoesNotContain(_factory.TargetRepositoryPath, await empty.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analysis_before_requirement_approval_returns_conflict()
    {
        var created = await CreateAsync("Add report export.", _factory.TargetRepositoryPath);
        var response = await _client.PostAsync($"/api/tasks/{created.GetProperty("id").GetGuid()}/repository-analysis", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Missing_repository_path_returns_safe_problem_details()
    {
        var missing = Path.Combine(_factory.TargetRepositoryPath, "does-not-exist");
        var created = await CreateAsync("Add report export. Acceptance criteria: export works. Validation: run tests.", missing);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();

        var response = await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("repository_missing_path", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(missing, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Capabilities_truthfully_report_read_only_fake_planning()
    {
        var capabilities = await _client.GetFromJsonAsync<JsonElement>("/api/system/capabilities");
        Assert.True(capabilities.GetProperty("repositoryInspectionAvailable").GetBoolean());
        Assert.True(capabilities.GetProperty("planningAvailable").GetBoolean());
        Assert.Equal("Fake", capabilities.GetProperty("clarificationProvider").GetString());
        Assert.Equal("Fake", capabilities.GetProperty("planningProvider").GetString());
        Assert.True(capabilities.GetProperty("clarificationConfigured").GetBoolean());
        Assert.True(capabilities.GetProperty("planningConfigured").GetBoolean());
        Assert.False(capabilities.GetProperty("targetModificationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("validationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("reviewAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("pullRequestCreationAvailable").GetBoolean());
    }

    [Fact]
    public async Task Planning_provider_failure_returns_safe_problem_details_and_persists_failed_call()
    {
        await using var factory = new PlanningProviderFailureFactory();
        using var client = factory.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = factory.TargetRepositoryPath,
            requirement = "Add report export. Acceptance criteria: export is available. Validation: run focused tests."
        });
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/api/tasks/{id}/plan", null);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("planning_provider_error", text);
        Assert.DoesNotContain("sensitive-planning-value", text);
        var persisted = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        var call = Assert.Single(persisted.GetProperty("telemetry").GetProperty("calls").EnumerateArray());
        Assert.Equal("Planning", call.GetProperty("stage").GetString());
        Assert.False(call.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Output_truncation_returns_specific_safe_problem_and_persists_costed_failure()
    {
        await using var factory = new OutputTruncatedPlanningFactory();
        using var client = factory.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = factory.TargetRepositoryPath,
            requirement = "Add report export. Acceptance criteria: export is available. Validation: run focused tests."
        });
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/api/tasks/{id}/plan", null);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("planning_output_truncated", problem.GetProperty("code").GetString());
        Assert.Equal("The planning response reached its output limit before the structured plan was complete.",
            problem.GetProperty("detail").GetString());
        var persisted = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        var call = Assert.Single(persisted.GetProperty("telemetry").GetProperty("calls").EnumerateArray());
        Assert.Equal(6000, call.GetProperty("outputTokens").GetInt32());
        Assert.Equal(0.21005m, call.GetProperty("estimatedCostUsd").GetDecimal());
        Assert.Equal("output_truncated", call.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task Missing_direct_evidence_refresh_is_zero_call_persists_and_requires_separate_plan_request()
    {
        await using var factory = new MissingDirectEvidenceFactory();
        using var client = factory.CreateClient();
        var webSource = Path.Combine(factory.TargetRepositoryPath, "web", "src");
        Directory.CreateDirectory(webSource);
        await File.WriteAllTextAsync(Path.Combine(webSource, "App.tsx"),
            "import { exportReport } from './api'\nexport const App = () => 'task report export'");
        await File.WriteAllTextAsync(Path.Combine(webSource, "api.ts"),
            "export const exportReport = () => 'task report export'");
        var createdResponse = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = factory.TargetRepositoryPath,
            requirement = "Export the task report from the UI through the frontend API helper. Acceptance criteria: export is available. Validation: run tests."
        });
        createdResponse.EnsureSuccessStatusCode();
        var id = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();

        var failedResponse = await client.PostAsync($"/api/tasks/{id}/plan", null);
        var problem = await failedResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failedResponse.StatusCode);
        Assert.Equal("missing_direct_evidence", problem.GetProperty("code").GetString());
        Assert.Equal(ImplementationPlanValidator.MissingDirectEvidenceMessage, problem.GetProperty("detail").GetString());
        Assert.Equal(1, factory.PlanningEngine.CallCount);

        var refreshResponse = await client.PostAsync($"/api/tasks/{id}/evidence-refresh", null);
        Assert.True(refreshResponse.IsSuccessStatusCode, await refreshResponse.Content.ReadAsStringAsync());
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ReadyForPlanning", refreshed.GetProperty("status").GetString());
        Assert.Equal(1, factory.PlanningEngine.CallCount);
        Assert.Contains(refreshed.GetProperty("evidenceItems").EnumerateArray(), item =>
            item.GetProperty("relativePath").GetString() == "web/src/api.ts");

        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Contains(reread.GetProperty("evidenceItems").EnumerateArray(), item =>
            item.GetProperty("relativePath").GetString() == "web/src/api.ts");
        var plannedResponse = await client.PostAsync($"/api/tasks/{id}/plan", null);
        Assert.True(plannedResponse.IsSuccessStatusCode, await plannedResponse.Content.ReadAsStringAsync());
        var planned = await plannedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPlanApproval", planned.GetProperty("status").GetString());
        Assert.Equal(2, factory.PlanningEngine.CallCount);
        Assert.Contains(planned.GetProperty("implementationPlan").GetProperty("affectedFiles").EnumerateArray(), file =>
            file.GetProperty("path").GetString() == "web/src/api.ts");
    }

    [Fact]
    public async Task OpenAI_mode_starts_without_key_and_reports_both_ai_stages_unavailable()
    {
        await using var factory = new OpenAiNoKeyFactory();
        using var client = factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/system/capabilities");

        Assert.Equal("OpenAI", capabilities.GetProperty("aiMode").GetString());
        Assert.Equal("OpenAI", capabilities.GetProperty("clarificationProvider").GetString());
        Assert.Equal("OpenAI", capabilities.GetProperty("planningProvider").GetString());
        Assert.False(capabilities.GetProperty("clarificationConfigured").GetBoolean());
        Assert.False(capabilities.GetProperty("planningConfigured").GetBoolean());
        Assert.False(capabilities.GetProperty("planningAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("aiConfigured").GetBoolean());
    }

    [Fact]
    public void OpenAI_capabilities_report_both_configured_models_when_key_is_available()
    {
        var result = new SystemController(
            new ForgeAiOptions { Mode = ForgeAiModes.OpenAI },
            new OpenAIConfigurationState(true)).GetCapabilities();
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var capabilities = Assert.IsType<SystemCapabilitiesResponse>(ok.Value);

        Assert.Equal("gpt-5.6-terra", capabilities.ClarificationModel);
        Assert.Equal("gpt-5.6-sol", capabilities.PlanningModel);
        Assert.True(capabilities.ClarificationConfigured);
        Assert.True(capabilities.PlanningConfigured);
        Assert.True(capabilities.PlanningAvailable);
    }

    private async Task<JsonElement> CreateAsync(string requirement, string repository = "C:/repo")
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { repository, requirement });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

public class FakeModeFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"forge-api-tests-{Guid.NewGuid():N}");
    public string TargetRepositoryPath
    {
        get
        {
            var path = Path.Combine(_directory, "target-repository");
            Directory.CreateDirectory(Path.Combine(path, "src"));
            var source = Path.Combine(path, "src", "ReportExportService.cs");
            if (!File.Exists(source)) File.WriteAllText(source, "public sealed class ReportExportService { }\n");
            var project = Path.Combine(path, "Forge.Sample.csproj");
            if (!File.Exists(project)) File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            return path;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:DatabasePath"] = Path.Combine(_directory, "forge.db"),
            ["Forge:AI:Mode"] = "Fake"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

public sealed class ProviderFailureFactory : FakeModeFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClarificationEngine>();
            services.AddSingleton<IClarificationEngine, FailingEngine>();
        });
    }

    private sealed class FailingEngine : IClarificationEngine
    {
        public Task<ClarificationEvaluation> EvaluateAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "gpt-5.6-terra", "low", now, now, false, null, 0, 0, 0, null, 0m, "provider_error");
            return Task.FromException<ClarificationEvaluation>(new ClarificationProviderException(
                "OpenAI could not complete the clarification request.",
                "provider_error",
                failed,
                new Exception("sensitive-test-value")));
        }
    }
}

public sealed class PlanningProviderFailureFactory : FakeModeFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPlanningEngine>();
            services.AddSingleton<IPlanningEngine, FailingPlanningEngine>();
        });
    }

    private sealed class FailingPlanningEngine : IPlanningEngine
    {
        public Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
                now, now, false, null, 0, 0, 0, null, 0m, "provider_error");
            return Task.FromException<PlanningEvaluation>(new PlanningProviderException(
                "OpenAI could not complete the planning request.", "provider_error", failed,
                new Exception("sensitive-planning-value")));
        }
    }
}

public sealed class OutputTruncatedPlanningFactory : FakeModeFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPlanningEngine>();
            services.AddSingleton<IPlanningEngine, TruncatedPlanningEngine>();
        });
    }

    private sealed class TruncatedPlanningEngine : IPlanningEngine
    {
        public Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
                now, now, false, "resp_truncated", 6100, 100, 6000, 2000, 0.21005m, "output_truncated");
            return Task.FromException<PlanningEvaluation>(new PlanningProviderException(
                "The planning response reached its output limit before the structured plan was complete.",
                "output_truncated", failed));
        }
    }
}

public sealed class MissingDirectEvidenceFactory : FakeModeFactory
{
    public FailOncePlanningEngine PlanningEngine { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPlanningEngine>();
            services.AddSingleton<IPlanningEngine>(PlanningEngine);
        });
    }

    public sealed class FailOncePlanningEngine : IPlanningEngine
    {
        public int CallCount { get; private set; }

        public Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                var now = DateTimeOffset.UtcNow;
                var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
                    now, now, false, "resp_plan", 1000, 0, 100, 25, 0.0071m, "missing_direct_evidence");
                return Task.FromException<PlanningEvaluation>(new PlanningProviderException(
                    ImplementationPlanValidator.MissingDirectEvidenceMessage, "missing_direct_evidence", failed));
            }

            return new FakePlanningEngine().CreatePlanAsync(context, cancellationToken);
        }
    }
}

public sealed class OpenAiNoKeyFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"forge-openai-no-key-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Forge:AI:Mode", "OpenAI");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Forge:DatabasePath"] = Path.Combine(_directory, "forge.db"),
                ["Forge:AI:Mode"] = "OpenAI"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Forge.Api.Controllers.OpenAIConfigurationState>();
            services.AddSingleton(new Forge.Api.Controllers.OpenAIConfigurationState(false));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
