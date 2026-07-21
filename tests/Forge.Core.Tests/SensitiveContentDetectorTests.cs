using Forge.Core;

namespace Forge.Core.Tests;

public sealed class SensitiveContentDetectorTests
{
    [Theory]
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

    [Fact]
    public void Runtime_constructed_credential_forms_are_detected_without_static_secret_literals()
    {
        var values = new[]
        {
            SyntheticSensitiveValues.BearerAuthorization(),
            SyntheticSensitiveValues.Jwt(),
            SyntheticSensitiveValues.JsonWithJwt(),
            SyntheticSensitiveValues.SasQuery(),
            SyntheticSensitiveValues.AmazonSignedQuery()
        };

        Assert.All(values, value => Assert.True(SensitiveContentDetector.ContainsSensitiveValue(value)));
    }

    [Theory]
    [InlineData("token budget = 6000")]
    [InlineData("password validation is required")]
    [InlineData("username label")]
    [InlineData("password label")]
    [InlineData("Enter username demo")]
    [InlineData("Enter password forge123")]
    [InlineData("https://example.invalid/path")]
    [InlineData("token count and token usage are shown")]
    [InlineData("API key documentation and bearer authentication explanation")]
    [InlineData("JWT format is header.payload.signature")]
    [InlineData("https://example.invalid/search?q=request%2Fresponse&expires=tomorrow")]
    [InlineData("sig=short-example&resource=document")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("src/Forge.Core/VerificationValidator.cs")]
    public void Ordinary_source_text_is_not_classified_as_a_secret(string value)
    {
        Assert.False(SensitiveContentDetector.ContainsSensitiveValue(value));
    }

    [Fact]
    public void Demo_password_assignment_reproduces_the_context_free_secret_assignment_match()
    {
        var value = "Enter password" + ": " + "forge123";

        Assert.True(SensitiveContentDetector.ContainsSensitiveValue(value));
    }

    [Fact]
    public void Generated_credential_structures_and_context_gated_entropy_are_detected_without_flagging_unlabelled_prose()
    {
        var random = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                     Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "Aa1-";
        var accessKey = "AKIA" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var values = new[]
        {
            $"Authorization: Bearer {random}",
            $"Authorization: Basic {random}",
            $"-----BEGIN PRIVATE KEY-----\n{random}",
            $"https://account:{Uri.EscapeDataString(random)}@example.invalid/path",
            accessKey,
            $"Server=example.invalid;Password={random};Database=forge",
            $"deployment credential: {random}"
        };

        Assert.All(values, value => Assert.True(SensitiveContentDetector.ContainsSensitiveValue(value)));
        Assert.False(SensitiveContentDetector.ContainsSensitiveValue(
            $"This ordinary explanation includes an opaque example {random} without a credential label."));
    }
}
