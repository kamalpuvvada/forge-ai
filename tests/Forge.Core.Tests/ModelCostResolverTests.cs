using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class ModelCostResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModelPricingSnapshot Snapshot = new(10m, 2m, 20m);

    [Fact]
    public void Breakdown_charges_uncached_cached_and_output_without_reasoning_surcharge()
    {
        var calculator = Calculator();

        var breakdown = calculator.Calculate(Snapshot, 1_000, 250, 500);
        var call = Call(1_000, 250, 500, null, Snapshot) with { ReasoningTokens = 400 };
        var resolved = Resolver().Resolve(call);

        Assert.Equal(750, breakdown.UncachedInputTokens);
        Assert.Equal(0.0075m, breakdown.UncachedInputCostUsd);
        Assert.Equal(0.0005m, breakdown.CachedInputCostUsd);
        Assert.Equal(0.01m, breakdown.OutputCostUsd);
        Assert.Equal(0.018m, breakdown.TotalCostUsd);
        Assert.Equal(breakdown.TotalCostUsd, resolved.EstimatedCostUsd);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(10, -1, 0)]
    [InlineData(10, 11, 0)]
    [InlineData(10, 0, -1)]
    public void Invalid_token_usage_is_cost_unavailable(int input, int cached, int output)
    {
        var resolved = Resolver().Resolve(Call(input, cached, output, null, Snapshot));

        Assert.Equal(ModelCostProvenance.CostUnavailable, resolved.Provenance);
        Assert.Null(resolved.EstimatedCostUsd);
    }

    [Fact]
    public void Incomplete_token_usage_is_cost_unavailable()
    {
        var resolved = Resolver().Resolve(Call(null, 0, 10, null, Snapshot));

        Assert.Equal(ModelCostProvenance.CostUnavailable, resolved.Provenance);
    }

    [Fact]
    public void Stored_snapshot_is_authoritative_and_recalculates_with_historical_rates()
    {
        var resolved = Resolver().Resolve(Call(1_000, 250, 500, 999m, Snapshot));

        Assert.Equal(ModelCostProvenance.StoredPricingSnapshot, resolved.Provenance);
        Assert.Equal("stored pricing snapshot", resolved.ProvenanceLabel);
        Assert.Equal(0.018m, resolved.EstimatedCostUsd);
        Assert.True(resolved.HasStoredPricingSnapshot);
    }

    [Fact]
    public void Legacy_estimate_preserves_zero_and_distinguishes_missing_estimate()
    {
        var resolver = Resolver();
        var storedZero = resolver.Resolve(Call(0, 0, 0, 0m));
        var missing = resolver.Resolve(Call(null, null, null, null));

        Assert.Equal(ModelCostProvenance.LegacyEstimatePricingSnapshotUnavailable, storedZero.Provenance);
        Assert.Equal("legacy estimate \u2014 pricing snapshot unavailable", storedZero.ProvenanceLabel);
        Assert.Equal(0m, storedZero.EstimatedCostUsd);
        Assert.Equal(ModelCostProvenance.CostUnavailable, missing.Provenance);
        Assert.Equal("cost unavailable", missing.ProvenanceLabel);
        Assert.Null(missing.EstimatedCostUsd);
    }

    [Fact]
    public void Legacy_zero_estimate_is_unavailable_without_required_usage_evidence()
    {
        var resolved = Resolver().Resolve(Call(null, null, null, 0m));

        Assert.Equal(ModelCostProvenance.CostUnavailable, resolved.Provenance);
        Assert.Null(resolved.EstimatedCostUsd);
    }

    [Fact]
    public void Legacy_nonzero_estimate_is_unavailable_without_required_usage_evidence()
    {
        var resolved = Resolver().Resolve(Call(null, null, null, 12.34m));

        Assert.Equal(ModelCostProvenance.CostUnavailable, resolved.Provenance);
        Assert.Null(resolved.EstimatedCostUsd);
    }

    [Fact]
    public void Missing_estimate_is_reestimated_with_current_model_pricing()
    {
        var resolved = Resolver().Resolve(Call(1_000, 250, 500, null));

        Assert.Equal(ModelCostProvenance.ReestimatedUsingCurrentPricing, resolved.Provenance);
        Assert.Equal("re-estimated using current pricing", resolved.ProvenanceLabel);
        Assert.Equal(0.018m, resolved.EstimatedCostUsd);
        Assert.False(resolved.HasStoredPricingSnapshot);
    }

    [Fact]
    public void Unknown_model_without_stored_estimate_is_unavailable()
    {
        var resolved = Resolver().Resolve(Call(100, 0, 10, null) with { Model = "unknown-model" });

        Assert.Equal(ModelCostProvenance.CostUnavailable, resolved.Provenance);
        Assert.Null(resolved.EstimatedCostUsd);
    }

    [Fact]
    public void Total_excludes_unavailable_calls_and_reports_partial_completeness()
    {
        var total = Resolver().ResolveTotal([
            Call(1_000, 250, 500, null, Snapshot),
            Call(null, null, null, null) with { Model = "unknown-model" }
        ]);

        Assert.Equal(0.018m, total.TotalEstimatedCostUsd);
        Assert.Equal(1, total.AvailableCallCount);
        Assert.Equal(1, total.UnavailableCallCount);
        Assert.True(total.IsPartial);
    }

    [Fact]
    public void Total_rejects_out_of_range_and_negative_legacy_values_without_overflow()
    {
        var total = Resolver().ResolveTotal([
            Call(1, 0, 1, decimal.MaxValue),
            Call(1, 0, 1, decimal.MaxValue),
            Call(1, 0, 1, -1m)
        ]);

        Assert.Null(total.TotalEstimatedCostUsd);
        Assert.Equal(0, total.AvailableCallCount);
        Assert.Equal(3, total.UnavailableCallCount);
        Assert.True(total.IsPartial);
        Assert.False(total.Overflowed);
    }

    private static ModelCostCalculator Calculator() => new(new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = new(Snapshot.InputPerMillionUsd, Snapshot.CachedInputPerMillionUsd, Snapshot.OutputPerMillionUsd)
    });

    private static ModelCostResolver Resolver() => new(Calculator());

    private static ModelCallRecord Call(
        int? input,
        int? cached,
        int? output,
        decimal? estimate,
        ModelPricingSnapshot? snapshot = null) => new(
            Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "model", "medium",
            Now, Now, true, "response", input, cached, output, null, estimate, null, snapshot);
}
