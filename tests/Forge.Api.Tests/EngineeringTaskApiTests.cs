using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Api.Contracts;
using Forge.Api.Controllers;
using Forge.Core;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    public async Task Fake_task_detail_and_history_use_one_safe_repository_identifier_without_path_components()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        const string repository = @"C:\Users\RecognizableUser\SecretClient\ForgeRepo";
        var response = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository,
            requirement = "Add a report. Acceptance criteria: the report exists. Validation: inspect it."
        });
        response.EnsureSuccessStatusCode();

        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        var history = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/tasks"))!);
        var detailRepository = detail.GetProperty("repository").GetString()!;
        var historyRepository = history.GetProperty("repository").GetString()!;
        var requirementSummary = detail.GetProperty("requirementSummary").GetString()!;

        Assert.Equal(detailRepository, historyRepository);
        Assert.Matches("^Repository [0-9a-f]{16}$", detailRepository);
        foreach (var value in new[] { detailRepository, requirementSummary })
        {
            Assert.DoesNotContain("RecognizableUser", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecretClient", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:", value, StringComparison.OrdinalIgnoreCase);
        }
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

    [Theory]
    [InlineData("GET", "/api/tasks/{0}")]
    [InlineData("GET", "/api/tasks/{0}/export/pdf")]
    [InlineData("GET", "/api/tasks/{0}/export/plan-pdf")]
    [InlineData("POST", "/api/tasks/{0}/requirement-approval")]
    public async Task Missing_task_actions_return_handled_problem_details_without_unhandled_error_logging(
        string method,
        string routeTemplate)
    {
        await using var factory = new MissingTaskLoggingFactory();
        using var client = factory.CreateClient();
        var route = string.Format(CultureInfo.InvariantCulture, routeTemplate, Guid.NewGuid());

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Engineering task not found", problem.GetProperty("title").GetString());
        Assert.Equal("task_not_found", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("C:\\", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Category.EndsWith(nameof(EngineeringTaskNotFoundExceptionFilter), StringComparison.Ordinal) &&
            entry.Level is LogLevel.Information or LogLevel.Warning &&
            entry.Message.Contains("Engineering task was not found", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Category.Contains("ExceptionHandlerMiddleware", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => !string.IsNullOrWhiteSpace(entry.ExceptionText));
        Assert.DoesNotContain(" at ", factory.Logs.Text, StringComparison.Ordinal);
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
            var workspaceStatus = FakeModeFactory.RunGit(Path.Combine(_factory.WorktreeRoot, id.ToString("N")),
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
        Assert.False(implemented.GetProperty("implementationWorkspace").TryGetProperty("token", out _));
        var revision = Assert.Single(implemented.GetProperty("implementationRevisions").EnumerateArray());
        Assert.Equal(1, revision.GetProperty("revisionNumber").GetInt32());
        Assert.True(revision.GetProperty("isCurrent").GetBoolean());
        Assert.Matches("^[0-9a-f]{64}$", revision.GetProperty("resultFingerprint").GetString()!);
        Assert.True(implemented.GetProperty("implementationResult").GetProperty("activeCheckoutVerified").GetBoolean());
        var resultFingerprint = revision.GetProperty("resultFingerprint").GetString()!;
        var (compatibilityResultJson, revisionLedgerJson) = await ReadImplementationResultJsonAsync(
            _factory.DatabasePath, id);
        using (var compatibilityDocument = JsonDocument.Parse(compatibilityResultJson))
            Assert.True(compatibilityDocument.RootElement.GetProperty("activeCheckoutVerified").GetBoolean());
        using (var ledgerDocument = JsonDocument.Parse(revisionLedgerJson))
            Assert.True(ledgerDocument.RootElement[0].GetProperty("result")
                .GetProperty("activeCheckoutVerified").GetBoolean());
        SqliteConnection.ClearAllPools();
        var reopened = (await new SqliteEngineeringTaskRepository($"Data Source={_factory.DatabasePath}")
            .GetAsync(id))!;
        Assert.True(reopened.ImplementationResult!.ActiveCheckoutVerified);
        var reopenedRevision = Assert.Single(reopened.ImplementationRevisions);
        Assert.True(reopenedRevision.Result!.ActiveCheckoutVerified);
        Assert.Equal(resultFingerprint, reopenedRevision.ResultFingerprint);

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
    public async Task Exact_implementation_approval_is_idempotent_terminal_and_does_not_touch_either_checkout()
    {
        var repository = _factory.TargetRepositoryPath;
        var created = await CreateAsync(
            "Add report export. Acceptance criteria: export is available. Validation: inspect the bounded diff.",
            repository);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        var generation = await _client.PostAsync($"/api/tasks/{id}/implementation", null);
        Assert.True(generation.IsSuccessStatusCode, await generation.Content.ReadAsStringAsync());
        var generationJson = await generation.Content.ReadAsStringAsync();
        var review = JsonDocument.Parse(generationJson).RootElement.Clone();
        var internalIdentities = await ReadImplementationIdentitiesAsync(_factory.DatabasePath, id);
        Assert.All(internalIdentities, identity =>
            Assert.DoesNotContain(identity, generationJson, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ImplementationBranchDisplay.SafeLabel, generationJson, StringComparison.Ordinal);
        var revision = Assert.Single(review.GetProperty("implementationRevisions").EnumerateArray());
        var commandId = Guid.NewGuid();
        var payload = new
        {
            commandId,
            expectedRowVersion = review.GetProperty("rowVersion").GetInt64(),
            expectedRevisionId = revision.GetProperty("revisionId").GetGuid(),
            expectedResultFingerprint = revision.GetProperty("resultFingerprint").GetString()
        };
        var sourceBefore = DirectoryIdentity(repository);
        var worktreesBefore = DirectoryIdentity(_factory.WorktreeRoot);

        var response = await _client.PostAsJsonAsync($"/api/tasks/{id}/implementation-approval", payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var approvedJson = await response.Content.ReadAsStringAsync();
        var approved = JsonDocument.Parse(approvedJson).RootElement.Clone();
        Assert.All(internalIdentities, identity =>
            Assert.DoesNotContain(identity, approvedJson, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ImplementationBranchDisplay.SafeLabel, approvedJson, StringComparison.Ordinal);
        var approvedRevision = Assert.Single(approved.GetProperty("implementationRevisions").EnumerateArray());
        Assert.Equal("ImplementationApproved", approved.GetProperty("status").GetString());
        Assert.Equal("Approved", approvedRevision.GetProperty("reviewState").GetString());
        Assert.True(approvedRevision.GetProperty("isApproved").GetBoolean());
        Assert.Equal(payload.expectedRevisionId, approved.GetProperty("approvedImplementationRevisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, approved.GetProperty("implementationRuntime").ValueKind);
        Assert.Equal(sourceBefore, DirectoryIdentity(repository));
        Assert.Equal(worktreesBefore, DirectoryIdentity(_factory.WorktreeRoot));

        var approvedRowVersion = approved.GetProperty("rowVersion").GetInt64();
        var approvedAt = approvedRevision.GetProperty("approvedAt").GetString();
        var replay = await _client.PostAsJsonAsync($"/api/tasks/{id}/implementation-approval", payload);
        replay.EnsureSuccessStatusCode();
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(approvedRowVersion, replayed.GetProperty("rowVersion").GetInt64());
        Assert.Equal(approvedAt, Assert.Single(replayed.GetProperty("implementationRevisions").EnumerateArray())
            .GetProperty("approvedAt").GetString());

        var conflict = await _client.PostAsJsonAsync($"/api/tasks/{id}/implementation-approval",
            payload with { commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("workflow_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());
        Assert.Equal(sourceBefore, DirectoryIdentity(repository));
        Assert.Equal(worktreesBefore, DirectoryIdentity(_factory.WorktreeRoot));
    }

    [Fact]
    public async Task Implementation_approval_returns_safe_validation_stale_and_missing_task_problem_details()
    {
        var repository = _factory.TargetRepositoryPath;
        var created = await CreateAsync(
            "Add report export. Acceptance criteria: export is available. Validation: inspect the bounded diff.",
            repository);
        var id = created.GetProperty("id").GetGuid();
        (await _client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        var generated = await _client.PostAsync($"/api/tasks/{id}/implementation", null);
        Assert.True(generated.IsSuccessStatusCode, await generated.Content.ReadAsStringAsync());
        var review = await generated.Content.ReadFromJsonAsync<JsonElement>();
        var revision = Assert.Single(review.GetProperty("implementationRevisions").EnumerateArray());
        var rowVersion = review.GetProperty("rowVersion").GetInt64();
        var revisionId = revision.GetProperty("revisionId").GetGuid();
        var fingerprint = revision.GetProperty("resultFingerprint").GetString()!;

        var malformedRequests = new object[]
        {
            new { commandId = Guid.Empty, expectedRowVersion = rowVersion, expectedRevisionId = revisionId, expectedResultFingerprint = fingerprint },
            new { commandId = Guid.NewGuid(), expectedRowVersion = -1, expectedRevisionId = revisionId, expectedResultFingerprint = fingerprint },
            new { commandId = Guid.NewGuid(), expectedRowVersion = rowVersion, expectedRevisionId = Guid.Empty, expectedResultFingerprint = fingerprint },
            new { commandId = Guid.NewGuid(), expectedRowVersion = rowVersion, expectedRevisionId = revisionId, expectedResultFingerprint = fingerprint.ToUpperInvariant() }
        };
        foreach (var malformed in malformedRequests)
        {
            var response = await _client.PostAsJsonAsync($"/api/tasks/{id}/implementation-approval", malformed);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(_factory.WorktreeRoot, await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }

        foreach (var stale in new object[]
                 {
                     new { commandId = Guid.NewGuid(), expectedRowVersion = rowVersion + 1, expectedRevisionId = revisionId, expectedResultFingerprint = fingerprint },
                     new { commandId = Guid.NewGuid(), expectedRowVersion = rowVersion, expectedRevisionId = Guid.NewGuid(), expectedResultFingerprint = fingerprint },
                     new { commandId = Guid.NewGuid(), expectedRowVersion = rowVersion, expectedRevisionId = revisionId, expectedResultFingerprint = new string('0', 64) }
                 })
        {
            var response = await _client.PostAsJsonAsync($"/api/tasks/{id}/implementation-approval", stale);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("task_concurrency_conflict", problem.GetProperty("code").GetString());
            Assert.DoesNotContain("forge/task-", problem.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var missing = await _client.PostAsJsonAsync($"/api/tasks/{Guid.NewGuid()}/implementation-approval", new
        {
            commandId = Guid.NewGuid(),
            expectedRowVersion = 1,
            expectedRevisionId = Guid.NewGuid(),
            expectedResultFingerprint = new string('a', 64)
        });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("task_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());
    }

    [Fact]
    public async Task Uncertain_checkout_review_returns_422_without_persisting_approval()
    {
        var task = CreatePersistedImplementationReview(activeCheckoutVerified: false);
        var repository = _factory.Services.GetRequiredService<IEngineeringTaskRepository>();
        await repository.SaveAsync(task);
        var revision = Assert.Single(task.ImplementationRevisions);
        var before = await ReadPersistedStateAsync(_factory.DatabasePath, task.Id);
        var commandsBefore = await CountApprovalCommandsAsync(_factory.DatabasePath);

        var response = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/implementation-approval", new
        {
            commandId = Guid.NewGuid(),
            expectedRowVersion = task.RowVersion,
            expectedRevisionId = revision.RevisionId,
            expectedResultFingerprint = revision.ResultFingerprint
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("implementation_review_ineligible", problem.GetProperty("code").GetString());
        Assert.Equal(before, await ReadPersistedStateAsync(_factory.DatabasePath, task.Id));
        Assert.Equal(commandsBefore, await CountApprovalCommandsAsync(_factory.DatabasePath));
    }

    [Fact]
    public void Complete_task_response_redacts_real_branch_when_runtime_workspace_is_unavailable()
    {
        var task = CreatePersistedImplementationReview(activeCheckoutVerified: true);
        var token = task.ImplementationWorkspace!.Token;
        var branch = task.ImplementationWorkspace.Branch;
        var response = EngineeringTaskResponse.FromDomain(
            task,
            new ModelCostResolver(new ModelCostCalculator(new Dictionary<string, ModelPricing>())),
            new ImplementationRuntimeStatus(false, true, ImplementationAttemptDisposition.RecoveryRequired,
                "The persisted review remains readable."));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(response, options);

        Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(branch, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ImplementationBranchDisplay.SafeLabel, json, StringComparison.Ordinal);
        Assert.Contains("\"workspaceAvailable\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_first_command_uses_production_di_without_resolving_operational_git_or_creating_roots()
    {
        using var factory = new ApprovalOnlyNoIoFactory();
        using var client = factory.CreateClient();
        void AssertNoOperationalIo()
        {
            Assert.Equal(0, factory.GitResolver.Calls);
            Assert.False(Directory.Exists(factory.WorktreeRoot));
            foreach (var directory in new[] { ".empty-hooks", ".git-home", ".locks", ".recovery", "recovery" })
                Assert.False(Directory.Exists(Path.Combine(factory.WorktreeRoot, directory)));
        }

        var task = CreatePersistedImplementationReview(activeCheckoutVerified: true);
        await factory.Services.GetRequiredService<IEngineeringTaskRepository>().SaveAsync(task);
        var revision = Assert.Single(task.ImplementationRevisions);
        var payload = new
        {
            commandId = Guid.NewGuid(),
            expectedRowVersion = task.RowVersion,
            expectedRevisionId = revision.RevisionId,
            expectedResultFingerprint = revision.ResultFingerprint
        };

        var malformed = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/implementation-approval",
            payload with { commandId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        AssertNoOperationalIo();
        var missing = await client.PostAsJsonAsync($"/api/tasks/{Guid.NewGuid()}/implementation-approval",
            payload with { commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        AssertNoOperationalIo();
        var stale = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/implementation-approval",
            payload with { expectedRowVersion = task.RowVersion + 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        AssertNoOperationalIo();

        var uncertain = CreatePersistedImplementationReview(activeCheckoutVerified: false);
        await factory.Services.GetRequiredService<IEngineeringTaskRepository>().SaveAsync(uncertain);
        var uncertainRevision = Assert.Single(uncertain.ImplementationRevisions);
        var uncertainResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{uncertain.Id}/implementation-approval", new
            {
                commandId = Guid.NewGuid(),
                expectedRowVersion = uncertain.RowVersion,
                expectedRevisionId = uncertainRevision.RevisionId,
                expectedResultFingerprint = uncertainRevision.ResultFingerprint
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, uncertainResponse.StatusCode);
        AssertNoOperationalIo();

        var approved = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/implementation-approval", payload);
        approved.EnsureSuccessStatusCode();
        AssertNoOperationalIo();
        var replay = await client.PostAsJsonAsync($"/api/tasks/{task.Id}/implementation-approval", payload);
        replay.EnsureSuccessStatusCode();
        AssertNoOperationalIo();
        var conflictingReplay = await client.PostAsJsonAsync(
            $"/api/tasks/{task.Id}/implementation-approval",
            payload with { expectedRowVersion = task.RowVersion + 1 });
        Assert.Equal(HttpStatusCode.Conflict, conflictingReplay.StatusCode);
        AssertNoOperationalIo();

        Assert.Equal(1, await CountApprovalCommandsAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task Task_pdf_export_observes_runtime_read_only_without_creating_locks_or_reverifying_checkout()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        var repository = factory.TargetRepositoryPath;
        var created = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository,
            requirement = "Modify report export. Acceptance criteria: a bounded diff is available. Validation: inspect the diff."
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        var implementation = await client.PostAsync($"/api/tasks/{id}/implementation", null);
        Assert.True(implementation.IsSuccessStatusCode, await implementation.Content.ReadAsStringAsync());

        var locksDirectory = Path.Combine(factory.WorktreeRoot, ".locks");
        if (Directory.Exists(locksDirectory)) Directory.Delete(locksDirectory, recursive: true);
        Assert.False(Directory.Exists(locksDirectory));
        var activeFile = Path.Combine(repository, "src", "ReportExportService.cs");
        await File.AppendAllTextAsync(activeFile, "// active checkout changed after completion\n");
        var activeFileBeforeExport = FileIdentity(activeFile);
        var filesystemBefore = DirectoryIdentity(factory.WorktreeRoot);
        var persistedBefore = await ReadPersistedStateAsync(factory.DatabasePath, id);
        SqliteConnection.ClearAllPools();
        var databaseBefore = FileIdentity(factory.DatabasePath);

        var response = await client.GetAsync($"/api/tasks/{id}/export/pdf");
        response.EnsureSuccessStatusCode();
        var text = ExtractPdf(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(filesystemBefore, DirectoryIdentity(factory.WorktreeRoot));
        Assert.Equal(persistedBefore, await ReadPersistedStateAsync(factory.DatabasePath, id));
        SqliteConnection.ClearAllPools();
        Assert.Equal(databaseBefore, FileIdentity(factory.DatabasePath));
        Assert.Equal(activeFileBeforeExport, FileIdentity(activeFile));
        Assert.False(Directory.Exists(locksDirectory));
        Assert.Contains("Active checkout verified when implementation completed: yes", text);
        Assert.Contains("Valid non-reparse isolated worktree metadata observed at export time: yes", text);
        Assert.Contains("Read-only export-time observation", text);
        Assert.DoesNotContain("Active checkout verified at export time", text);
        Assert.DoesNotContain("Active checkout reverified", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fresh_host_first_pdf_export_does_not_initialize_git_runner_directories_or_mutate_persistence()
    {
        await using var factory = new FakeModeFactory();
        Directory.CreateDirectory(Path.GetDirectoryName(factory.DatabasePath)!);
        var connectionString = $"Data Source={factory.DatabasePath}";
        await new SqliteDatabaseInitializer(connectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(connectionString);
        var task = CreatePersistedImplementationAttempt();
        await repository.SaveAsync(task);
        SqliteConnection.ClearAllPools();
        Assert.False(Directory.Exists(factory.WorktreeRoot));
        var persistedBefore = await ReadPersistedJsonAsync(factory.DatabasePath, task.Id);
        SqliteConnection.ClearAllPools();
        var databaseBefore = FileIdentity(factory.DatabasePath);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/tasks/{task.Id}/export/pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = ExtractPdf(bytes);
        Assert.Contains("Valid non-reparse isolated worktree metadata observed at export time: no", text);
        Assert.Contains("No implementation result was persisted.", text);
        Assert.False(Directory.Exists(factory.WorktreeRoot));
        Assert.False(Directory.Exists(Path.Combine(factory.WorktreeRoot, ".empty-hooks")));
        Assert.False(Directory.Exists(Path.Combine(factory.WorktreeRoot, ".git-home")));
        Assert.False(Directory.Exists(Path.Combine(factory.WorktreeRoot, ".locks")));
        Assert.Equal(persistedBefore, await ReadPersistedJsonAsync(factory.DatabasePath, task.Id));
        SqliteConnection.ClearAllPools();
        Assert.Equal(databaseBefore, FileIdentity(factory.DatabasePath));
    }

    [Fact]
    public async Task Sensitive_implementation_failure_is_absent_from_problem_dto_database_logs_and_pdfs()
    {
        await using var factory = new SensitiveImplementationFailureFactory();
        using var client = factory.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = factory.TargetRepositoryPath,
            requirement = "Add report export. Acceptance criteria: export is available. Validation: inspect the diff."
        });
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();

        var failure = await client.PostAsync($"/api/tasks/{id}/implementation", null);
        var problem = await failure.Content.ReadAsStringAsync();
        var detail = await client.GetStringAsync($"/api/tasks/{id}");
        var taskPdf = ExtractPdf(await client.GetByteArrayAsync($"/api/tasks/{id}/export/pdf"));
        var planPdf = ExtractPdf(await client.GetByteArrayAsync($"/api/tasks/{id}/export/plan-pdf"));
        await using var connection = new SqliteConnection($"Data Source={factory.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(ImplementationResult, '') || coalesce(LastImplementationFailure, '') FROM EngineeringTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var persisted = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failure.StatusCode);
        foreach (var surface in new[] { problem, detail, persisted, taskPdf, planPdf, factory.LogText })
            Assert.DoesNotContain(factory.SensitiveValue, surface, StringComparison.Ordinal);
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
        Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("InvalidOperationException", factory.Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unrelated_key_not_found_exception_remains_an_unexpected_server_failure()
    {
        await using var factory = new GenericKeyNotFoundPdfFactory();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository = "C:/repo",
            requirement = "Add report export. Acceptance criteria: export works. Validation: inspect the report."
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/tasks/{id}/export/pdf");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server_error", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("dictionary storage defect", problem.ToString(), StringComparison.OrdinalIgnoreCase);
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
    public async Task Fake_plan_correction_enforces_three_file_scope_in_dto_and_proposed_plan_pdf()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        var repository = await CreatePlanConstraintTargetAsync(factory);
        var requirement = """
            Modify the greeting behavior in these named files:
            - src/GreetingService.cs
            - config/settings.json
            - README.md
            Acceptance criteria: only the named greeting behavior changes are represented.
            Validation: the initial proposed plan is available for human correction.
            """;
        var createdResponse = await client.PostAsJsonAsync("/api/tasks", new { repository, requirement });
        createdResponse.EnsureSuccessStatusCode();
        var id = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        var initialResponse = await client.PostAsync($"/api/tasks/{id}/plan", null);
        Assert.True(initialResponse.IsSuccessStatusCode, await initialResponse.Content.ReadAsStringAsync());
        var initial = await initialResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(initial.GetProperty("implementationPlan").GetProperty("affectedFiles").EnumerateArray(),
            file => file.GetProperty("path").GetString() == "ManualTarget.csproj");

        var correctionResponse = await client.PostAsJsonAsync($"/api/tasks/{id}/plan-revision", new
        {
            correction = """
                Only the three named files may be affected.
                Remove ManualTarget.csproj everywhere.
                Do not add or update tests.
                Do not propose or run repository validation commands.
                Exactly three Modify actions.
                """
        });
        Assert.True(correctionResponse.IsSuccessStatusCode, await correctionResponse.Content.ReadAsStringAsync());
        var revised = await correctionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var plan = revised.GetProperty("implementationPlan");
        var files = plan.GetProperty("affectedFiles").EnumerateArray().ToArray();

        Assert.Equal("AwaitingPlanApproval", revised.GetProperty("status").GetString());
        Assert.Equal(3, files.Length);
        Assert.Equal(new[] { "src/GreetingService.cs", "config/settings.json", "README.md" },
            files.Select(file => file.GetProperty("path").GetString()));
        Assert.All(files, file => Assert.Equal("Modify", file.GetProperty("action").GetString()));
        Assert.DoesNotContain("ManualTarget.csproj", plan.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.GetProperty("proposedValidationCommands").EnumerateArray());
        Assert.DoesNotContain(plan.GetProperty("orderedSteps").EnumerateArray(), step =>
            step.GetProperty("description").GetString()!.Contains("tests", StringComparison.OrdinalIgnoreCase));

        var pdfText = ExtractPdf(await client.GetByteArrayAsync($"/api/tasks/{id}/export/plan-pdf"));
        Assert.DoesNotContain("ManualTarget.csproj", pdfText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/GreetingService.cs", pdfText, StringComparison.Ordinal);
        Assert.Contains("config/settings.json", pdfText, StringComparison.Ordinal);
        Assert.Contains("README.md", pdfText, StringComparison.Ordinal);
        Assert.Equal("AwaitingPlanApproval",
            (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Exact_manual_requirement_initial_fake_plan_enforces_three_existing_files_and_pdf_scope()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        var repository = await CreatePlanConstraintTargetAsync(factory);
        var requirement = ExactManualRequirement();
        var created = await client.PostAsJsonAsync("/api/tasks", new { repository, requirement });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        var analysis = await client.PostAsync($"/api/tasks/{id}/repository-analysis", null);
        analysis.EnsureSuccessStatusCode();
        Assert.Equal(4, (await analysis.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("evidenceItems").GetArrayLength());

        var response = await client.PostAsync($"/api/tasks/{id}/plan", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var planned = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plan = planned.GetProperty("implementationPlan");
        var files = plan.GetProperty("affectedFiles").EnumerateArray().ToArray();

        Assert.Equal("AwaitingPlanApproval", planned.GetProperty("status").GetString());
        Assert.Equal(new[] { "src/GreetingService.cs", "config/settings.json", "README.md" },
            files.Select(file => file.GetProperty("path").GetString()));
        Assert.All(files, file => Assert.Equal("Modify", file.GetProperty("action").GetString()));
        Assert.Empty(plan.GetProperty("proposedValidationCommands").EnumerateArray());
        Assert.DoesNotContain("ManualTarget.csproj", plan.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan.GetProperty("orderedSteps").EnumerateArray(), step =>
            step.GetProperty("description").GetString()!.Contains("add or update focused tests", StringComparison.OrdinalIgnoreCase));

        var pdfText = ExtractPdf(await client.GetByteArrayAsync($"/api/tasks/{id}/export/plan-pdf"));
        Assert.DoesNotContain("ManualTarget.csproj", pdfText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/GreetingService.cs", pdfText, StringComparison.Ordinal);
        Assert.Contains("config/settings.json", pdfText, StringComparison.Ordinal);
        Assert.Contains("README.md", pdfText, StringComparison.Ordinal);

        var noOp = await client.PostAsJsonAsync($"/api/tasks/{id}/plan-revision", new
        {
            correction = "Exactly three Modify actions."
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noOp.StatusCode);
        Assert.Equal("plan_revision_no_change",
            (await noOp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var restored = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("AwaitingPlanApproval", restored.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("planApprovedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, restored.GetProperty("implementationPlan").ValueKind);
        Assert.Single(restored.GetProperty("planRevisionNotes").EnumerateArray());

        var secondCorrection = await client.PostAsJsonAsync($"/api/tasks/{id}/plan-revision", new
        {
            correction = """
                Modify these existing files only:
                - src/GreetingService.cs
                - config/settings.json
                """
        });
        Assert.True(secondCorrection.IsSuccessStatusCode, await secondCorrection.Content.ReadAsStringAsync());
        var corrected = await secondCorrection.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingPlanApproval", corrected.GetProperty("status").GetString());
        Assert.Equal(2, corrected.GetProperty("planRevisionNotes").GetArrayLength());
    }

    [Fact]
    public async Task Exact_manual_task_report_exports_complete_three_file_plan_and_review_without_mutation()
    {
        await using var factory = new FakeModeFactory();
        using var client = factory.CreateClient();
        var repository = await CreatePlanConstraintTargetAsync(factory);
        var created = await client.PostAsJsonAsync("/api/tasks", new
        {
            repository,
            requirement = ExactManualRequirement()
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/tasks/{id}/requirement-approval", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/repository-analysis", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/tasks/{id}/plan-approval", null)).EnsureSuccessStatusCode();
        var implementation = await client.PostAsync($"/api/tasks/{id}/implementation", null);
        Assert.True(implementation.IsSuccessStatusCode, await implementation.Content.ReadAsStringAsync());
        var implemented = await implementation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AwaitingImplementationReview", implemented.GetProperty("status").GetString());
        Assert.Equal(3, implemented.GetProperty("implementationResult").GetProperty("changedFiles").GetArrayLength());
        var persistedBefore = await ReadPersistedJsonAsync(factory.DatabasePath, id);

        var reportResponse = await client.GetAsync($"/api/tasks/{id}/export/pdf");
        reportResponse.EnsureSuccessStatusCode();
        var text = ExtractPdf(await reportResponse.Content.ReadAsByteArrayAsync());
        var persistedAfter = await ReadPersistedJsonAsync(factory.DatabasePath, id);

        Assert.Equal(persistedBefore, persistedAfter);
        Assert.Contains("Repository analysis", text);
        Assert.Contains("Plan approval status: APPROVED", text);
        Assert.Contains("Implementation review", text);
        Assert.Contains("Changed-file review", text);
        var rawBranch = (await ReadImplementationIdentitiesAsync(factory.DatabasePath, id))[1];
        Assert.Contains(ImplementationBranchDisplay.SafeLabel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(rawBranch, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace token", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Relative path: ManualTarget.csproj", text);
        Assert.Equal(1, CountOccurrences(text, "ManualTarget.csproj"));
        Assert.DoesNotContain("Affected file 1: ManualTarget.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Affected file 2: ManualTarget.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Affected file 3: ManualTarget.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changed file 1: ManualTarget.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changed file 2: ManualTarget.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changed file 3: ManualTarget.csproj", text, StringComparison.OrdinalIgnoreCase);
        foreach (var path in new[] { "src/GreetingService.cs", "config/settings.json", "README.md" })
        {
            Assert.Contains($"Affected file", text);
            Assert.Contains(path, text);
        }
        foreach (var file in implemented.GetProperty("implementationResult").GetProperty("changedFiles").EnumerateArray())
        {
            Assert.Contains(file.GetProperty("path").GetString()!, text, StringComparison.Ordinal);
            Assert.Contains(file.GetProperty("originalContentSha256").GetString()!, text, StringComparison.Ordinal);
            Assert.Contains(file.GetProperty("newContentSha256").GetString()!, text, StringComparison.Ordinal);
            Assert.Contains($"Original bytes: {file.GetProperty("originalBytes").GetInt64():N0}", text);
            Assert.Contains($"Generated bytes: {file.GetProperty("newBytes").GetInt64():N0}", text);
            Assert.Contains($"Original lines: {file.GetProperty("originalLines").GetInt32():N0}", text);
            Assert.Contains($"Generated lines: {file.GetProperty("newLines").GetInt32():N0}", text);
        }
        Assert.Equal(3, CountOccurrences(text, "Original SHA-256:"));
        Assert.Equal(3, CountOccurrences(text, "Generated SHA-256:"));
        Assert.Equal(3, CountOccurrences(text, "Bounded diff preview:"));
        Assert.Contains("Valid non-reparse isolated worktree metadata observed at export time: yes", text);
        Assert.Contains("Active checkout verified when implementation completed: yes", text);
        Assert.Contains("Attempt disposition: Completed", text);
        Assert.DoesNotContain(repository, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.WorktreeRoot, text, StringComparison.OrdinalIgnoreCase);
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
        Assert.True(capabilities.GetProperty("fakeImplementationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("openAiImplementationAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("silentFallbackSupported").GetBoolean());
        Assert.False(capabilities.GetProperty("commitAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("pushAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("deliveryPullRequestAvailable").GetBoolean());
        Assert.True(capabilities.GetProperty("targetModificationAvailable").GetBoolean());
        Assert.True(capabilities.GetProperty("implementationApprovalAvailable").GetBoolean());
        Assert.False(capabilities.GetProperty("implementationCorrectionAvailable").GetBoolean());
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
        Assert.Equal("OpenAI", capabilities.GetProperty("implementationProvider").GetString());
        Assert.False(capabilities.GetProperty("implementationConfigured").GetBoolean());
        Assert.False(capabilities.GetProperty("openAiImplementationAvailable").GetBoolean());
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
        Assert.Equal("OpenAI", capabilities.ImplementationProvider);
        Assert.True(capabilities.OpenAiImplementationAvailable);
        Assert.True(capabilities.ImplementationConfigured);
        Assert.False(capabilities.SilentFallbackSupported);
    }

    [Fact]
    public void Task_api_uses_central_resolved_cost_and_exposes_only_stored_per_call_rates()
    {
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Add report export", now);
        var snapshot = new ModelPricingSnapshot(10m, 2m, 20m);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "model", "medium",
            now, now, true, "response", 100, 25, 50, 40, 999m, null, snapshot, "request-safe"), now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "legacy-model", "low",
            now, now, true, "legacy-response", 0, 0, 0, 0, 0m, null), now);
        task.RecordModelCall(new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Implementation, "OpenAI", "model", "high",
            now, now, false, null, null, null, null, null, 0m, "provider_error", snapshot), now);
        var calculator = new ModelCostCalculator(new Dictionary<string, ModelPricing>
        {
            ["model"] = new(100m, 100m, 100m)
        });

        var response = EngineeringTaskResponse.FromDomain(task, new ModelCostResolver(calculator));

        var call = Assert.Single(response.Telemetry.Calls, item => item.ProviderResponseId == "response");
        var legacyCall = Assert.Single(response.Telemetry.Calls, item => item.ProviderResponseId == "legacy-response");
        var unavailable = Assert.Single(response.Telemetry.Calls, item => item.Stage == ModelCallStage.Implementation);
        Assert.Equal(75, call.UncachedInputTokens);
        Assert.Equal("request-safe", call.ProviderRequestId);
        Assert.Equal("stored pricing snapshot", call.PricingProvenance);
        Assert.Equal("legacy estimate \u2014 pricing snapshot unavailable", legacyCall.PricingProvenance);
        Assert.Equal(0.0018m, call.EstimatedCostUsd);
        Assert.Equal(snapshot.InputPerMillionUsd, call.StoredPricingSnapshot?.InputPerMillionUsd);
        Assert.True(call.UsageAvailable);
        Assert.False(unavailable.UsageAvailable);
        Assert.Null(unavailable.InputTokens);
        Assert.Null(unavailable.EstimatedCostUsd);
        Assert.True(response.Telemetry.IsPartialEstimate);
        Assert.Equal(1, response.Telemetry.CostUnavailableCallCount);
    }

    [Fact]
    public void Task_api_aggregates_one_and_multiple_complete_usage_records()
    {
        var one = TelemetryFor(UsageCall(100, 20, 30, null));

        Assert.Equal(ModelUsageAvailability.Complete, one.UsageAvailability);
        Assert.Equal(0, one.UsageUnavailableCallCount);
        Assert.Equal(100, one.TotalInputTokens);
        Assert.Equal(20, one.TotalCachedInputTokens);
        Assert.Equal(30, one.TotalOutputTokens);
        Assert.Null(one.TotalReasoningTokens);
        Assert.NotNull(one.TotalEstimatedCostUsd);

        var multiple = TelemetryFor(
            UsageCall(100, 20, 30, 5),
            UsageCall(40, 5, 10, 2));

        Assert.Equal(ModelUsageAvailability.Complete, multiple.UsageAvailability);
        Assert.Equal(140, multiple.TotalInputTokens);
        Assert.Equal(25, multiple.TotalCachedInputTokens);
        Assert.Equal(40, multiple.TotalOutputTokens);
        Assert.Equal(7, multiple.TotalReasoningTokens);
        Assert.NotNull(multiple.TotalEstimatedCostUsd);
    }

    [Fact]
    public void Task_api_serializes_unavailable_usage_and_cost_as_null_instead_of_zero()
    {
        var telemetry = TelemetryFor(UsageCall(null, null, null, null));
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.SerializeToElement(telemetry, jsonOptions);

        Assert.Equal(ModelUsageAvailability.Unavailable, telemetry.UsageAvailability);
        Assert.Equal(1, telemetry.UsageUnavailableCallCount);
        Assert.Null(telemetry.TotalInputTokens);
        Assert.Null(telemetry.TotalCachedInputTokens);
        Assert.Null(telemetry.TotalOutputTokens);
        Assert.Null(telemetry.TotalReasoningTokens);
        Assert.Null(telemetry.TotalEstimatedCostUsd);
        Assert.Equal("Unavailable", json.GetProperty("usageAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("totalInputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("totalOutputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("totalEstimatedCostUsd").ValueKind);
        Assert.DoesNotContain("\"totalInputTokens\":0", json.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"totalOutputTokens\":0", json.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"totalEstimatedCostUsd\":0", json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Task_api_reports_all_unavailable_and_mixed_usage_without_false_totals()
    {
        var allUnavailable = TelemetryFor(
            UsageCall(null, null, null, null),
            UsageCall(null, null, null, null));
        var mixed = TelemetryFor(
            UsageCall(100, 20, 30, 5),
            UsageCall(null, null, null, null));

        Assert.Equal(ModelUsageAvailability.Unavailable, allUnavailable.UsageAvailability);
        Assert.Equal(2, allUnavailable.UsageUnavailableCallCount);
        Assert.Null(allUnavailable.TotalInputTokens);
        Assert.Null(allUnavailable.TotalEstimatedCostUsd);
        Assert.Equal(ModelUsageAvailability.Partial, mixed.UsageAvailability);
        Assert.Equal(1, mixed.UsageUnavailableCallCount);
        Assert.Null(mixed.TotalInputTokens);
        Assert.Null(mixed.TotalCachedInputTokens);
        Assert.Null(mixed.TotalOutputTokens);
        Assert.Null(mixed.TotalReasoningTokens);
        Assert.Null(mixed.TotalEstimatedCostUsd);
    }

    [Fact]
    public void Task_api_preserves_provider_reported_zero_as_complete_usage()
    {
        var telemetry = TelemetryFor(UsageCall(0, 0, 0, 0));

        Assert.Equal(ModelUsageAvailability.Complete, telemetry.UsageAvailability);
        Assert.Equal(0, telemetry.UsageUnavailableCallCount);
        Assert.Equal(0, telemetry.TotalInputTokens);
        Assert.Equal(0, telemetry.TotalCachedInputTokens);
        Assert.Equal(0, telemetry.TotalOutputTokens);
        Assert.Equal(0, telemetry.TotalReasoningTokens);
        Assert.Equal(0m, telemetry.TotalEstimatedCostUsd);
    }

    private static ModelTelemetryResponse TelemetryFor(params ModelCallRecord[] calls)
    {
        var task = EngineeringTask.Create("C:/repo", "Report truthful usage", DateTimeOffset.UtcNow);
        foreach (var call in calls) task.RecordModelCall(call, call.CompletedAt);
        var calculator = new ModelCostCalculator(new Dictionary<string, ModelPricing>
        {
            ["model"] = new(10m, 2m, 20m)
        });
        return EngineeringTaskResponse.FromDomain(task, new ModelCostResolver(calculator)).Telemetry;
    }

    private static ModelCallRecord UsageCall(int? input, int? cached, int? output, int? reasoning)
    {
        var now = DateTimeOffset.UtcNow;
        return new ModelCallRecord(
            Guid.NewGuid(), ModelCallStage.Implementation, "OpenAI", "model", "high",
            now, now, true, "response-safe", input, cached, output, reasoning, null, null,
            new ModelPricingSnapshot(10m, 2m, 20m), "request-safe");
    }

    private static string ExactManualRequirement() => """
        Prepare a safe manual-validation change in this repository.

        Modify these existing files only:
        - src/GreetingService.cs
        - config/settings.json
        - README.md

        The purpose is to demonstrate Forge AI's isolated implementation and diff-review workflow in Fake mode. Do not run the target project's build or tests. Do not stage, commit, push, or create a pull request.

        Acceptance criteria:
        - The approved plan identifies only the three existing files listed above.
        - Implementation changes are created only in an isolated Forge worktree.
        - The original target checkout remains completely unchanged.
        - A bounded and clearly labelled Fake implementation diff is available for review.

        Validation:
        - Review the Forge-generated file list, hashes, byte and line counts, and diff previews.
        - Compare the target repository branch, HEAD, status, index, and file hashes before and after implementation.
        """;

    private static EngineeringTask CreatePersistedImplementationAttempt()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var task = EngineeringTask.Create("C:/persisted/source-repository", "Generate a safe report.", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Generate a safe report."), now);
        task.ApproveRequirementSummary(now.AddMinutes(1));
        task.BeginRepositoryAnalysis(now.AddMinutes(2));
        var metadata = new RepositoryFileMetadata("src/App.cs", ".cs", 20, 1, "source", false, "App", ["App"]);
        var snapshot = new RepositorySnapshot("C:/persisted/source-repository", true, "main", "aaaaaaaa",
            new string('a', 40), "clean", 1, 1, 0, ["C#"], [".cs"], ["Forge.csproj"], [], [],
            now.AddMinutes(2), new string('f', 64), [metadata], new string('d', 64));
        var evidence = new EvidenceItem("E1", "src/App.cs", 1, 1, "class App {}", "Direct evidence", 10,
            new string('e', 64));
        task.StoreRepositorySnapshot(snapshot, now.AddMinutes(2));
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now.AddMinutes(2));
        var plan = new ImplementationPlan("Implement report", "Generate a safe report.", "The file is persisted.",
            [new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Update the source.", ["E1"], .9m)],
            [new ImplementationStep(1, "Update the source.", ["src/App.cs"], ["E1"], "The source changes.")],
            ["inspect diff"], [], [], [],
            [new RequirementCoverageItem("Generate the report.", ["src/App.cs"], [1])],
            "A bounded report change.", PlanningSource.DeterministicFake, null, now.AddMinutes(3), snapshot.Fingerprint);
        task.StoreImplementationPlan(plan, now.AddMinutes(3), TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(now.AddMinutes(4));
        const string token = "0123456789abcdef0123456789abcdef";
        var workspace = new ImplementationWorkspace(token, $"forge/task-{token}", snapshot.FullHeadSha!,
            ImplementationWorkspacePhase.Reserved, now.AddMinutes(5), now.AddMinutes(5), false,
            new string('1', 64), new string('2', 64), $"refs/forge/tasks/{token}");
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            now.AddMinutes(5), now.AddMinutes(5), now.AddMinutes(6));
        task.BeginImplementation(workspace, lease, now.AddMinutes(5));
        return task;
    }

    private static EngineeringTask CreatePersistedImplementationReview(bool activeCheckoutVerified)
    {
        var task = CreatePersistedImplementationAttempt();
        var workspace = task.ImplementationWorkspace!;
        var lease = task.ImplementationLease!;
        const string diff = "diff --git a/src/App.cs b/src/App.cs";
        var result = new ImplementationResult(
            ImplementationSource.DeterministicFake,
            null,
            workspace.BaseCommitSha,
            workspace.Branch,
            "Persisted implementation review.",
            [],
            [new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify,
                new string('3', 64), new string('4', 64), 10, 20, 1, 2, 1, 0,
                diff, diff.Length, diff.Length, false, Encoding.UTF8.GetByteCount(diff),
                Encoding.UTF8.GetByteCount(diff))],
            diff.Length,
            diff.Length,
            false,
            task.ImplementationStartedAt!.Value.AddMinutes(1),
            Encoding.UTF8.GetByteCount(diff),
            Encoding.UTF8.GetByteCount(diff),
            true,
            new string('5', 64),
            1,
            20);
        task.StoreImplementationResult(result, lease.AttemptId, lease.OwnerId, result.CompletedAt);
        if (!activeCheckoutVerified)
            task.RecordImplementationPostconditionFailure(new ImplementationFailure(
                "implementation_active_checkout_changed",
                "Forge could not verify that the active checkout remained unchanged.",
                true,
                result.CompletedAt.AddMinutes(1),
                ActiveCheckoutVerified: false), result.CompletedAt.AddMinutes(1));
        return task;
    }

    private static async Task<string> CreatePlanConstraintTargetAsync(FakeModeFactory factory)
    {
        var repository = Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "plan-constraint-target");
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        Directory.CreateDirectory(Path.Combine(repository, "config"));
        await File.WriteAllTextAsync(Path.Combine(repository, "src", "GreetingService.cs"),
            "public sealed class GreetingService { public string Greet() => \"Hello\"; }\n");
        await File.WriteAllTextAsync(Path.Combine(repository, "config", "settings.json"), "{\"greeting\":\"Hello\"}\n");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Greeting service\n");
        await File.WriteAllTextAsync(Path.Combine(repository, "ManualTarget.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- GreetingService config settings README --></Project>\n");
        FakeModeFactory.RunGit(repository, "init", "--initial-branch=main");
        FakeModeFactory.RunGit(repository, "add", "--", ".");
        FakeModeFactory.RunGit(repository, "-c", "user.name=Forge API Tests",
            "-c", "user.email=forge-tests@example.invalid", "commit", "-m", "constraint fixture");
        return repository;
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
                   LastImplementationFailure, ImplementationLease, ImplementationRevisions,
                   ActiveImplementationRevisionId, ApprovedImplementationRevisionId
            FROM EngineeringTasks WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return string.Join('|', Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString()));
    }

    private static async Task<long> CountApprovalCommandsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImplementationApprovalCommands;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string CompatibilityResult, string RevisionLedger)> ReadImplementationResultJsonAsync(
        string databasePath,
        Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ImplementationResult, ImplementationRevisions FROM EngineeringTasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string[]> ReadImplementationIdentitiesAsync(
        string databasePath,
        Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ImplementationWorkspace FROM EngineeringTasks WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);
        return
        [
            document.RootElement.GetProperty("token").GetString()!,
            document.RootElement.GetProperty("branch").GetString()!,
            document.RootElement.GetProperty("repositoryIdentity").GetString()!,
            document.RootElement.GetProperty("gitCommonDirectoryIdentity").GetString()!,
            document.RootElement.GetProperty("ownershipReference").GetString()!
        ];
    }

    private static async Task<string> ReadPersistedJsonAsync(string databasePath, Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ClarificationAnswers, RequirementRevisionNotes, ModelCalls, RepositorySnapshot,
                   EvidenceItems, ImplementationPlan, PlanRevisionNotes, ImplementationWorkspace,
                   ImplementationResult, LastImplementationFailure, ImplementationLease,
                   ImplementationRevisions, ActiveImplementationRevisionId, ApprovedImplementationRevisionId
            FROM EngineeringTasks WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return string.Join('|', Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetString(index)));
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0;
             index += expected.Length)
            count++;
        return count;
    }

    private static string FileIdentity(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var info = new FileInfo(path);
        return $"{bytes.LongLength}:{info.LastWriteTimeUtc:O}:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }

    private static string DirectoryIdentity(string root)
    {
        if (!Directory.Exists(root)) return "<missing>";
        return string.Join('\n', Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                return Directory.Exists(path) ? $"D|{relative}" : $"F|{relative}|{FileIdentity(path)}";
            }));
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

public sealed class ApprovalOnlyNoIoFactory : FakeModeFactory
{
    public CountingRejectingGitResolver GitResolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGitExecutablePathResolver>();
            services.AddSingleton<IGitExecutablePathResolver>(GitResolver);
        });
    }

    public sealed class CountingRejectingGitResolver : IGitExecutablePathResolver
    {
        public int Calls { get; private set; }

        public string Resolve(string? configuredPath)
        {
            Calls++;
            throw new InvalidOperationException("The approval dependency graph resolved Git unexpectedly.");
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

public sealed class SensitiveImplementationFailureFactory : FakeModeFactory
{
    private readonly CapturingLoggerProvider loggerProvider = new();
    public string SensitiveValue { get; } = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                                             Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "Aa1-";
    public string LogText => loggerProvider.Text;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImplementationWorkspaceManager>();
            services.AddSingleton<IImplementationWorkspaceManager>(new SensitiveFailureWorkspaceManager(this));
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });
    }

    private sealed class SensitiveFailureWorkspaceManager(SensitiveImplementationFailureFactory owner)
        : IImplementationWorkspaceManager
    {
        private static readonly string Token = new('a', 32);

        public Task<ImplementationInspection> InspectAsync(string repositoryPath, RepositorySnapshot snapshot,
            ImplementationPlan plan, ImplementationLimits limits, CancellationToken cancellationToken = default)
        {
            var files = plan.AffectedFiles.Select(file => file.Action switch
            {
                PlannedFileAction.Create => new ImplementationFileContext(file.Path, file.Action, null, null),
                _ => Context(file.Path, file.Action)
            }).ToArray();
            return Task.FromResult(new ImplementationInspection(
                new ActiveCheckoutSignature("main", snapshot.FullHeadSha!, "status", "index"), files,
                new string('1', 64), new string('2', 64)));
        }

        public Task<ImplementationReservation> ReserveAsync(Guid taskId, string repositoryPath,
            RepositorySnapshot snapshot, ImplementationPlan plan, CancellationToken cancellationToken = default)
        {
            var files = plan.AffectedFiles.Select(file => file.Action switch
            {
                PlannedFileAction.Create => new ImplementationFileContext(file.Path, file.Action, null, null),
                _ => Context(file.Path, file.Action)
            }).ToArray();
            var now = DateTimeOffset.UtcNow;
            var workspace = new ImplementationWorkspace(Token, $"forge/task-{Token}", snapshot.FullHeadSha!,
                ImplementationWorkspacePhase.Reserved, now, now, false, new string('1', 64),
                new string('2', 64), $"refs/forge/tasks/{Token}");
            return Task.FromResult(new ImplementationReservation(workspace,
                new ActiveCheckoutSignature("main", snapshot.FullHeadSha!, "status", "index"), files));
        }

        public Task<PreparedImplementationWorkspace> PrepareAsync(string repositoryPath,
            ImplementationWorkspace workspace, ImplementationPlan plan, ImplementationLimits limits,
            ActiveCheckoutSignature activeCheckout, CancellationToken cancellationToken = default)
        {
            var files = plan.AffectedFiles.Select(file => file.Action switch
            {
                PlannedFileAction.Create => new ImplementationFileContext(file.Path, file.Action, null, null),
                _ => Context(file.Path, file.Action)
            }).ToArray();
            return Task.FromResult(new PreparedImplementationWorkspace(
                workspace with { Phase = ImplementationWorkspacePhase.Ready, IsAvailable = true },
                activeCheckout, files, new HeldWorkspaceLock()));
        }

        public Task<ImplementationResult> ApplyAsync(string repositoryPath, PreparedImplementationWorkspace prepared,
            ImplementationOutput output, ImplementationLimits limits, DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImplementationResult>(new ImplementationException("implementation_failure",
                $"deployment credential: {owner.SensitiveValue}", true));

        public Task<bool> IsAvailableAsync(string repositoryPath, ImplementationWorkspace workspace,
            ImplementationPlan plan, ImplementationResult? result, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task VerifyActiveCheckoutAsync(string repositoryPath, ImplementationPlan plan,
            ActiveCheckoutSignature expected, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static ImplementationFileContext Context(string path, PlannedFileAction action)
        {
            const string content = "existing fixture content\n";
            return new ImplementationFileContext(path, action, content, ImplementationOutputValidator.Hash(content));
        }
    }

    private sealed class HeldWorkspaceLock : IImplementationWorkspaceLock
    {
        public bool IsHeld { get; private set; } = true;
        public ValueTask DisposeAsync()
        {
            IsHeld = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();
        public string Text => string.Join('\n', messages);
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null) messages.Enqueue(exception.ToString());
            }
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
    public ApiLogCapture Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEngineeringTaskPdfExporter>();
            services.AddSingleton<IEngineeringTaskPdfExporter, FailingPdfExporter>();
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }

    private sealed class FailingPdfExporter : IEngineeringTaskPdfExporter
    {
        public byte[] Export(EngineeringTask task, ImplementationReportRuntimeStatus? runtimeStatus = null) =>
            throw new InvalidOperationException("sensitive-pdf-generator-detail");
    }
}

public sealed class MissingTaskLoggingFactory : FakeModeFactory
{
    public ApiLogCapture Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
    }
}

public sealed class GenericKeyNotFoundPdfFactory : FakeModeFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEngineeringTaskPdfExporter>();
            services.AddSingleton<IEngineeringTaskPdfExporter, GenericKeyNotFoundPdfExporter>();
        });
    }

    private sealed class GenericKeyNotFoundPdfExporter : IEngineeringTaskPdfExporter
    {
        public byte[] Export(EngineeringTask task, ImplementationReportRuntimeStatus? runtimeStatus = null) =>
            throw new KeyNotFoundException("dictionary storage defect");
    }
}

public sealed record ApiLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    string? ExceptionText);

public sealed class ApiLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<ApiLogEntry> entries = new();
    public IReadOnlyList<ApiLogEntry> Entries => entries.ToArray();
    public string Text => string.Join('\n', entries.Select(entry =>
        $"{entry.Level} {entry.Category}: {entry.Message}{Environment.NewLine}{entry.ExceptionText}"));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);
    public void Dispose() { }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<ApiLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new ApiLogEntry(category, logLevel, formatter(state, exception), exception?.ToString()));
    }
}
