using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Core;
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
        planResponse.EnsureSuccessStatusCode();
        var planned = await planResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPlanApproval", planned.GetProperty("status").GetString());
        Assert.True(planned.GetProperty("implementationPlan").GetProperty("isDeterministicFake").GetBoolean());

        var approvalResponse = await _client.PostAsync($"/api/tasks/{id}/plan-approval", null);
        approvalResponse.EnsureSuccessStatusCode();
        var approved = await approvalResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Implementing", approved.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("planApprovedAt").ValueKind);

        var persisted = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("Implementing", persisted.GetProperty("status").GetString());
        Assert.True(persisted.GetProperty("evidenceItems").GetArrayLength() > 0);
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
        Assert.False(capabilities.GetProperty("targetModificationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("validationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("reviewAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("pullRequestCreationAvailable").GetBoolean());
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
