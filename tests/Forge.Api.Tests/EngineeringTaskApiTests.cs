using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
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
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;

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
    public async Task Task_history_is_empty_then_returns_only_lightweight_summaries_without_mutation()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/tasks"))!);
        var created = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = "C:/history-repository",
            requirement = "History requirement with private planning context. Acceptance criteria: list it. Validation: run tests."
        });
        created.EnsureSuccessStatusCode();
        var detail = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = detail.GetProperty("id").GetGuid();
        var before = await client.GetStringAsync($"/api/tasks/{id}");

        var response = await client.GetAsync("/api/tasks");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var summary = Assert.Single(json.EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["createdAt", "id", "originalRequirementPreview", "repository", "status", "updatedAt"],
            summary.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(id, summary.GetProperty("id").GetGuid());
        Assert.StartsWith("Repository ", summary.GetProperty("repository").GetString());
        Assert.DoesNotContain("implementationPlan", summary.ToString());
        Assert.DoesNotContain("telemetry", summary.ToString());
        Assert.Equal(before, await client.GetStringAsync($"/api/tasks/{id}"));
        Assert.Equal(before, await client.GetStringAsync($"/api/tasks/{id}"));
    }

    [Fact]
    public async Task Task_history_preview_json_is_unicode_safe_and_bounded()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        var requirement = new string('a', 159) + "\U0001F680 trailing text";
        var created = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = "C:/unicode-repository",
            requirement
        });
        created.EnsureSuccessStatusCode();

        var summary = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/tasks"))!);
        var preview = summary.GetProperty("originalRequirementPreview").GetString();

        Assert.NotNull(preview);
        Assert.Equal(160, preview.Length);
        Assert.EndsWith("\u2026", preview, StringComparison.Ordinal);
        for (var index = 0; index < preview.Length; index++)
        {
            if (!char.IsSurrogate(preview[index])) continue;
            Assert.True(Rune.TryGetRuneAt(preview, index, out _), $"Preview contained an unpaired surrogate at index {index}: {preview}");
            if (char.IsHighSurrogate(preview[index])) index++;
        }
    }

    [Fact]
    public async Task Detail_routes_handle_invalid_and_missing_task_ids_without_mutation()
    {
        var invalid = await _client.GetAsync("/api/tasks/not-a-guid");
        var missing = await _client.GetAsync($"/api/tasks/{Guid.NewGuid()}");
        var problem = await missing.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("task_not_found", problem.GetProperty("code").GetString());
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
    public void Fake_mode_host_uses_an_isolated_temporary_database()
    {
        ApiTestDatabaseGuard.AssertIsolated(_factory, _factory.DatabasePath);
    }

    [Fact]
    public void OpenAI_no_key_host_uses_an_isolated_temporary_database()
    {
        using var factory = new OpenAiNoKeyFactory();
        ApiTestDatabaseGuard.AssertIsolated(factory, factory.DatabasePath);
    }

    [Fact]
    public void API_test_factories_use_distinct_databases_and_remove_them_on_dispose()
    {
        var first = new FakeModeFactory();
        var second = new FakeModeFactory();
        var firstDirectory = Path.GetDirectoryName(first.DatabasePath)!;
        var secondDirectory = Path.GetDirectoryName(second.DatabasePath)!;
        try
        {
            ApiTestDatabaseGuard.AssertIsolated(first, first.DatabasePath);
            ApiTestDatabaseGuard.AssertIsolated(second, second.DatabasePath);
            Assert.NotEqual(first.DatabasePath, second.DatabasePath);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
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
        Assert.StartsWith("Repository ", analyzed.GetProperty("repository").GetString());
        Assert.False(analyzed.GetProperty("repositorySnapshot").TryGetProperty("normalizedRoot", out _));
        Assert.DoesNotContain(_factory.TargetRepositoryPath, analyzed.GetRawText(), StringComparison.OrdinalIgnoreCase);
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
    public async Task Approved_plan_generates_isolated_fake_changes_and_returns_bounded_review_data()
    {
        var repository = _factory.TargetRepositoryPath;
        var activeFile = Path.Combine(repository, "src", "ReportExportService.cs");
        var contentBefore = await File.ReadAllTextAsync(activeFile);
        var headBefore = FakeModeFactory.RunGit(repository, "rev-parse", "HEAD");
        var branchBefore = FakeModeFactory.RunGit(repository, "branch", "--show-current");
        var statusBefore = FakeModeFactory.RunGit(repository, "status", "--porcelain=v1", "--untracked-files=all");

        var created = await CreateAsync(
            "Add report export. Acceptance criteria: export is available. Validation: run focused tests.",
            repository);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();

        var response = await _client.PostAsync($"/api/tasks/{id}/implementation", null);
        if (!response.IsSuccessStatusCode)
        {
            var failed = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
            var token = failed.GetProperty("implementationWorkspace").GetProperty("token").GetString()!;
            var workspaceStatus = FakeModeFactory.RunGit(Path.Combine(_factory.WorktreeRoot, token),
                "status", "--porcelain=v1", "--untracked-files=all");
            Assert.Fail($"{await response.Content.ReadAsStringAsync()} Workspace status: {workspaceStatus}");
        }
        var implemented = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("AwaitingImplementationReview", implemented.GetProperty("status").GetString());
        Assert.Equal("DeterministicFake", implemented.GetProperty("implementationResult").GetProperty("source").GetString());
        Assert.True(implemented.GetProperty("implementationResult").GetProperty("isDeterministicFake").GetBoolean());
        Assert.NotEmpty(implemented.GetProperty("implementationResult").GetProperty("changedFiles").EnumerateArray());
        Assert.Equal("Completed", implemented.GetProperty("implementationRuntime").GetProperty("disposition").GetString());
        Assert.True(implemented.GetProperty("implementationRuntime").GetProperty("workspaceAvailable").GetBoolean());
        Assert.True(implemented.GetProperty("implementationRuntime").GetProperty("activeCheckoutVerified").GetBoolean());
        Assert.True(implemented.GetProperty("implementationResult").GetProperty("fullDiffUtf8Bytes").GetInt32() > 0);
        Assert.Contains("not AI-authored", implemented.GetProperty("implementationResult").GetProperty("warnings").ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, implemented.GetProperty("telemetry").GetProperty("totalCalls").GetInt32());
        Assert.DoesNotContain(_factory.WorktreeRoot, implemented.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validation succeeded", implemented.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(contentBefore, await File.ReadAllTextAsync(activeFile));
        Assert.Equal(headBefore, FakeModeFactory.RunGit(repository, "rev-parse", "HEAD"));
        Assert.Equal(branchBefore, FakeModeFactory.RunGit(repository, "branch", "--show-current"));
        Assert.Equal(statusBefore, FakeModeFactory.RunGit(repository, "status", "--porcelain=v1", "--untracked-files=all"));

        var beforeReadOnlyRoutes = await ReadPersistedStateAsync(_factory.DatabasePath, id);
        var persisted = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("AwaitingImplementationReview", persisted.GetProperty("status").GetString());
        Assert.Equal(implemented.GetProperty("implementationResult").ToString(), persisted.GetProperty("implementationResult").ToString());
        (await _client.GetAsync("/api/tasks")).EnsureSuccessStatusCode();
        (await _client.GetAsync($"/api/tasks/{id}/export/plan-pdf")).EnsureSuccessStatusCode();
        (await _client.GetAsync($"/api/tasks/{id}/export/pdf")).EnsureSuccessStatusCode();
        var afterReadOnlyRoutes = await ReadPersistedStateAsync(_factory.DatabasePath, id);
        Assert.Equal(beforeReadOnlyRoutes, afterReadOnlyRoutes);
    }

    [Fact]
    public async Task Implementation_endpoint_rejects_invalid_state_without_mutating_repository()
    {
        var repository = _factory.TargetRepositoryPath;
        var statusBefore = FakeModeFactory.RunGit(repository, "status", "--porcelain=v1", "--untracked-files=all");
        var branchesBefore = FakeModeFactory.RunGit(repository, "branch", "--format=%(refname:short)");
        var worktreesBefore = Directory.Exists(_factory.WorktreeRoot)
            ? Directory.GetDirectories(_factory.WorktreeRoot).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var created = await CreateAsync("Add report export.", repository);

        var response = await _client.PostAsync($"/api/tasks/{created.GetProperty("id").GetGuid()}/implementation", null);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("workflow_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(statusBefore, FakeModeFactory.RunGit(repository, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(branchesBefore, FakeModeFactory.RunGit(repository, "branch", "--format=%(refname:short)"));
        Assert.Equal(worktreesBefore, Directory.Exists(_factory.WorktreeRoot)
            ? Directory.GetDirectories(_factory.WorktreeRoot).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : []);
    }

    [Fact]
    public async Task Dirty_repository_implementation_failure_is_safe_and_remains_plan_approved()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        var repository = factory.TargetRepositoryPath;
        var createdResponse = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository,
            requirement = "Add report export. Acceptance criteria: export is available. Validation: run focused tests."
        });
        createdResponse.EnsureSuccessStatusCode();
        var id = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(Path.Combine(repository, "untracked-after-approval.txt"), "dirty");

        var response = await client.PostAsync($"/api/tasks/{id}/implementation", null);
        var responseText = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(responseText);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("implementation_repository_dirty", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(repository, responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.WorktreeRoot, responseText, StringComparison.OrdinalIgnoreCase);
        var persisted = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("PlanApproved", persisted.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("implementationWorkspace").ValueKind);
        Assert.False(Directory.Exists(factory.WorktreeRoot) && Directory.EnumerateDirectories(factory.WorktreeRoot)
            .Any(path => Path.GetFileName(path).Length == 32));
    }

    [Fact]
    public async Task Approved_task_pdf_has_safe_headers_required_content_and_does_not_mutate_task()
    {
        const string requirement = "Add report export. Acceptance criteria: export is available. Validation: run focused tests.";
        var created = await CreateAsync(requirement, _factory.TargetRepositoryPath);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        var before = await _client.GetStringAsync($"/api/tasks/{id}");

        var response = await _client.GetAsync($"/api/tasks/{id}/export/pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"forge-task-{id:D}.pdf", response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join('\n', pdf.GetPages().Select(page => page.Text));
        Assert.Contains(requirement, text);
        Assert.Contains("estimates, not invoices", text);

        var after = await _client.GetStringAsync($"/api/tasks/{id}");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Proposed_and_approved_plan_pdf_routes_use_persisted_status_safe_headers_and_do_not_mutate_task()
    {
        const string requirement = "Add plan PDF export. Acceptance criteria: export all plan sections. Validation: run focused tests.";
        var created = await CreateAsync(requirement, _factory.TargetRepositoryPath);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        var proposedBefore = await _client.GetStringAsync($"/api/tasks/{id}");

        var proposed = await _client.GetAsync($"/api/tasks/{id}/export/plan-pdf");
        var proposedBytes = await proposed.Content.ReadAsByteArrayAsync();
        var proposedText = ExtractPdf(proposedBytes);

        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        Assert.Equal("application/pdf", proposed.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"forge-plan-{id:D}.pdf", proposed.Content.Headers.ContentDisposition?.FileNameStar ??
            proposed.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(proposedBytes, 0, 4));
        Assert.Contains("PROPOSED PLAN \u2014 NOT APPROVED", proposedText);
        Assert.Contains("NOT EXECUTED", proposedText);
        Assert.DoesNotContain(_factory.TargetRepositoryPath, proposedText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(proposedBefore, await _client.GetStringAsync($"/api/tasks/{id}"));

        (await _client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        var approvedBefore = await _client.GetStringAsync($"/api/tasks/{id}");
        var approved = await _client.GetAsync($"/api/tasks/{id}/export/plan-pdf");
        var approvedText = ExtractPdf(await approved.Content.ReadAsByteArrayAsync());

        Assert.Contains("APPROVED PLAN", approvedText);
        Assert.DoesNotContain("PROPOSED PLAN \u2014 NOT APPROVED", approvedText);
        Assert.Equal(approvedBefore, await _client.GetStringAsync($"/api/tasks/{id}"));
    }

    [Fact]
    public async Task Plan_pdf_route_rejects_no_plan_and_missing_task_safely()
    {
        var created = await CreateAsync("Add plan PDF export.");
        var id = created.GetProperty("id").GetGuid();

        var noPlan = await _client.GetAsync($"/api/tasks/{id}/export/plan-pdf");
        var missing = await _client.GetAsync($"/api/tasks/{Guid.NewGuid()}/export/plan-pdf");

        Assert.Equal(HttpStatusCode.Conflict, noPlan.StatusCode);
        Assert.Equal("workflow_conflict", (await noPlan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("task_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pdf_export_missing_task_uses_existing_not_found_contract()
    {
        var response = await _client.GetAsync($"/api/tasks/{Guid.NewGuid()}/export/pdf");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("task_not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pdf_generation_failure_uses_safe_server_error_contract()
    {
        await using var factory = new PdfFailureFactory();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = "C:/repo",
            requirement = "Add report export. Acceptance criteria: export works. Validation: run tests."
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/tasks/{id}/export/pdf");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("server_error", body);
        Assert.DoesNotContain("sensitive-pdf-generator-detail", body);
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
    public async Task Capabilities_truthfully_report_fake_isolated_implementation_without_validation_or_pr_creation()
    {
        var capabilities = await _client.GetFromJsonAsync<JsonElement>("/api/system/capabilities");
        Assert.True(capabilities.GetProperty("repositoryInspectionAvailable").GetBoolean());
        Assert.True(capabilities.GetProperty("planningAvailable").GetBoolean());
        Assert.Equal("Fake", capabilities.GetProperty("clarificationProvider").GetString());
        Assert.Equal("Fake", capabilities.GetProperty("planningProvider").GetString());
        Assert.True(capabilities.GetProperty("clarificationConfigured").GetBoolean());
        Assert.True(capabilities.GetProperty("planningConfigured").GetBoolean());
        Assert.Equal("Deterministic Fake", capabilities.GetProperty("implementationProvider").GetString());
        Assert.True(capabilities.GetProperty("implementationConfigured").GetBoolean());
        Assert.True(capabilities.GetProperty("targetModificationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("validationAvailable").GetBoolean());
        Assert.True(capabilities.GetProperty("reviewAvailable").GetBoolean());
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
        FakeModeFactory.RunGit(factory.TargetRepositoryPath, "add", "--", "web/src/App.tsx", "web/src/api.ts");
        FakeModeFactory.RunGit(factory.TargetRepositoryPath, "-c", "user.name=Forge API Tests", "-c",
            "user.email=forge-tests@example.invalid", "commit", "-m", "add frontend fixture");
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

    [Fact]
    public void Task_api_uses_central_resolved_cost_and_exposes_only_stored_per_call_rates()
    {
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Add report export", now);
        var snapshot = new ModelPricingSnapshot(10m, 2m, 20m);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "model", "medium",
            now, now, true, "response", 100, 25, 50, 40, 999m, null, snapshot), now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "legacy-model", "low",
            now, now, true, "legacy-response", 0, 0, 0, 0, 0m, null), now);
        var calculator = new ModelCostCalculator(new Dictionary<string, ModelPricing>
        {
            ["model"] = new(100m, 100m, 100m)
        });

        var response = EngineeringTaskResponse.FromDomain(task, new ModelCostResolver(calculator));

        var call = Assert.Single(response.Telemetry.Calls, item => item.ProviderResponseId == "response");
        var legacyCall = Assert.Single(response.Telemetry.Calls, item => item.ProviderResponseId == "legacy-response");
        Assert.Equal(75, call.UncachedInputTokens);
        Assert.Equal("stored pricing snapshot", call.PricingProvenance);
        Assert.Equal("legacy estimate \u2014 pricing snapshot unavailable", legacyCall.PricingProvenance);
        Assert.Equal(0.0018m, call.EstimatedCostUsd);
        Assert.Equal(snapshot.InputPerMillionUsd, call.StoredPricingSnapshot?.InputPerMillionUsd);
        Assert.False(response.Telemetry.IsPartialEstimate);
        Assert.Equal(0, response.Telemetry.CostUnavailableCallCount);
    }

    private async Task<JsonElement> CreateAsync(string requirement, string repository = "C:/repo")
    {
        var response = await _client.PostAsJsonAsync("/api/tasks", new { repository, requirement });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ExtractPdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join('\n', pdf.GetPages().Select(page => page.Text));
    }

    private static async Task<string> ReadPersistedStateAsync(string databasePath, Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowVersion, UpdatedAt, Status, ImplementationWorkspace, ImplementationResult,
                   LastImplementationFailure, ImplementationLease
            FROM EngineeringTasks WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return string.Join('|', Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString()));
    }
}

public class FakeModeFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"forge-api-tests-{Guid.NewGuid():N}");
    private readonly object _repositoryLock = new();
    public string DatabasePath => Path.Combine(_directory, "forge.db");
    public string WorktreeRoot => Path.Combine(_directory, "worktrees");
    public string TargetRepositoryPath
    {
        get
        {
            var path = Path.Combine(_directory, "target-repository");
            lock (_repositoryLock)
            {
                if (!Directory.Exists(Path.Combine(path, ".git")))
                {
                    Directory.CreateDirectory(Path.Combine(path, "src"));
                    File.WriteAllText(Path.Combine(path, "src", "ReportExportService.cs"), "public sealed class ReportExportService { }\n");
                    File.WriteAllText(Path.Combine(path, "Forge.Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
                    RunGit(path, "init", "--initial-branch=main");
                    RunGit(path, "add", "--", ".");
                    RunGit(path, "-c", "user.name=Forge API Tests", "-c", "user.email=forge-tests@example.invalid", "commit", "-m", "fixture baseline");
                }
            }
            return path;
        }
    }

    internal static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git test fixture.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git test fixture failed: {stderr}");
        return stdout.Trim();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Forge:DatabasePath", DatabasePath);
        builder.UseSetting("Forge:Implementation:WorktreeRoot", WorktreeRoot);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forge:DatabasePath"] = DatabasePath,
            ["Forge:AI:Mode"] = "Fake",
            ["Forge:Implementation:WorktreeRoot"] = WorktreeRoot
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(_directory, true);
        }
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
    public string DatabasePath => Path.Combine(_directory, "forge.db");
    public string WorktreeRoot => Path.Combine(_directory, "worktrees");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Forge:DatabasePath", DatabasePath);
        builder.UseSetting("Forge:AI:Mode", "OpenAI");
        builder.UseSetting("Forge:Implementation:WorktreeRoot", WorktreeRoot);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Forge:DatabasePath"] = DatabasePath,
                ["Forge:AI:Mode"] = "OpenAI",
                ["Forge:Implementation:WorktreeRoot"] = WorktreeRoot
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

internal static class ApiTestDatabaseGuard
{
    public static void AssertIsolated(WebApplicationFactory<Program> factory, string expectedDatabasePath)
    {
        using var client = factory.CreateClient();
        using var response = client.GetAsync("/api/system/capabilities").GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var configuredPath = configuration["Forge:DatabasePath"];
        var configuredWorktreeRoot = configuration["Forge:Implementation:WorktreeRoot"];
        var developmentDatabasePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "data", "forge.db"));
        var productionWorktreeRoot = Path.GetFullPath(new ImplementationWorkspaceOptions().WorktreeRoot);

        Assert.True(Path.IsPathFullyQualified(expectedDatabasePath));
        Assert.Equal(expectedDatabasePath, configuredPath);
        Assert.NotEqual(developmentDatabasePath, Path.GetFullPath(expectedDatabasePath));
        Assert.StartsWith(Path.GetTempPath(), expectedDatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(expectedDatabasePath), "The API initializer did not create the isolated test database.");
        Assert.False(string.IsNullOrWhiteSpace(configuredWorktreeRoot));
        Assert.StartsWith(Path.GetTempPath(), configuredWorktreeRoot!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(productionWorktreeRoot, Path.GetFullPath(configuredWorktreeRoot!));
        Assert.StartsWith(Path.GetDirectoryName(expectedDatabasePath)!, Path.GetFullPath(configuredWorktreeRoot!),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PdfFailureFactory : FakeModeFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEngineeringTaskPdfExporter>();
            services.AddSingleton<IEngineeringTaskPdfExporter, FailingPdfExporter>();
        });
    }

    private sealed class FailingPdfExporter : IEngineeringTaskPdfExporter
    {
        public byte[] Export(EngineeringTask task) =>
            throw new InvalidOperationException("sensitive-pdf-generator-detail");
    }
}
