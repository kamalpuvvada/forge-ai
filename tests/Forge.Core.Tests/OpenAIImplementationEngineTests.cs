using System.Text.Json;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class OpenAIImplementationEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Implementation_configuration_defaults_and_syntax_are_frozen()
    {
        var options = new ForgeAiOptions();

        Assert.Equal("gpt-5.6-sol", options.ImplementationModel);
        Assert.Equal("high", options.ImplementationReasoningEffort);
        Assert.Equal(32_000, options.ImplementationMaxOutputTokens);
        Assert.Equal(180, options.ImplementationTimeoutSeconds);
        options.ValidateSyntax();
        Assert.Throws<InvalidOperationException>(() => new ForgeAiOptions
        {
            ImplementationTimeoutSeconds = 0
        }.ValidateSyntax());
    }

    [Fact]
    public void Invalid_pricing_output_bounds_and_reasoning_fail_configuration_safely()
    {
        AssertInvalid(options => options.Pricing[options.ImplementationModel] = new(-1, .5m, 30));
        AssertInvalid(options => options.Pricing[options.ImplementationModel] =
            new(ForgeAiOptions.MaximumPricePerMillionUsd + 1, .5m, 30));
        AssertInvalid(options => options.ImplementationMaxOutputTokens = 0);
        AssertInvalid(options => options.ImplementationMaxOutputTokens =
            ForgeAiOptions.MaximumImplementationOutputTokens + 1);
        AssertInvalid(options => options.ImplementationReasoningEffort = "unsupported");

        static void AssertInvalid(Action<ForgeAiOptions> mutate)
        {
            var options = new ForgeAiOptions();
            mutate(options);
            var error = Assert.Throws<InvalidOperationException>(options.ValidateSyntax);
            Assert.Equal("Forge OpenAI implementation configuration is syntactically invalid.", error.Message);
        }
    }

    [Fact]
    public async Task Valid_mixed_output_uses_frozen_request_and_returns_accepted_telemetry()
    {
        var context = Context(PlannedFileAction.Create, PlannedFileAction.Modify, PlannedFileAction.Delete);
        var gateway = new QueueGateway(Envelope(Wire(context)));

        var result = await Engine(gateway).GenerateAsync(context);

        Assert.Equal(3, result.Output.Operations.Count);
        Assert.Equal(ImplementationSource.OpenAI, result.Output.Source);
        Assert.Equal("gpt-5.6-sol", result.Output.Model);
        Assert.Equal("high", result.Output.ReasoningEffort);
        var call = Assert.Single(result.ModelCalls);
        Assert.True(call.Succeeded);
        Assert.Equal(ModelCallStage.Implementation, call.Stage);
        Assert.Equal("provider-request", call.ProviderRequestId);
        Assert.NotNull(call.EstimatedCostUsd);

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("gpt-5.6-sol", request.Model);
        Assert.Equal("high", request.ReasoningEffort);
        Assert.Equal(32_000, request.MaxOutputTokens);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
        Assert.DoesNotContain("anyOf", request.JsonSchema, StringComparison.Ordinal);
        using var schema = JsonDocument.Parse(request.JsonSchema);
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["contextFingerprint", "summary", "warnings", "creates", "modifies", "deletes"], required);
        Assert.Contains("UNTRUSTED_REPOSITORY_AND_REQUIREMENT_DATA", request.UserInput, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", request.UserInput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gateway_serialization_has_no_tools_or_stateful_response_features()
    {
        var request = new OpenAIResponseRequest("gpt-5.6-sol", "high", 32_000, "instructions", "context", "{}",
            ClientRequestId: Guid.NewGuid().ToString("D"));
        using var json = JsonDocument.Parse(SdkOpenAIResponsesGateway.BuildRequestJson(request));
        var root = json.RootElement;

        Assert.Equal("gpt-5.6-sol", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(32_000, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.False(root.GetProperty("background").GetBoolean());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("disabled", root.GetProperty("truncation").GetString());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.False(root.TryGetProperty("metadata", out _));
        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public void Canonical_context_and_source_identities_are_stable_and_exclude_local_workspace_identity()
    {
        var first = Context(PlannedFileAction.Modify, PlannedFileAction.Create);
        var second = Context(PlannedFileAction.Modify, PlannedFileAction.Create);

        Assert.Equal(first.ContextFingerprint, second.ContextFingerprint);
        Assert.Equal(OpenAIImplementationEngine.BuildCanonicalContext(first),
            OpenAIImplementationEngine.BuildCanonicalContext(second));
        Assert.Equal(first.Files.Select(file => file.SourceContextIdentity),
            second.Files.Select(file => file.SourceContextIdentity));
        var canonical = OpenAIImplementationEngine.BuildCanonicalContext(first);
        Assert.DoesNotContain("worktree", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceToken", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gitCommonDirectory", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENAI_API_KEY", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_reasoning_item_and_one_assistant_output_text_are_accepted()
    {
        var context = Context(PlannedFileAction.Modify);
        var output = Wire(context);
        var items = new OpenAIResponseOutputItem[]
        {
            new(OpenAIResponseOutputItemKind.Reasoning, null, []),
            Message(output)
        };

        var result = await Engine(new QueueGateway(Envelope(output, items))).GenerateAsync(context);

        Assert.True(Assert.Single(result.ModelCalls).Succeeded);
    }

    [Fact]
    public async Task Missing_usage_is_accepted_with_unavailable_tokens_and_cost()
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(Envelope(Wire(context)) with { UsageAvailable = false });

        var call = Assert.Single((await Engine(gateway).GenerateAsync(context)).ModelCalls);

        Assert.True(call.Succeeded);
        Assert.Null(call.InputTokens);
        Assert.Null(call.EstimatedCostUsd);
    }

    [Fact]
    public async Task Cost_overflow_after_dispatch_preserves_successful_telemetry_with_unavailable_cost()
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(Envelope(Wire(context)) with
        {
            InputTokens = int.MaxValue,
            CachedInputTokens = 0,
            OutputTokens = int.MaxValue
        });
        var malformedPricing = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue)
        };

        var call = Assert.Single((await Engine(gateway, calculator: new ModelCostCalculator(malformedPricing))
            .GenerateAsync(context)).ModelCalls);

        Assert.Single(gateway.Requests);
        Assert.True(call.Succeeded);
        Assert.Equal(int.MaxValue, call.InputTokens);
        Assert.Equal("response-id", call.ProviderResponseId);
        Assert.Null(call.EstimatedCostUsd);
    }

    [Theory]
    [InlineData(OpenAIResponseStatus.Incomplete, OpenAIResponseIncompleteReason.MaxOutputTokens, "implementation_output_truncated")]
    [InlineData(OpenAIResponseStatus.Incomplete, OpenAIResponseIncompleteReason.ContentFilter, "implementation_content_filter")]
    [InlineData(OpenAIResponseStatus.Failed, null, "implementation_incomplete_response")]
    public async Task Incomplete_provider_outcomes_are_rejected_without_retry(
        OpenAIResponseStatus status,
        OpenAIResponseIncompleteReason? reason,
        string category)
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(Envelope(Wire(context)) with { Status = status, IncompleteReason = reason });

        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() => Engine(gateway).GenerateAsync(context));

        Assert.Equal(category, failure.Category);
        Assert.Single(gateway.Requests);
        Assert.Equal(category, Assert.Single(failure.ModelCalls).FailureCategory);
    }

    [Theory]
    [InlineData(OpenAIResponseOutputItemKind.Tool)]
    [InlineData(OpenAIResponseOutputItemKind.Unknown)]
    public async Task Tool_and_unknown_output_items_are_rejected(OpenAIResponseOutputItemKind kind)
    {
        var context = Context(PlannedFileAction.Modify);
        var json = Wire(context);
        var gateway = new QueueGateway(Envelope(json,
            [new(kind, null, []), Message(json)]));

        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() => Engine(gateway).GenerateAsync(context));

        Assert.Equal("implementation_unexpected_output", failure.Category);
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task Refusal_is_rejected_without_retry()
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(Envelope("", [new(OpenAIResponseOutputItemKind.Message, "assistant",
            [new(OpenAIResponseContentKind.Refusal, "no")])]));

        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() => Engine(gateway).GenerateAsync(context));

        Assert.Equal("implementation_refusal", failure.Category);
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task Multiple_messages_and_multiple_text_parts_are_rejected()
    {
        var context = Context(PlannedFileAction.Modify);
        var json = Wire(context);
        var multipleMessages = new QueueGateway(Envelope(json, [Message(json), Message(json)]));
        var messagesFailure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(multipleMessages).GenerateAsync(context));
        Assert.Equal("implementation_unexpected_output", messagesFailure.Category);

        var multipleText = new QueueGateway(Envelope(json, [new(OpenAIResponseOutputItemKind.Message, "assistant",
            [new(OpenAIResponseContentKind.OutputText, json), new(OpenAIResponseContentKind.OutputText, json)])]));
        var textFailure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(multipleText).GenerateAsync(context));
        Assert.Equal("implementation_unexpected_output", textFailure.Category);
    }

    [Fact]
    public async Task Empty_and_oversized_structured_output_are_rejected()
    {
        var context = Context(PlannedFileAction.Modify);
        var empty = new QueueGateway(Envelope("", [Message("")]));
        var emptyFailure = await Assert.ThrowsAsync<ImplementationProviderException>(() => Engine(empty).GenerateAsync(context));
        Assert.Equal("implementation_empty_response", emptyFailure.Category);

        var oversizedText = "{" + new string(' ', OpenAIImplementationEngine.MaximumRawResponseBytes + 1);
        var oversized = new QueueGateway(Envelope(oversizedText));
        var oversizedFailure = await Assert.ThrowsAsync<ImplementationProviderException>(() => Engine(oversized).GenerateAsync(context));
        Assert.Equal("implementation_invalid_structured_output", oversizedFailure.Category);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"unknown\":true}")]
    public async Task Malformed_or_schema_mismatched_json_is_rejected(string output)
    {
        var context = Context(PlannedFileAction.Modify);
        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(new QueueGateway(Envelope(output))).GenerateAsync(context));

        Assert.Equal("implementation_invalid_structured_output", failure.Category);
    }

    [Fact]
    public async Task Missing_blank_oversized_and_unsafe_response_ids_are_rejected()
    {
        var context = Context(PlannedFileAction.Modify);
        foreach (var id in new[] { "", " ", new string('r', OpenAIProviderIdentifier.MaximumLength + 1), "C:.secret" })
        {
            var gateway = new QueueGateway(Envelope(Wire(context)) with { ResponseId = id });
            var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
                Engine(gateway).GenerateAsync(context));
            Assert.Equal("implementation_incomplete_response", failure.Category);
            Assert.Null(Assert.Single(failure.ModelCalls).ProviderResponseId);
        }
    }

    [Fact]
    public async Task Duplicate_root_and_nested_properties_are_rejected_but_names_in_distinct_objects_are_valid()
    {
        var context = Context(PlannedFileAction.Modify);
        var valid = Wire(context);
        var duplicateRoot = valid.Replace("\"summary\":", "\"summary\":\"first\",\"summary\":",
            StringComparison.Ordinal);
        var rootFailure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(new QueueGateway(Envelope(duplicateRoot))).GenerateAsync(context));
        Assert.Equal("implementation_invalid_structured_output", rootFailure.Category);

        var duplicateNested = valid.Replace("\"path\":", "\"path\":\"src/Duplicate.cs\",\"path\":",
            StringComparison.Ordinal);
        var nestedFailure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(new QueueGateway(Envelope(duplicateNested))).GenerateAsync(context));
        Assert.Equal("implementation_invalid_structured_output", nestedFailure.Category);

        var twoObjects = Context(PlannedFileAction.Modify, PlannedFileAction.Modify);
        var accepted = await Engine(new QueueGateway(Envelope(Wire(twoObjects)))).GenerateAsync(twoObjects);
        Assert.Equal(2, accepted.Output.Operations.Count);
    }

    [Fact]
    public async Task Wrong_fingerprint_and_source_identity_are_validation_rejections()
    {
        var context = Context(PlannedFileAction.Modify);
        var wrongFingerprint = Wire(context).Replace(context.ContextFingerprint, new string('f', 64), StringComparison.Ordinal);
        var first = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(new QueueGateway(Envelope(wrongFingerprint))).GenerateAsync(context));
        Assert.Equal("implementation_validation_rejected", first.Category);

        var identity = Assert.Single(context.Files).SourceContextIdentity;
        var wrongIdentity = Wire(context).Replace(identity, new string('e', 64), StringComparison.Ordinal);
        var second = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(new QueueGateway(Envelope(wrongIdentity))).GenerateAsync(context));
        Assert.Equal("implementation_validation_rejected", second.Category);
    }

    [Theory]
    [InlineData("rate_limit", 429, "implementation_rate_limit")]
    [InlineData("provider_error", 502, "implementation_provider_error")]
    [InlineData("provider_error", 503, "implementation_provider_error")]
    public async Task Permitted_transient_failure_records_two_calls_and_preserves_request_except_for_client_id(
        string transportCategory,
        int? statusCode,
        string expectedCategory)
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(
            new OpenAITransportException(transportCategory, "safe", statusCode: statusCode,
                dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived,
                retryAfter: TimeSpan.Zero),
            Envelope(Wire(context)));

        var evaluation = await Engine(gateway).GenerateAsync(context);

        Assert.Equal(2, gateway.Requests.Count);
        Assert.NotEqual(gateway.Requests[0].ClientRequestId, gateway.Requests[1].ClientRequestId);
        Assert.Equal(gateway.Requests[0] with { ClientRequestId = null }, gateway.Requests[1] with { ClientRequestId = null });
        Assert.Equal(2, evaluation.ModelCalls.Count);
        Assert.False(evaluation.ModelCalls[0].Succeeded);
        Assert.Equal(expectedCategory, evaluation.ModelCalls[0].FailureCategory);
        Assert.True(evaluation.ModelCalls[1].Succeeded);
    }

    [Fact]
    public async Task Definite_pre_dispatch_dns_or_connect_failure_retries_once_with_truthful_calls()
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(
            new OpenAITransportException("provider_error", "safe",
                dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch),
            Envelope(Wire(context)));

        var evaluation = await Engine(gateway).GenerateAsync(context);

        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(2, evaluation.ModelCalls.Count);
        Assert.False(evaluation.ModelCalls[0].Succeeded);
        Assert.Equal("implementation_provider_error", evaluation.ModelCalls[0].FailureCategory);
        Assert.Null(evaluation.ModelCalls[0].ProviderResponseId);
        Assert.True(evaluation.ModelCalls[1].Succeeded);
    }

    [Theory]
    [InlineData(OpenAITransportDispatchCertainty.DispatchMayHaveOccurred)]
    [InlineData(OpenAITransportDispatchCertainty.ResponseReceived)]
    public async Task Ambiguous_reset_or_response_interruption_never_retries(
        OpenAITransportDispatchCertainty certainty)
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(new OpenAITransportException("provider_error", "safe",
            new HttpRequestException("ambiguous"), dispatchCertainty: certainty));

        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(gateway).GenerateAsync(context));

        Assert.Single(gateway.Requests);
        var call = Assert.Single(failure.ModelCalls);
        Assert.False(call.Succeeded);
        Assert.Equal("implementation_provider_error", call.FailureCategory);
    }

    [Theory]
    [InlineData("invalid_request", 400, "implementation_invalid_request")]
    [InlineData("invalid_request", 422, "implementation_invalid_request")]
    [InlineData("authentication", 401, "implementation_authentication")]
    [InlineData("permission", 403, "implementation_permission")]
    [InlineData("model_unavailable", 404, "implementation_model_unavailable")]
    [InlineData("provider_error", 500, "implementation_provider_error")]
    [InlineData("timeout", 504, "implementation_timeout")]
    public async Task Non_retryable_transport_failures_make_one_call(string transport, int status, string category)
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new QueueGateway(new OpenAITransportException(transport, "safe", statusCode: status));

        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() => Engine(gateway).GenerateAsync(context));

        Assert.Equal(category, failure.Category);
        Assert.Single(gateway.Requests);
        Assert.Single(failure.ModelCalls);
    }

    [Fact]
    public async Task Sensitive_or_oversized_mandatory_context_fails_before_dispatch()
    {
        var sensitive = Context(PlannedFileAction.Modify) with { ApprovedRequirementSummary = "api_key=Abcdefghijk123456789" };
        sensitive = sensitive with { ContextFingerprint = ImplementationContextIdentity.ComputeGlobal(sensitive) };
        var sensitiveGateway = new QueueGateway();
        var sensitiveFailure = await Assert.ThrowsAsync<ImplementationException>(() =>
            Engine(sensitiveGateway).GenerateAsync(sensitive));
        Assert.Equal("implementation_sensitive_context", sensitiveFailure.Category);
        Assert.Empty(sensitiveGateway.Requests);

        var oversized = Context(PlannedFileAction.Modify);
        oversized = oversized with { ApprovedRequirementSummary = new string('x', OpenAIImplementationEngine.MaximumRequirementBytes + 1) };
        oversized = oversized with { ContextFingerprint = ImplementationContextIdentity.ComputeGlobal(oversized) };
        var limitGateway = new QueueGateway();
        var limitFailure = await Assert.ThrowsAsync<ImplementationException>(() => Engine(limitGateway).GenerateAsync(oversized));
        Assert.Equal("implementation_context_limit", limitFailure.Category);
        Assert.Empty(limitGateway.Requests);
    }

    [Fact]
    public async Task Logical_timeout_records_one_failed_physical_call_and_does_not_retry()
    {
        var context = Context(PlannedFileAction.Modify);
        var gateway = new NeverCompletingGateway();

        var failure = await Assert.ThrowsAsync<ImplementationProviderException>(() =>
            Engine(gateway, timeoutSeconds: 1).GenerateAsync(context));

        Assert.Equal("implementation_timeout", failure.Category);
        Assert.Equal(1, gateway.RequestCount);
        Assert.Equal("implementation_timeout", Assert.Single(failure.ModelCalls).FailureCategory);
    }

    private static OpenAIImplementationEngine Engine(
        IOpenAIResponsesGateway gateway,
        int timeoutSeconds = 180,
        ModelCostCalculator? calculator = null) => new(
        new ForgeAiOptions
        {
            Mode = ForgeAiModes.OpenAI,
            ImplementationModel = "gpt-5.6-sol",
            ImplementationReasoningEffort = "high",
            ImplementationMaxOutputTokens = 32_000,
            ImplementationTimeoutSeconds = timeoutSeconds
        }, gateway, calculator ?? new ModelCostCalculator(ForgeAiOptions.DefaultPricing()), new FixedTimeProvider());

    private static ImplementationContext Context(params PlannedFileAction[] actions)
    {
        var affected = actions.Select((action, index) => new PlannedFileChange(Path(action, index), action,
            "Implement the approved change.", [], .9m)).ToArray();
        var plan = new ImplementationPlan("Implement", "Implement approved files.", "Approved files are bounded.", affected,
            [new ImplementationStep(1, "Apply the approved operations.", affected.Select(file => file.Path).ToArray(), [], "Files change.")],
            [], [], [], [], [new RequirementCoverageItem("Approved change", affected.Select(file => file.Path).ToArray(), [1])],
            "Implement bounded changes.", PlanningSource.DeterministicFake, null, Now, new string('a', 64));
        var planFingerprint = ImplementationReviewFingerprint.ComputePlan(plan);
        var baseSha = new string('b', 40);
        var files = affected.Select(file =>
        {
            var original = file.Action == PlannedFileAction.Create ? null : $"original {file.Path}\n";
            var hash = original is null ? null : ImplementationOutputValidator.Hash(original);
            var bytes = original is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(original);
            return new ImplementationFileContext(file.Path, file.Action, original, hash, bytes,
                ImplementationContextIdentity.ComputeSource(baseSha, planFingerprint, file.Path, file.Action, hash, bytes));
        }).ToArray();
        var context = new ImplementationContext("Implement the approved bounded change.", plan, files, Now,
            planFingerprint, baseSha, [], [], 0);
        return context with { ContextFingerprint = ImplementationContextIdentity.ComputeGlobal(context) };
    }

    private static string Path(PlannedFileAction action, int index) => action switch
    {
        PlannedFileAction.Create => $"docs/new-{index}.md",
        PlannedFileAction.Modify => $"src/App{index}.cs",
        PlannedFileAction.Delete => $"docs/old-{index}.md",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string Wire(ImplementationContext context)
    {
        var creates = context.Files.Where(file => file.PlannedAction == PlannedFileAction.Create).Select(file => new
        {
            path = file.Path, sourceContextIdentity = file.SourceContextIdentity,
            content = $"# Created {file.Path}\n", rationale = "Create the approved file."
        });
        var modifies = context.Files.Where(file => file.PlannedAction == PlannedFileAction.Modify).Select(file => new
        {
            path = file.Path, expectedOriginalSha256 = file.OriginalContentSha256,
            expectedOriginalUtf8Bytes = file.OriginalUtf8Bytes, sourceContextIdentity = file.SourceContextIdentity,
            content = file.OriginalContent + "// changed\n", rationale = "Modify the approved file."
        });
        var deletes = context.Files.Where(file => file.PlannedAction == PlannedFileAction.Delete).Select(file => new
        {
            path = file.Path, expectedOriginalSha256 = file.OriginalContentSha256,
            expectedOriginalUtf8Bytes = file.OriginalUtf8Bytes, sourceContextIdentity = file.SourceContextIdentity,
            rationale = "Delete the approved file."
        });
        return JsonSerializer.Serialize(new
        {
            contextFingerprint = context.ContextFingerprint,
            summary = "Implemented the approved operations.", warnings = Array.Empty<string>(), creates, modifies, deletes
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static OpenAIResponseOutputItem Message(string output) => new(OpenAIResponseOutputItemKind.Message,
        "assistant", [new(OpenAIResponseContentKind.OutputText, output)]);

    private static OpenAIResponseEnvelope Envelope(
        string output,
        IReadOnlyList<OpenAIResponseOutputItem>? items = null) =>
        new("response-id", output, 100, 20, 50, 10, ProviderRequestId: "provider-request",
            OutputItems: items ?? [Message(output)]);

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

    private sealed class NeverCompletingGateway : IOpenAIResponsesGateway
    {
        public int RequestCount { get; private set; }
        public async Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(Interlocked.Increment(ref ticks));
    }
}
