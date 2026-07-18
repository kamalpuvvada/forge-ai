namespace Forge.Core;

public enum ModelCallStage
{
    Clarification,
    Planning
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
    ModelPricingSnapshot? PricingSnapshot = null);
