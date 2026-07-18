namespace Forge.Infrastructure;

public sealed class ModelCostCalculator(IReadOnlyDictionary<string, ModelPricing> pricing)
{
    public decimal Calculate(string model, int inputTokens, int cachedInputTokens, int outputTokens)
    {
        if (!pricing.TryGetValue(model, out var rates))
            throw new InvalidOperationException($"No pricing is configured for model '{model}'.");
        if (inputTokens < 0 || cachedInputTokens < 0 || outputTokens < 0 || cachedInputTokens > inputTokens)
            throw new ArgumentOutOfRangeException(nameof(inputTokens), "Token counts must be non-negative and cached input cannot exceed total input.");

        var uncachedInput = inputTokens - cachedInputTokens;
        return decimal.Round(
            ((uncachedInput * rates.InputPerMillionUsd) +
             (cachedInputTokens * rates.CachedInputPerMillionUsd) +
             (outputTokens * rates.OutputPerMillionUsd)) / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
    }
}
