using Forge.Core;

namespace Forge.Infrastructure;

public enum ModelCostProvenance
{
    StoredPricingSnapshot,
    LegacyEstimatePricingSnapshotUnavailable,
    ReestimatedUsingCurrentPricing,
    CostUnavailable
}

public sealed record ModelCallCostResolution(
    decimal? EstimatedCostUsd,
    ModelCostProvenance Provenance,
    int? UncachedInputTokens,
    decimal? UncachedInputCostUsd,
    decimal? CachedInputCostUsd,
    decimal? OutputCostUsd,
    bool HasStoredPricingSnapshot)
{
    public string ProvenanceLabel => Provenance switch
    {
        ModelCostProvenance.StoredPricingSnapshot => "stored pricing snapshot",
        ModelCostProvenance.LegacyEstimatePricingSnapshotUnavailable => "legacy estimate \u2014 pricing snapshot unavailable",
        ModelCostProvenance.ReestimatedUsingCurrentPricing => "re-estimated using current pricing",
        _ => "cost unavailable"
    };
}

public sealed record ModelTaskCostResolution(
    decimal? TotalEstimatedCostUsd,
    int AvailableCallCount,
    int UnavailableCallCount,
    bool Overflowed)
{
    public bool IsPartial => UnavailableCallCount > 0;
}

public sealed class ModelCostResolver(ModelCostCalculator calculator)
{
    public const decimal MaximumPersistedEstimatedCostUsd = 1_000_000_000m;

    public ModelCallCostResolution Resolve(ModelCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!ModelCallUsageEvidence.IsAvailable(call))
            return Unavailable(call.PricingSnapshot is not null);

        if (call.PricingSnapshot is not null)
        {
            if (TryCalculate(call.PricingSnapshot, call, out var storedBreakdown))
                return FromBreakdown(storedBreakdown, ModelCostProvenance.StoredPricingSnapshot, true);
            return Unavailable(true);
        }

        if (call.EstimatedCostUsd is { } estimate)
        {
            if (!IsValidPersistedEstimate(estimate)) return Unavailable(false);
            return new ModelCallCostResolution(
                estimate,
                ModelCostProvenance.LegacyEstimatePricingSnapshotUnavailable,
                ValidUncachedTokens(call),
                null,
                null,
                null,
                false);
        }

        if (!string.IsNullOrWhiteSpace(call.Model) &&
            calculator.TryGetPricingSnapshot(call.Model, out var currentPricing) &&
            TryCalculate(currentPricing, call, out var currentBreakdown))
            return FromBreakdown(currentBreakdown, ModelCostProvenance.ReestimatedUsingCurrentPricing, false);

        return Unavailable(false);
    }

    public ModelTaskCostResolution ResolveTotal(IEnumerable<ModelCallRecord> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        decimal total = 0;
        var available = 0;
        var unavailable = 0;
        var overflowed = false;
        foreach (var call in calls)
        {
            var resolved = Resolve(call);
            if (resolved.EstimatedCostUsd is not { } cost || cost < 0)
            {
                unavailable++;
                continue;
            }
            try
            {
                total = checked(total + cost);
                available++;
            }
            catch (OverflowException)
            {
                unavailable++;
                overflowed = true;
            }
        }
        return new ModelTaskCostResolution(
            overflowed || available == 0 && unavailable > 0 ? null : total,
            available,
            unavailable,
            overflowed);
    }

    private ModelCallCostResolution FromBreakdown(
        ModelCostBreakdown breakdown,
        ModelCostProvenance provenance,
        bool hasStoredSnapshot) => new(
            breakdown.TotalCostUsd,
            provenance,
            breakdown.UncachedInputTokens,
            breakdown.UncachedInputCostUsd,
            breakdown.CachedInputCostUsd,
            breakdown.OutputCostUsd,
            hasStoredSnapshot);

    private bool TryCalculate(ModelPricingSnapshot pricing, ModelCallRecord call, out ModelCostBreakdown breakdown)
    {
        breakdown = null!;
        if (call.InputTokens is not { } input || call.CachedInputTokens is not { } cached || call.OutputTokens is not { } output)
            return false;
        return calculator.TryCalculate(pricing, input, cached, output, out breakdown);
    }

    private static int? ValidUncachedTokens(ModelCallRecord call) =>
        call.InputTokens is { } input && call.CachedInputTokens is { } cached && input >= 0 && cached >= 0 && cached <= input
            ? input - cached
            : null;

    private static bool IsValidPersistedEstimate(decimal estimate) =>
        estimate >= 0 && estimate <= MaximumPersistedEstimatedCostUsd;

    private static ModelCallCostResolution Unavailable(bool hasStoredSnapshot) => new(
        null,
        ModelCostProvenance.CostUnavailable,
        null,
        null,
        null,
        null,
        hasStoredSnapshot);
}
