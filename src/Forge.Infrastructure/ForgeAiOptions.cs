namespace Forge.Infrastructure;

public static class ForgeAiModes
{
    public const string Fake = "Fake";
    public const string OpenAI = "OpenAI";
}

public sealed class ForgeAiOptions
{
    public const int MaximumImplementationOutputTokens = 100_000;
    public const int MaximumImplementationTimeoutSeconds = 600;
    public const decimal MaximumPricePerMillionUsd = 100_000m;
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
    public string ImplementationModel { get; set; } = "gpt-5.6-sol";
    public string ImplementationReasoningEffort { get; set; } = "high";
    public int ImplementationMaxOutputTokens { get; set; } = 32_000;
    public int ImplementationTimeoutSeconds { get; set; } = 180;
    public string VerificationPlanningModel { get; set; } = "gpt-5.6-sol";
    public string VerificationPlanningReasoningEffort { get; set; } = "medium";
    public int VerificationPlanningMaxOutputTokens { get; set; } = 8_000;
    public int VerificationPlanningTimeoutSeconds { get; set; } = 180;
    public string FailureAnalysisModel { get; set; } = "gpt-5.6-sol";
    public string FailureAnalysisReasoningEffort { get; set; } = "medium";
    public int FailureAnalysisMaxOutputTokens { get; set; } = 6_000;
    public int FailureAnalysisTimeoutSeconds { get; set; } = 180;
    public Dictionary<string, ModelPricing> Pricing { get; set; } = DefaultPricing();

    public bool IsClarificationConfigurationComplete(bool hasApiKey) =>
        hasApiKey &&
        !string.IsNullOrWhiteSpace(ClarificationModel) &&
        SupportedReasoningEfforts.Contains(ClarificationReasoningEffort) &&
        ClarificationMaxOutputTokens > 0 &&
        Pricing is not null && Pricing.ContainsKey(ClarificationModel);

    public bool IsPlanningConfigurationComplete(bool hasApiKey) =>
        hasApiKey &&
        !string.IsNullOrWhiteSpace(PlanningModel) &&
        SupportedReasoningEfforts.Contains(PlanningReasoningEffort) &&
        PlanningMaxOutputTokens > 0 &&
        Pricing is not null && Pricing.ContainsKey(PlanningModel);

    public bool IsOpenAiConfigurationComplete(bool hasApiKey) =>
        IsClarificationConfigurationComplete(hasApiKey) && IsPlanningConfigurationComplete(hasApiKey) &&
        IsImplementationConfigurationComplete(hasApiKey) && IsVerificationPlanningConfigurationComplete(hasApiKey);

    public bool IsImplementationConfigurationComplete(bool hasApiKey) =>
        hasApiKey &&
        !string.IsNullOrWhiteSpace(ImplementationModel) &&
        ImplementationModel.Length <= 160 &&
        SupportedReasoningEfforts.Contains(ImplementationReasoningEffort) &&
        ImplementationMaxOutputTokens is > 0 and <= MaximumImplementationOutputTokens &&
        ImplementationTimeoutSeconds is > 0 and <= MaximumImplementationTimeoutSeconds &&
        Pricing is not null && Pricing.TryGetValue(ImplementationModel, out var pricing) && IsValidPricing(pricing);

    public bool IsVerificationPlanningConfigurationComplete(bool hasApiKey) =>
        hasApiKey &&
        !string.IsNullOrWhiteSpace(VerificationPlanningModel) &&
        VerificationPlanningModel.Length <= 160 &&
        SupportedReasoningEfforts.Contains(VerificationPlanningReasoningEffort) &&
        VerificationPlanningMaxOutputTokens is > 0 and <= MaximumImplementationOutputTokens &&
        VerificationPlanningTimeoutSeconds is > 0 and <= MaximumImplementationTimeoutSeconds &&
        Pricing is not null && Pricing.TryGetValue(VerificationPlanningModel, out var pricing) && IsValidPricing(pricing);

    public bool IsFailureAnalysisConfigurationComplete(bool hasApiKey) =>
        hasApiKey && !string.IsNullOrWhiteSpace(FailureAnalysisModel) && FailureAnalysisModel.Length <= 160 &&
        SupportedReasoningEfforts.Contains(FailureAnalysisReasoningEffort) &&
        FailureAnalysisMaxOutputTokens is > 0 and <= MaximumImplementationOutputTokens &&
        FailureAnalysisTimeoutSeconds is > 0 and <= MaximumImplementationTimeoutSeconds &&
        Pricing is not null && Pricing.TryGetValue(FailureAnalysisModel, out var pricing) && IsValidPricing(pricing);

    public void ValidateSyntax()
    {
        if (!string.Equals(Mode, ForgeAiModes.Fake, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Forge AI mode configuration is invalid.");
        if (string.IsNullOrWhiteSpace(ImplementationModel) || ImplementationModel.Length > 160 ||
            !SupportedReasoningEfforts.Contains(ImplementationReasoningEffort) ||
            ImplementationMaxOutputTokens is <= 0 or > MaximumImplementationOutputTokens ||
            ImplementationTimeoutSeconds is <= 0 or > MaximumImplementationTimeoutSeconds ||
            Pricing is null || !Pricing.TryGetValue(ImplementationModel, out var pricing) || !IsValidPricing(pricing))
            throw new InvalidOperationException("Forge OpenAI implementation configuration is syntactically invalid.");
        if (string.IsNullOrWhiteSpace(VerificationPlanningModel) || VerificationPlanningModel.Length > 160 ||
            !SupportedReasoningEfforts.Contains(VerificationPlanningReasoningEffort) ||
            VerificationPlanningMaxOutputTokens is <= 0 or > MaximumImplementationOutputTokens ||
            VerificationPlanningTimeoutSeconds is <= 0 or > MaximumImplementationTimeoutSeconds ||
            Pricing is null || !Pricing.TryGetValue(VerificationPlanningModel, out var verificationPricing) ||
            !IsValidPricing(verificationPricing))
            throw new InvalidOperationException("Forge OpenAI verification-planning configuration is syntactically invalid.");
        if (string.IsNullOrWhiteSpace(FailureAnalysisModel) || FailureAnalysisModel.Length > 160 ||
            !SupportedReasoningEfforts.Contains(FailureAnalysisReasoningEffort) ||
            FailureAnalysisMaxOutputTokens is <= 0 or > MaximumImplementationOutputTokens ||
            FailureAnalysisTimeoutSeconds is <= 0 or > MaximumImplementationTimeoutSeconds ||
            Pricing is null || !Pricing.TryGetValue(FailureAnalysisModel, out var failurePricing) ||
            !IsValidPricing(failurePricing))
            throw new InvalidOperationException("Forge OpenAI failure-analysis configuration is syntactically invalid.");
    }

    internal static bool IsValidPricing(ModelPricing? pricing) => pricing is not null &&
        IsValidRate(pricing.InputPerMillionUsd) &&
        IsValidRate(pricing.CachedInputPerMillionUsd) &&
        IsValidRate(pricing.OutputPerMillionUsd);

    private static bool IsValidRate(decimal rate) => rate is >= 0 and <= MaximumPricePerMillionUsd;

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
