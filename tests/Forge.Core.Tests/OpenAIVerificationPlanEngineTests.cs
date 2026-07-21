using System.Text;
using System.Text.Json;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class OpenAIVerificationPlanEngineTests
{
    [Fact]
    public void Verification_configuration_and_strict_schema_are_bounded()
    {
        var options = new ForgeAiOptions();
        Assert.Equal("gpt-5.6-sol", options.VerificationPlanningModel);
        Assert.Equal("medium", options.VerificationPlanningReasoningEffort);
        Assert.Equal(8_000, options.VerificationPlanningMaxOutputTokens);
        options.ValidateSyntax();
        using var schema = JsonDocument.Parse(OpenAIVerificationPlanEngine.ResponseSchema);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(12, schema.RootElement.GetProperty("properties").GetProperty("testCases").GetProperty("maxItems").GetInt32());
        Assert.Contains("Never claim", OpenAIVerificationPlanEngine.DeveloperInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_context_is_bounded_and_excludes_private_execution_identity()
    {
        var context = Context();
        var canonical = OpenAIVerificationPlanEngine.BuildCanonicalContext(context);
        Assert.True(Encoding.UTF8.GetByteCount(canonical) <= OpenAIVerificationPlanEngine.MaximumCanonicalContextBytes);
        Assert.DoesNotContain("C:/repo", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceToken", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerRef", canonical, StringComparison.OrdinalIgnoreCase);
        using var parsed = JsonDocument.Parse(canonical);
        Assert.Equal(VerificationTrustLabels.ManualNotExecuted,
            parsed.RootElement.GetProperty("trustBoundary").GetString());
    }

    [Fact]
    public void Duplicate_properties_are_rejected_before_deserialization()
    {
        var context = Context();
        var json = ValidJson(context).Replace("\"summary\":", "\"summary\":\"first\",\"summary\":", StringComparison.Ordinal);
        var exception = Assert.Throws<VerificationException>(() => OpenAIVerificationPlanEngine.Parse(json));
        Assert.Equal("verification_invalid_structured_output", exception.Category);
    }

    [Fact]
    public void Complete_valid_wire_output_is_accepted_for_local_validation()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        {
            Model = "gpt-5.6-sol",
            ReasoningEffort = "medium"
        };
        var plan = VerificationValidator.FinalizeCandidate(context, candidate, 1, Guid.NewGuid(), [], new VerificationLimits());
        Assert.Single(plan.TestCases);
        Assert.Equal(context.ContextFingerprint, plan.GenerationContextFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", plan.PlanFingerprint);
    }

    [Fact]
    public async Task Exact_final_projection_is_scanned_before_provider_dispatch()
    {
        var secret = SyntheticSensitiveValues.Jwt();
        var original = Context();
        var contexts = new List<VerificationPlanContext>
        {
            Rebind(original with { ApprovedValidationCommands = [new ApprovedValidationCommand("V1", $"dotnet test --token {secret}")] }),
            original with { ApprovedPlan = original.ApprovedPlan with { Risks = [$"Risk {secret}"] } },
            original with { ApprovedPlan = original.ApprovedPlan with { Assumptions = [$"Assume {secret}"] } },
            original with { ApprovedPlan = original.ApprovedPlan with { RequirementCoverage = [new RequirementCoverageItem($"Coverage {secret}", ["src/App.cs"], [1])] } },
            original with { ImplementationResult = original.ImplementationResult with { Warnings = [$"Warning {secret}"] } },
            original with { RepositoryEvidence = [original.RepositoryEvidence[0] with { Excerpt = $"Evidence {secret}" }] },
            Rebind(original with { ImplementationResult = original.ImplementationResult with { ChangedFiles = [original.ImplementationResult.ChangedFiles[0] with { DiffPreview = $"diff {secret}" }] } })
        };

        foreach (var context in contexts)
        {
            var gateway = new CountingGateway();
            var exception = await Assert.ThrowsAsync<VerificationException>(() => Engine(gateway).GenerateAsync(context));
            Assert.Equal("verification_sensitive_context", exception.Category);
            Assert.Equal(0, gateway.RequestCount);
        }
    }

    [Theory]
    [InlineData("rate_limit", 429)]
    [InlineData("provider_error", 502)]
    [InlineData("provider_error", 503)]
    public async Task Explicit_retryable_provider_response_is_durably_checkpointed_before_one_retry(
        string category, int status)
    {
        var context = Context();
        var gateway = new QueueGateway(
            new OpenAITransportException(category, "safe", statusCode: status,
                dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived),
            Envelope(ValidJson(context)));
        var observer = new TrackingObserver();

        var evaluation = await Engine(gateway).GenerateAsync(context, observer);

        Assert.Equal(2, evaluation.ModelCalls.Count);
        Assert.Equal(new[]
        {
            VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.RetryableProviderResponse,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.ResponseReceived
        }, observer.Checkpoints);
        Assert.Single(observer.Responses);
        Assert.Equal(VerificationCallDispatchDisposition.ResponseReceived,
            observer.Responses[0].DispatchDisposition);
        Assert.Equal(status, observer.TransportOutcomes[0].Call.ProviderHttpStatusCode);
    }

    [Fact]
    public async Task Ambiguous_transport_failure_is_not_retried_and_remains_durably_ambiguous()
    {
        var gateway = new QueueGateway(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DispatchMayHaveOccurred));
        var observer = new TrackingObserver();

        var exception = await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(gateway).GenerateAsync(Context(), observer));

        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, exception.DurableStatus);
        Assert.Equal(new[] { VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.AmbiguousAfterDispatch }, observer.Checkpoints);
        Assert.Single(gateway.Requests);
        Assert.Equal(VerificationCallDispatchDisposition.PossiblyDispatched,
            Assert.Single(observer.TransportOutcomes).Disposition);
    }

    [Fact]
    public async Task Timeout_or_cancellation_after_dispatch_is_durably_ambiguous_and_never_retried()
    {
        var gateway = new QueueGateway(new OperationCanceledException("simulated timeout after dispatch"));
        var observer = new TrackingObserver();

        var exception = await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(gateway).GenerateAsync(Context(), observer));

        Assert.Equal("verification_timeout", exception.Category);
        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, exception.DurableStatus);
        Assert.Equal(new[] { VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.AmbiguousAfterDispatch }, observer.Checkpoints);
        Assert.Single(gateway.Requests);
        Assert.Equal(VerificationCallDispatchDisposition.PossiblyDispatched,
            Assert.Single(observer.TransportOutcomes).Disposition);
    }

    [Fact]
    public async Task Definitely_pre_dispatch_failure_is_checkpointed_and_may_retry_once()
    {
        var context = Context();
        var gateway = new QueueGateway(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch),
            Envelope(ValidJson(context)));
        var observer = new TrackingObserver();

        await Engine(gateway).GenerateAsync(context, observer);

        Assert.Equal(new[] { VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.FailedBeforeDispatch,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.ResponseReceived }, observer.Checkpoints);
        Assert.Equal(VerificationCallDispatchDisposition.DefinitelyNotDispatched,
            Assert.Single(observer.TransportOutcomes).Disposition);
    }

    [Fact]
    public async Task Missing_usage_is_persisted_as_unavailable_before_output_parsing_without_zero_coercion()
    {
        var context = Context();
        var envelope = Envelope(ValidJson(context)) with
        {
            InputTokens = null, CachedInputTokens = null, OutputTokens = null, ReasoningTokens = null,
            UsageAvailable = false
        };
        var observer = new TrackingObserver();

        var evaluation = await Engine(new QueueGateway(envelope)).GenerateAsync(context, observer);

        var response = Assert.Single(observer.Responses);
        Assert.False(response.UsageAvailable);
        Assert.Null(response.InputTokens);
        Assert.Null(response.OutputTokens);
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.False(call.ProviderUsageAvailable);
        Assert.Null(call.InputTokens);
        Assert.Null(call.EstimatedCostUsd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Partial_usage_preserves_valid_fields_and_only_prices_when_input_and_output_are_known(
        bool outputKnown)
    {
        var context = Context();
        var envelope = Envelope(ValidJson(context)) with
        {
            InputTokens = 100, CachedInputTokens = null, OutputTokens = outputKnown ? 25 : null,
            ReasoningTokens = null, UsageAvailable = false,
            UsageAvailability = VerificationUsageAvailability.Partial
        };
        var observer = new TrackingObserver();

        var evaluation = await Engine(new QueueGateway(envelope)).GenerateAsync(context, observer);

        var response = Assert.Single(observer.Responses);
        Assert.Equal(VerificationUsageAvailability.Partial, response.EffectiveUsageAvailability);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(outputKnown ? 25 : null, response.OutputTokens);
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.Equal(VerificationUsageAvailability.Partial, call.ProviderUsageAvailability);
        Assert.Equal(100, call.InputTokens);
        if (outputKnown) Assert.NotNull(call.EstimatedCostUsd);
        else Assert.Null(call.EstimatedCostUsd);
    }

    [Fact]
    public async Task Rejected_structured_output_still_records_normalized_response_telemetry_first()
    {
        var observer = new TrackingObserver();

        await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(new QueueGateway(Envelope("{ malformed"))).GenerateAsync(Context(), observer));

        var response = Assert.Single(observer.Responses);
        Assert.Equal("response-id", response.ProviderResponseId);
        Assert.True(response.UsageAvailable);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(200, response.HttpStatusCode);
    }

    [Fact]
    public void Slash_separated_prose_is_not_a_path_but_credible_unapproved_paths_are_rejected()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var prose = candidate with { Summary = "Save/Retry request/response pass/fail client/server date/time and/or behavior." };
        VerificationValidator.ValidateCandidate(context, prose, new VerificationLimits());

        var path = candidate with { Summary = "Inspect `src/Unapproved.cs` manually." };
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(context, path,
            new VerificationLimits()));
    }

    [Theory]
    [InlineData("Inspect `request/response` and \"application/json\" results.")]
    [InlineData("Compare `pass/fail`, client/server, read/write, and input/output behavior.")]
    [InlineData("Check application/problem+json and text/plain representations.")]
    [InlineData("Check `APPLICATION/JSON` and `Text/Plain` representations.")]
    [InlineData("Observe JSON Pointer /items/0/name without treating it as a local file.")]
    [InlineData("Observe JSON Pointer /items/schema.json and fragment #/items/schema.json.")]
    [InlineData("Observe escaped pointers /items/a~1b and /items/a~0b.")]
    [InlineData("Open https://example.test/api/items and the relative URL api/items?view=compact.")]
    [InlineData("Open https://example.test/docs/schema.json?view=compact#details and the relative URL docs/schema.json?view=compact.")]
    [InlineData("A relative documentation URL may look like docs/schema.json?view=compact.")]
    [InlineData("The browser route is docs/schema.json#example.")]
    [InlineData("Use the hyperlink reference `docs/schema.json?download=1` in documentation.")]
    [InlineData("Compare the XML namespace urn:example:items and C# token System.Collections.Generic.")]
    [InlineData("A Dockerfile is commonly used for container builds, while a Makefile is a general build convention.")]
    [InlineData("Inspect the exact approved path `SRC/App.CS` manually.")]
    [InlineData("Inspect the exact approved path `src/App.cs?view=compact` manually.")]
    public void Prose_data_syntax_urls_and_exact_approved_paths_are_accepted(string summary)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = summary };

        VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits());
    }

    [Theory]
    [InlineData("Inspect C:\\private\\file.txt.")]
    [InlineData("Inspect C:/private/file.txt.")]
    [InlineData("Inspect \\\\server\\share\\file.txt.")]
    [InlineData("Inspect /home/user/file.txt.")]
    [InlineData("Inspect /Users/name/file.txt.")]
    [InlineData("Inspect ../secret.txt.")]
    [InlineData("Inspect ..\\secret.txt.")]
    [InlineData("Inspect src\\..\\secret.txt.")]
    [InlineData("Inspect src/..\\secret.txt.")]
    [InlineData("Inspect %2e%2e%2fsecret.txt.")]
    [InlineData("Inspect %252e%252e%252fsecret.txt.")]
    [InlineData("Inspect `src/Unapproved.cs`.")]
    [InlineData("Inspect `src/Unapproved`.")]
    [InlineData("Inspect `config/private.json`.")]
    [InlineData("Inspect src/Unapproved.cs?view=compact.")]
    [InlineData("Modify config/secret.json#current.")]
    [InlineData("Open `Directory.Build.props?raw=1` from the repository.")]
    [InlineData("Examine src/Unapproved.cs?view=compact.")]
    [InlineData("Change config/secret.json#current.")]
    [InlineData("Patch package.json?raw=true.")]
    [InlineData("Load Directory.Build.props?raw=1.")]
    [InlineData("Verify contents of src/Unapproved.cs?view=compact.")]
    [InlineData("Consult app.config?raw=1 in the repository.")]
    [InlineData("Use the repository file Dockerfile?download=1.")]
    [InlineData("Manipulate src/Unapproved.cs#section.")]
    [InlineData("Inspect src/App.cs?next=../secret.txt.")]
    [InlineData("Inspect src/App.cs?next=%252e%252e%252fsecret.txt.")]
    [InlineData("Inspect Dockerfile.")]
    [InlineData("Inspect Makefile.")]
    [InlineData("Inspect .gitignore.")]
    [InlineData("The Dockerfile exists in the repository.")]
    [InlineData("Open Directory.Build.props.")]
    [InlineData("Inspect package.json.")]
    [InlineData("Inspect file:///private/file.txt.")]
    [InlineData("Inspect ..∕secret.txt.")]
    public void Absolute_traversal_and_credible_unapproved_paths_are_rejected(string summary)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = summary };

        Assert.Throws<VerificationException>(() =>
            VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits()));
    }

    private static VerificationPlanContext Context()
    {
        var task = VerificationWorkflowTests.ApprovedImplementation();
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        task.BeginVerificationPlanGeneration(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, revision.RevisionId, revision.ResultFingerprint!), DateTimeOffset.UtcNow);
        return VerificationWorkflowService.CreateContext(task, DateTimeOffset.UtcNow);
    }

    private static VerificationPlanContext Rebind(VerificationPlanContext context)
    {
        var unbound = context with { ContextFingerprint = string.Empty };
        return unbound with { ContextFingerprint = VerificationFingerprint.ComputeContext(unbound) };
    }

    private static OpenAIVerificationPlanEngine Engine(IOpenAIResponsesGateway gateway) => new(
        new ForgeAiOptions
        {
            Mode = ForgeAiModes.OpenAI,
            VerificationPlanningModel = "gpt-5.6-sol",
            VerificationPlanningReasoningEffort = "medium",
            VerificationPlanningMaxOutputTokens = 8_000,
            VerificationPlanningTimeoutSeconds = 30
        }, gateway, new ModelCostCalculator(ForgeAiOptions.DefaultPricing()), TimeProvider.System);

    private static OpenAIResponseEnvelope Envelope(string output) => new(
        "response-id", output, 100, 0, 50, 10, ProviderRequestId: "request-id",
        OutputItems: [new OpenAIResponseOutputItem(OpenAIResponseOutputItemKind.Message, "assistant",
            [new OpenAIResponseContent(OpenAIResponseContentKind.OutputText, output)])]);

    private sealed class CountingGateway : IOpenAIResponsesGateway
    {
        public int RequestCount { get; private set; }
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            throw new InvalidOperationException("Provider invocation was not expected.");
        }
    }

    private sealed class QueueGateway(params object[] outcomes) : IOpenAIResponsesGateway
    {
        private readonly Queue<object> queue = new(outcomes);
        public List<OpenAIResponseRequest> Requests { get; } = [];
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var outcome = queue.Dequeue();
            return outcome is Exception exception
                ? Task.FromException<OpenAIResponseEnvelope>(exception)
                : Task.FromResult((OpenAIResponseEnvelope)outcome);
        }
    }

    private sealed class TrackingObserver : IVerificationGenerationObserver
    {
        public List<VerificationDispatchCheckpoint> Checkpoints { get; } = [];
        public List<VerificationProviderResponseTelemetry> Responses { get; } = [];
        public List<(ModelCallRecord Call, VerificationCallDispatchDisposition Disposition)> TransportOutcomes { get; } = [];
        public Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid physicalCallId,
            CancellationToken cancellationToken = default)
        {
            Assert.NotEqual(Guid.Empty, physicalCallId);
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }

        public Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
            CancellationToken cancellationToken = default)
        {
            Responses.Add(response);
            Checkpoints.Add(VerificationDispatchCheckpoint.ResponseReceived);
            return Task.CompletedTask;
        }

        public Task RecordTransportFailureAsync(Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
            ModelCallRecord modelCall, VerificationCallDispatchDisposition disposition, string safeFailureMessage,
            CancellationToken cancellationToken = default)
        {
            TransportOutcomes.Add((modelCall, disposition));
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }
    }

    private static string ValidJson(VerificationPlanContext context) => JsonSerializer.Serialize(new
    {
        contextFingerprint = context.ContextFingerprint,
        summary = "Concise manual verification guidance.", scope = "Exact approved revision only.",
        preconditions = new[] { "Use the exact approved revision." },
        testCases = new[]
        {
            new
            {
                order = 1, title = "Manual behavior check", objective = "Observe the approved behavior.",
                category = "ManualBehavior", isRequired = true,
                preconditions = Array.Empty<string>(), testData = Array.Empty<string>(),
                orderedSteps = new[] { new { order = 1, instruction = "Inspect the approved behavior manually.", approvedValidationCommandId = "", expectedObservation = "The user observes the expected behavior." } },
                expectedResult = "The user reports the expected behavior.", negativeOrEdgeCases = Array.Empty<string>(),
                regressionScope = Array.Empty<string>(), evidenceRequirements = Array.Empty<string>(),
                safetyNotes = new[] { "Forge does not execute this check." }, originTestCaseId = "",
                regressionFailureReportIds = Array.Empty<string>()
            }
        },
        risks = Array.Empty<string>(), limitations = new[] { "Manual user report only." },
        evidenceGuidance = new[] { "Do not include secrets." }
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
