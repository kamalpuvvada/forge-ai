namespace Forge.Testing;

public static class LiveOpenAITestGate
{
    public static bool IsEligible(string? enabled, string? filterAcknowledged, string? apiKey) =>
        string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(filterAcknowledged, "true", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(apiKey);
}
