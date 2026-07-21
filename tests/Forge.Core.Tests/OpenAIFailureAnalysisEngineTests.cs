using System.Text.Json;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class OpenAIFailureAnalysisEngineTests
{
    [Fact]
    public async Task Strict_structured_analysis_is_accepted_with_truthful_dispatch_telemetry()
    {
        var context = await Context();
        var output = Output(context);
        var gateway = new QueueGateway(Envelope(output));
        var observer = new TrackingObserver();

        var evaluation = await Engine(gateway).GenerateAsync(context, observer);

        Assert.Equal(FailureClassification.ImplementationDefect, evaluation.Candidate.Classification);
        Assert.Single(evaluation.ModelCalls);
        Assert.Equal(ModelCallStage.FailureAnalysis, evaluation.ModelCalls[0].Stage);
        Assert.Equal(VerificationCallDispatchDisposition.ResponseReceived,
            evaluation.ModelCalls[0].VerificationDispatchDisposition);
        Assert.Equal([VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.ResponseReceived], observer.Checkpoints);
        Assert.Single(observer.Responses);
        Assert.Equal(VerificationProviderResponseStatus.Completed, observer.Responses[0].Status);
        Assert.Single(gateway.Requests);
        using var schema = JsonDocument.Parse(gateway.Requests[0].JsonSchema);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Duplicate_properties_are_rejected_after_one_truthfully_recorded_response()
    {
        var context = await Context();
        var valid = Output(context);
        var duplicate = valid.Replace("\"rootCauseSummary\":\"Bounded defect\"",
            "\"rootCauseSummary\":\"first\",\"rootCauseSummary\":\"second\"", StringComparison.Ordinal);
        var gateway = new QueueGateway(Envelope(duplicate));
        var observer = new TrackingObserver();

        var failure = await Assert.ThrowsAsync<FailureAnalysisProviderException>(() =>
            Engine(gateway).GenerateAsync(context, observer));

        Assert.Equal("failure_analysis_invalid_structured_output", failure.Category);
        Assert.Single(gateway.Requests);
        Assert.Contains(VerificationDispatchCheckpoint.ResponseReceived, observer.Checkpoints);
    }

    [Theory]
    [InlineData(OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch, VerificationCallDispatchDisposition.DefinitelyNotDispatched)]
    [InlineData(OpenAITransportDispatchCertainty.DispatchMayHaveOccurred, VerificationCallDispatchDisposition.PossiblyDispatched)]
    public async Task Transport_certainty_is_persisted_and_never_automatically_retried(
        OpenAITransportDispatchCertainty certainty, VerificationCallDispatchDisposition expected)
    {
        var context = await Context();
        var gateway = new QueueGateway(new OpenAITransportException("provider_error", "Safe failure.",
            dispatchCertainty: certainty));
        var observer = new TrackingObserver();

        var failure = await Assert.ThrowsAsync<FailureAnalysisProviderException>(() =>
            Engine(gateway).GenerateAsync(context, observer));

        Assert.Single(gateway.Requests);
        Assert.Single(observer.TransportOutcomes);
        Assert.Equal(expected, observer.TransportOutcomes[0].Disposition);
        Assert.Equal(expected == VerificationCallDispatchDisposition.DefinitelyNotDispatched
            ? FailureAnalysisStatus.FailedBeforeDispatch : FailureAnalysisStatus.AmbiguousAfterDispatch,
            failure.DurableStatus);
    }

    private static async Task<FailureAnalysisContext> Context()
    {
        var task = await CorrectionWorkflowTests.FailedTask();
        task.BeginFailureAnalysis(CorrectionWorkflowTests.AnalysisCommand(task), DateTimeOffset.UtcNow);
        return CorrectionWorkflowService.CreateAnalysisContext(task, DateTimeOffset.UtcNow);
    }

    private static string Output(FailureAnalysisContext context) => JsonSerializer.Serialize(new
    {
        contextFingerprint = context.ContextFingerprint,
        classification = "ImplementationDefect",
        confidencePercent = 70,
        rootCauseSummary = "Bounded defect",
        rationale = "The exact failed result references the approved operation.",
        evidenceReferences = context.FailedResultRevisionIds.Select(item => item.ToString("D")),
        affectedApprovedOperations = context.ApprovedOperations.Take(1).Select(item => new
        { item.Path, action = item.Action.ToString() }),
        correctionStrategy = "Adjust only the exact approved operation.",
        expectedBehavior = "The recorded expectation is satisfied.",
        verificationImpact = "Repeat the failed case as required regression coverage.",
        risks = new[] { "Human verification remains required." }
    });

    private static OpenAIFailureAnalysisEngine Engine(IOpenAIResponsesGateway gateway) => new(
        new ForgeAiOptions { Mode = ForgeAiModes.OpenAI, FailureAnalysisModel = "gpt-5.6-sol",
            FailureAnalysisReasoningEffort = "medium", FailureAnalysisMaxOutputTokens = 6000,
            FailureAnalysisTimeoutSeconds = 30 }, gateway,
        new ModelCostCalculator(ForgeAiOptions.DefaultPricing()), TimeProvider.System, new CorrectionLimits());

    private static OpenAIResponseEnvelope Envelope(string output) => new(
        "response-id", output, 100, 10, 50, 5, ProviderRequestId: "request-id",
        OutputItems: [new OpenAIResponseOutputItem(OpenAIResponseOutputItemKind.Message, "assistant",
            [new OpenAIResponseContent(OpenAIResponseContentKind.OutputText, output)])]);

    private sealed class QueueGateway(params object[] outcomes) : IOpenAIResponsesGateway
    {
        private readonly Queue<object> outcomes = new(outcomes);
        public List<OpenAIResponseRequest> Requests { get; } = [];
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var value = outcomes.Dequeue();
            return value is Exception exception ? Task.FromException<OpenAIResponseEnvelope>(exception) :
                Task.FromResult((OpenAIResponseEnvelope)value);
        }
    }

    private sealed class TrackingObserver : IVerificationGenerationObserver
    {
        public List<VerificationDispatchCheckpoint> Checkpoints { get; } = [];
        public List<VerificationProviderResponseTelemetry> Responses { get; } = [];
        public List<(ModelCallRecord Call, VerificationCallDispatchDisposition Disposition)> TransportOutcomes { get; } = [];
        public Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
            CancellationToken cancellationToken = default)
        { Checkpoints.Add(checkpoint); return Task.CompletedTask; }
        public Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
            CancellationToken cancellationToken = default)
        { Responses.Add(response); Checkpoints.Add(VerificationDispatchCheckpoint.ResponseReceived); return Task.CompletedTask; }
        public Task RecordCallAsync(Guid logicalCallId, ModelCallRecord modelCall,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordTransportFailureAsync(Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
            ModelCallRecord modelCall, VerificationCallDispatchDisposition disposition, string safeFailureMessage,
            CancellationToken cancellationToken = default)
        { TransportOutcomes.Add((modelCall, disposition)); Checkpoints.Add(checkpoint); return Task.CompletedTask; }
    }
}
