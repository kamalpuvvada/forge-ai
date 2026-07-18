using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class OpenAIPlanningEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_plan_maps_provenance_request_and_usage_cost()
    {
        var gateway = new CapturingGateway(Envelope(ValidJson()));
        var result = await CreateEngine(gateway).CreatePlanAsync(Context());

        Assert.Equal(PlanningSource.OpenAI, result.Plan.Source);
        Assert.Equal("gpt-5.6-sol", result.Plan.PlanningModel);
        Assert.Equal(ModelCallStage.Planning, result.ModelCall?.Stage);
        Assert.Equal(0.0071m, result.ModelCall?.EstimatedCostUsd);
        Assert.Equal("gpt-5.6-sol", gateway.Request?.Model);
        Assert.Equal("medium", gateway.Request?.ReasoningEffort);
        Assert.Equal(2400, gateway.Request?.MaxOutputTokens);
        Assert.Equal("forge_implementation_plan", gateway.Request?.SchemaName);
        Assert.Contains("additionalProperties", gateway.Request?.JsonSchema);
    }

    [Fact]
    public async Task Valid_new_file_is_accepted_without_inventing_existing_evidence()
    {
        var json = ValidJson()
            .Replace("src/App.cs", "src/NewExport.cs", StringComparison.Ordinal)
            .Replace("\"action\":\"modify\"", "\"action\":\"create\"", StringComparison.Ordinal)
            .Replace("[\"E1\"]", "[]", StringComparison.Ordinal);

        var result = await CreateEngine(new CapturingGateway(Envelope(json))).CreatePlanAsync(Context());

        Assert.Equal(PlannedFileAction.Create, Assert.Single(result.Plan.AffectedFiles).Action);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown_evidence")]
    [InlineData("missing_evidence")]
    [InlineData("missing_modify_path")]
    [InlineData("create_existing_path")]
    [InlineData("traversal")]
    [InlineData("absolute_path")]
    [InlineData("duplicate_order")]
    [InlineData("undeclared_step_path")]
    [InlineData("validation_claim")]
    public async Task Invalid_structured_plans_are_rejected_with_failed_planning_telemetry(string mutation)
    {
        var exception = await Assert.ThrowsAsync<PlanningProviderException>(() =>
            CreateEngine(new CapturingGateway(Envelope(Mutate(ValidJson(), mutation)))).CreatePlanAsync(Context()));

        Assert.Equal("invalid_plan_response", exception.Category);
        Assert.Equal(ModelCallStage.Planning, exception.FailedCall.Stage);
        Assert.False(exception.FailedCall.Succeeded);
        Assert.Equal("resp_plan", exception.FailedCall.ProviderResponseId);
    }

    [Fact]
    public async Task Transport_failure_is_safe_and_preserves_category()
    {
        const string secret = "provider-secret-detail";
        var exception = await Assert.ThrowsAsync<PlanningProviderException>(() =>
            CreateEngine(new ThrowingGateway(new OpenAITransportException(
                "rate_limit", "OpenAI rate-limited the planning request.", new Exception(secret))))
                .CreatePlanAsync(Context()));

        Assert.Equal("rate_limit", exception.Category);
        Assert.Equal("rate_limit", exception.FailedCall.FailureCategory);
        Assert.DoesNotContain(secret, exception.Message);
        Assert.DoesNotContain(secret, exception.FailedCall.ToString());
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_wrapping()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateEngine(new CancellationGateway()).CreatePlanAsync(Context(), cancellation.Token));
    }

    [Fact]
    public async Task Missing_api_key_configuration_is_rejected_before_a_call()
    {
        await Assert.ThrowsAsync<PlanningException>(() => CreateEngine(null).CreatePlanAsync(Context()));
    }

    [Fact]
    public void Canonical_context_omits_absolute_root_and_redacts_secrets()
    {
        const string secret = "do-not-send-this";
        var context = Context(original: $"Export reports. api_key={secret}");

        var serialized = OpenAIPlanningEngine.BuildCanonicalContext(context);

        Assert.DoesNotContain(context.Snapshot.NormalizedRoot, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", serialized, StringComparison.Ordinal);
        Assert.Contains("src/App.cs", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("fullHeadSha", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fake_planning_is_async_and_records_no_model_call()
    {
        var result = await new FakePlanningEngine().CreatePlanAsync(Context());
        Assert.Equal(PlanningSource.DeterministicFake, result.Plan.Source);
        Assert.Null(result.Plan.PlanningModel);
        Assert.Null(result.ModelCall);
    }

    private static OpenAIPlanningEngine CreateEngine(IOpenAIResponsesGateway? gateway)
    {
        var options = new ForgeAiOptions { Mode = ForgeAiModes.OpenAI };
        return new OpenAIPlanningEngine(options, gateway, new ModelCostCalculator(options.Pricing), TimeProvider.System);
    }

    private static PlanningContext Context(string original = "Add report export")
    {
        var snapshot = new RepositorySnapshot(
            @"C:\private\target-repository", true, "main", "abc1234", "abc123456789", "clean",
            1, 1, 0, ["C#/.NET"], [".cs"], ["Forge.slnx"], ["tests"], [], Now, "fingerprint",
            [new RepositoryFileMetadata("src/App.cs", ".cs", 200, 10, "source", false, null, ["App"])]);
        var evidence = new EvidenceItem("E1", "src/App.cs", 1, 10, "public class App { }", "report term in content", 40, "hash");
        return new PlanningContext(original, "Add a bounded report export", [], [], snapshot, [evidence], Now);
    }

    private static OpenAIResponseEnvelope Envelope(string output) => new("resp_plan", output, 1000, 200, 100, 25);

    private static string ValidJson() => """
        {"title":"Plan report export","objective":"Add bounded report export behavior.","repositoryUnderstanding":"Evidence E1 identifies the application surface.","affectedFiles":[{"path":"src/App.cs","action":"modify","purpose":"Expose report export behavior backed by E1.","evidenceIds":["E1"],"confidence":0.9}],"orderedSteps":[{"order":1,"description":"Add the report export behavior.","affectedPaths":["src/App.cs"],"evidenceIds":["E1"],"expectedResult":"The report export behavior is represented."}],"proposedValidationCommands":["dotnet test ForgeAI.slnx"],"risks":["Output size needs a bound."],"assumptions":["The current API shape remains stable."],"unresolvedQuestions":[],"summary":"A focused evidence-backed export plan."}
        """;

    private static string Mutate(string json, string mutation) => mutation switch
    {
        "malformed" => "{not-json",
        "unknown_evidence" => json.Replace("[\"E1\"]", "[\"E99\"]", StringComparison.Ordinal),
        "missing_evidence" => json.Replace("[\"E1\"]", "[]", StringComparison.Ordinal),
        "missing_modify_path" => json.Replace("src/App.cs", "src/Missing.cs", StringComparison.Ordinal),
        "create_existing_path" => json.Replace("\"action\":\"modify\"", "\"action\":\"create\"", StringComparison.Ordinal),
        "traversal" => json.Replace("src/App.cs", "../outside.cs", StringComparison.Ordinal),
        "absolute_path" => json.Replace("Plan report export", @"Plan C:\private\target-repository export", StringComparison.Ordinal),
        "duplicate_order" => json.Replace(
            "}],\"proposedValidationCommands\"",
            "},{\"order\":1,\"description\":\"Duplicate.\",\"affectedPaths\":[\"src/App.cs\"],\"evidenceIds\":[\"E1\"],\"expectedResult\":\"Duplicate result.\"}],\"proposedValidationCommands\"",
            StringComparison.Ordinal),
        "undeclared_step_path" => json.Replace("\"affectedPaths\":[\"src/App.cs\"]", "\"affectedPaths\":[\"src/Other.cs\"]", StringComparison.Ordinal),
        "validation_claim" => json.Replace("The report export behavior is represented.", "All tests passed.", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation))
    };

    private sealed class CapturingGateway(OpenAIResponseEnvelope envelope) : IOpenAIResponsesGateway
    {
        public OpenAIResponseRequest? Request { get; private set; }
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(envelope);
        }
    }

    private sealed class ThrowingGateway(Exception exception) : IOpenAIResponsesGateway
    {
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<OpenAIResponseEnvelope>(exception);
    }

    private sealed class CancellationGateway : IOpenAIResponsesGateway
    {
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<OpenAIResponseEnvelope>(cancellationToken);
    }
}
