using Forge.Core;

namespace Forge.Core.Tests;

public sealed class SensitiveContentDetectorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnop")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("https://user:password@example.invalid/path")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("password=correct-horse-battery-staple")]
    [InlineData("api_key: sk-abcdefghijklmnopqrstuvwxyz")]
    public void High_confidence_secret_forms_are_detected_without_returning_values(string value)
    {
        Assert.True(SensitiveContentDetector.ContainsSensitiveValue(value));
    }

    [Theory]
    [InlineData("token budget = 6000")]
    [InlineData("password validation is required")]
    [InlineData("https://example.invalid/path")]
    public void Ordinary_source_text_is_not_classified_as_a_secret(string value)
    {
        Assert.False(SensitiveContentDetector.ContainsSensitiveValue(value));
    }
}
