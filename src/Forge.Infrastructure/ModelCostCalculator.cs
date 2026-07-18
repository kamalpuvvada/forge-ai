using Forge.Core;

namespace Forge.Infrastructure;

public sealed record ModelCostBreakdown(
    int UncachedInputTokens,
    decimal UncachedInputCostUsd,
    decimal CachedInputCostUsd,
    decimal OutputCostUsd,
    decimal TotalCostUsd);

public sealed class ModelCostCalculator(IReadOnlyDictionary<string, ModelPricing> pricing)
{
    public bool TryGetPricingSnapshot(string model, out ModelPricingSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(model) && pricing.TryGetValue(model, out var rates))
        {
            snapshot = new ModelPricingSnapshot(
                rates.InputPerMillionUsd,
                rates.CachedInputPerMillionUsd,
                rates.OutputPerMillionUsd);
            return true;
        }

        snapshot = null!;
        return false;
    }

    public decimal Calculate(string model, int inputTokens, int cachedInputTokens, int outputTokens)
    {
        if (!TryGetPricingSnapshot(model, out var snapshot))
            throw new InvalidOperationException($"No pricing is configured for model '{model}'.");
        return Calculate(snapshot, inputTokens, cachedInputTokens, outputTokens).TotalCostUsd;
    }

    public ModelCostBreakdown Calculate(
        ModelPricingSnapshot rates,
        int inputTokens,
        int cachedInputTokens,
        int outputTokens)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (inputTokens < 0 || cachedInputTokens < 0 || outputTokens < 0 || cachedInputTokens > inputTokens)
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "Token counts must be non-negative and cached input cannot exceed total input.");
        if (rates.InputPerMillionUsd < 0 || rates.CachedInputPerMillionUsd < 0 || rates.OutputPerMillionUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(rates), "Pricing rates must be non-negative.");

        var uncachedInput = inputTokens - cachedInputTokens;
        var uncachedCost = Round(uncachedInput * rates.InputPerMillionUsd / 1_000_000m);
        var cachedCost = Round(cachedInputTokens * rates.CachedInputPerMillionUsd / 1_000_000m);
        var outputCost = Round(outputTokens * rates.OutputPerMillionUsd / 1_000_000m);
        return new ModelCostBreakdown(
            uncachedInput,
            uncachedCost,
            cachedCost,
            outputCost,
            Round(uncachedCost + cachedCost + outputCost));
    }

    private static decimal Round(decimal value) => decimal.Round(value, 8, MidpointRounding.AwayFromZero);
}
