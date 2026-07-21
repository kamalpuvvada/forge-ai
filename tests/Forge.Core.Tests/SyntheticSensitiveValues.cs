using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core.Tests;

internal static class SyntheticSensitiveValues
{
    public static string Jwt()
    {
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url("""{"sub":"synthetic-forge-test","scope":"test-only"}""");
        var signature = Base64Url("synthetic test signature material");
        return string.Join('.', header, payload, signature);
    }

    public static string JsonWithJwt() =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = Jwt() });

    public static string BearerAuthorization() =>
        string.Join(' ', "Authorization:", "Bearer", OpaqueValue("bearer test fixture"));

    public static string SasQuery() => SignedQuery(
        "storage.example.invalid",
        "/blob",
        ["sv=2024-01-01", "sp=rw", "se=2030-01-01", "sr=b"]);

    public static string AmazonSignedQuery() => SignedQuery(
        "example.invalid",
        "/object",
        ["X-Amz-Credential=synthetic-test", "X-Amz-Expires=600"],
        "X-Amz-Signature");

    private static string SignedQuery(
        string host,
        string path,
        IEnumerable<string> supportParameters,
        string signatureParameter = "sig")
    {
        var query = supportParameters.Append($"{signatureParameter}={OpaqueValue("signed query test fixture")}");
        return new UriBuilder(Uri.UriSchemeHttps, host) { Path = path, Query = string.Join('&', query) }.Uri.AbsoluteUri;
    }

    private static string OpaqueValue(string purpose) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose))).ToLowerInvariant();

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
