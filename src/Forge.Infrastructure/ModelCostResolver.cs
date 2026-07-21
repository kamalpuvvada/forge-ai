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
    bool HasStoredPricingSnapshot,
    bool IsPartialEstimate = false)
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
    bool Overflowed,
    decimal? CompleteEstimatedSubtotalUsd = null,
    decimal? PartialEstimatedSubtotalUsd = null,
    decimal? AvailableEstimatedSubtotalUsd = null,
    bool HasPartialEstimates = false)
{
    public bool IsPartial => UnavailableCallCount > 0;
}

public sealed class ModelCostResolver(ModelCostCalculator calculator)
{
    public const decimal MaximumPersistedEstimatedCostUsd = 1_000_000_000m;

    public ModelCallCostResolution Resolve(ModelCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var verificationAvailability = call.Stage == ModelCallStage.VerificationPlanning
            ? call.ProviderUsageAvailability ?? VerificationUsage.Classify(call.InputTokens,
                call.CachedInputTokens, call.OutputTokens, call.ReasoningTokens)
            : (VerificationUsageAvailability?)null;
        if (!VerificationUsage.IsInternallyConsistent(call.InputTokens, call.CachedInputTokens,
                call.OutputTokens, call.ReasoningTokens) ||
            verificationAvailability == VerificationUsageAvailability.Unavailable ||
            call.InputTokens is null || call.OutputTokens is null)
            return Unavailable(call.PricingSnapshot is not null);
        var isPartialEstimate = call.CachedInputTokens is null;

        if (call.PricingSnapshot is not null)
        {
            if (TryCalculate(call.PricingSnapshot, call, out var storedBreakdown))
                return FromBreakdown(storedBreakdown, ModelCostProvenance.StoredPricingSnapshot, true,
                    isPartialEstimate);
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
                false,
                isPartialEstimate);
        }

        if (!string.IsNullOrWhiteSpace(call.Model) &&
            calculator.TryGetPricingSnapshot(call.Model, out var currentPricing) &&
            TryCalculate(currentPricing, call, out var currentBreakdown))
            return FromBreakdown(currentBreakdown, ModelCostProvenance.ReestimatedUsingCurrentPricing, false,
                isPartialEstimate);

        return Unavailable(false);
    }

    public ModelTaskCostResolution ResolveTotal(IEnumerable<ModelCallRecord> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        decimal total = 0;
        decimal completeSubtotal = 0;
        decimal partialSubtotal = 0;
        var available = 0;
        var unavailable = 0;
        var overflowed = false;
        var hasPartial = false;
        var completeAvailable = 0;
        var partialAvailable = 0;
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
                if (resolved.IsPartialEstimate)
                {
                    partialSubtotal = checked(partialSubtotal + cost);
                    hasPartial = true;
                    partialAvailable++;
                }
                else
                {
                    completeSubtotal = checked(completeSubtotal + cost);
                    completeAvailable++;
                }
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
            overflowed,
            overflowed || completeAvailable == 0 ? null : completeSubtotal,
            overflowed || partialAvailable == 0 ? null : partialSubtotal,
            overflowed || available == 0 && unavailable > 0 ? null : total,
            hasPartial);
    }

    private ModelCallCostResolution FromBreakdown(
        ModelCostBreakdown breakdown,
        ModelCostProvenance provenance,
        bool hasStoredSnapshot,
        bool isPartialEstimate) => new(
            breakdown.TotalCostUsd,
            provenance,
            breakdown.UncachedInputTokens,
            breakdown.UncachedInputCostUsd,
            breakdown.CachedInputCostUsd,
            breakdown.OutputCostUsd,
            hasStoredSnapshot,
            isPartialEstimate);

    private bool TryCalculate(ModelPricingSnapshot pricing, ModelCallRecord call, out ModelCostBreakdown breakdown)
    {
        breakdown = null!;
        if (call.InputTokens is not { } input || call.OutputTokens is not { } output)
            return false;
        var cached = call.CachedInputTokens ?? 0;
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
