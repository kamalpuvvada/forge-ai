using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class OpenAIClarificationEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_ask_structured_response_maps_usage_and_estimated_cost()
    {
        var gateway = new StubGateway(new OpenAIResponseEnvelope(
            "resp_123",
            """{"decision":"ask","question":"Who can access it?","summary":null,"knownFacts":["Audit log requested"],"assumptions":[],"unresolvedGaps":["Access"]}""",
            1000, 400, 200, 50));
        var engine = CreateEngine(gateway);

        var result = await engine.EvaluateAsync(NewTask());

        Assert.Equal(ClarificationDecision.Ask, result.Decision);
        Assert.Equal("Who can access it?", result.Question);
        Assert.NotNull(result.ModelCall);
        Assert.Equal(1000, result.ModelCall.InputTokens);
        Assert.Equal(400, result.ModelCall.CachedInputTokens);
        Assert.Equal(0.0046m, result.ModelCall.EstimatedCostUsd);
    }

    [Fact]
    public async Task Valid_summarize_structured_response_is_accepted()
    {
        var engine = CreateEngine(new StubGateway(Envelope(
            """{"decision":"summarize","question":null,"summary":"Implement administrator audit logging.","knownFacts":[],"assumptions":[],"unresolvedGaps":[]}""")));
        var result = await engine.EvaluateAsync(NewTask());
        Assert.Equal(ClarificationDecision.Summarize, result.Decision);
        Assert.Equal("Implement administrator audit logging.", result.Summary);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"decision\":\"ask\",\"question\":\"Q?\",\"summary\":\"also summary\",\"knownFacts\":[],\"assumptions\":[],\"unresolvedGaps\":[]}")]
    [InlineData("{\"decision\":\"summarize\",\"question\":null,\"summary\":null,\"knownFacts\":[],\"assumptions\":[],\"unresolvedGaps\":[]}")]
    public async Task Malformed_or_invalid_decision_is_rejected(string output)
    {
        var exception = await Assert.ThrowsAsync<ClarificationProviderException>(() =>
            CreateEngine(new StubGateway(Envelope(output))).EvaluateAsync(NewTask()));
        Assert.Equal("invalid_response", exception.Category);
        Assert.False(exception.FailedCall.Succeeded);
    }

    [Fact]
    public async Task Missing_api_key_configuration_is_rejected_before_a_call()
    {
        var engine = CreateEngine(null);
        await Assert.ThrowsAsync<ClarificationConfigurationException>(() => engine.EvaluateAsync(NewTask()));
    }

    [Fact]
    public async Task Provider_error_is_mapped_without_persisting_secret_text()
    {
        const string secret = "sensitive-test-value";
        var gateway = new ThrowingGateway(new OpenAITransportException("rate_limit", "OpenAI rate-limited the clarification request.", new Exception(secret)));

        var exception = await Assert.ThrowsAsync<ClarificationProviderException>(() =>
            CreateEngine(gateway).EvaluateAsync(NewTask()));

        Assert.Equal("rate_limit", exception.FailedCall.FailureCategory);
        Assert.DoesNotContain(secret, exception.FailedCall.ToString());
    }

    [Fact]
    public void Estimated_cost_uses_uncached_cached_and_output_rates()
    {
        var calculator = new ModelCostCalculator(ForgeAiOptions.DefaultPricing());
        Assert.Equal(0.01775m, calculator.Calculate("gpt-5.6-terra", 2000, 1000, 1000));
    }

    private static OpenAIClarificationEngine CreateEngine(IOpenAIResponsesGateway? gateway)
    {
        var options = new ForgeAiOptions { Mode = ForgeAiModes.OpenAI };
        return new OpenAIClarificationEngine(options, gateway, new ModelCostCalculator(options.Pricing), TimeProvider.System);
    }

    private static EngineeringTask NewTask() => EngineeringTask.Create("C:/repo", "Add audit logging", Now);
    private static OpenAIResponseEnvelope Envelope(string output) => new("resp_test", output, 10, 0, 5, 1);

    private sealed class StubGateway(OpenAIResponseEnvelope envelope) : IOpenAIResponsesGateway
    {
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(envelope);
    }

    private sealed class ThrowingGateway(Exception exception) : IOpenAIResponsesGateway
    {
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<OpenAIResponseEnvelope>(exception);
    }
}
