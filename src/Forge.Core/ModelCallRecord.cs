namespace Forge.Core;

public enum ModelCallStage
{
    Clarification,
    Planning,
    Implementation
}

public sealed record ModelPricingSnapshot(
    decimal InputPerMillionUsd,
    decimal CachedInputPerMillionUsd,
    decimal OutputPerMillionUsd);

public sealed record ModelCallRecord(
    Guid Id,
    ModelCallStage Stage,
    string Provider,
    string Model,
    string ReasoningEffort,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    string? ProviderResponseId,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    decimal? EstimatedCostUsd,
    string? FailureCategory,
    ModelPricingSnapshot? PricingSnapshot = null,
    string? ProviderRequestId = null);

public static class ModelCallUsageEvidence
{
    public static bool IsAvailable(ModelCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.InputTokens is { } input && input >= 0 &&
               call.OutputTokens is { } output && output >= 0 &&
               (call.CachedInputTokens is null ||
                call.CachedInputTokens is >= 0 && call.CachedInputTokens <= input) &&
               (call.ReasoningTokens is null || call.ReasoningTokens is >= 0);
    }
}
