namespace Forge.Infrastructure;

public static class ForgeAiModes
{
    public const string Fake = "Fake";
    public const string OpenAI = "OpenAI";
}

public sealed class ForgeAiOptions
{
    private static readonly HashSet<string> SupportedReasoningEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "minimal", "low", "medium", "high", "xhigh", "max"
    };

    public string Mode { get; set; } = ForgeAiModes.Fake;
    public string ClarificationModel { get; set; } = "gpt-5.6-terra";
    public string ClarificationReasoningEffort { get; set; } = "low";
    public int ClarificationMaxOutputTokens { get; set; } = 800;
    public string PlanningModel { get; set; } = "gpt-5.6-sol";
    public string PlanningReasoningEffort { get; set; } = "medium";
    public int PlanningMaxOutputTokens { get; set; } = 6000;
    public Dictionary<string, ModelPricing> Pricing { get; set; } = DefaultPricing();

    public bool IsClarificationConfigurationComplete(bool hasApiKey) =>
        hasApiKey &&
        !string.IsNullOrWhiteSpace(ClarificationModel) &&
        SupportedReasoningEfforts.Contains(ClarificationReasoningEffort) &&
        ClarificationMaxOutputTokens > 0 &&
        Pricing.ContainsKey(ClarificationModel);

    public bool IsPlanningConfigurationComplete(bool hasApiKey) =>
        hasApiKey &&
        !string.IsNullOrWhiteSpace(PlanningModel) &&
        SupportedReasoningEfforts.Contains(PlanningReasoningEffort) &&
        PlanningMaxOutputTokens > 0 &&
        Pricing.ContainsKey(PlanningModel);

    public bool IsOpenAiConfigurationComplete(bool hasApiKey) =>
        IsClarificationConfigurationComplete(hasApiKey) && IsPlanningConfigurationComplete(hasApiKey);

    public static Dictionary<string, ModelPricing> DefaultPricing() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6-sol"] = new(5.00m, 0.50m, 30.00m),
        ["gpt-5.6-terra"] = new(2.50m, 0.25m, 15.00m),
        ["gpt-5.6-luna"] = new(1.00m, 0.10m, 6.00m)
    };
}

public sealed record ModelPricing(
    decimal InputPerMillionUsd,
    decimal CachedInputPerMillionUsd,
    decimal OutputPerMillionUsd);
