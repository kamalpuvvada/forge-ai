using System.Text.RegularExpressions;

namespace Forge.Core;

public static partial class SensitiveContentDetector
{
    public static bool ContainsSensitiveValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return AuthorizationRegex().IsMatch(value) || PrivateKeyRegex().IsMatch(value) ||
               CredentialUriRegex().IsMatch(value) || AccessTokenRegex().IsMatch(value) ||
               PasswordConnectionStringRegex().IsMatch(value) || SecretAssignmentRegex().IsMatch(value);
    }

    [GeneratedRegex(@"(?im)\bauthorization\s*:\s*(?:bearer|basic)\s+[A-Za-z0-9+/._~=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s/:@]+:[^\s/@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUriRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AKIA[0-9A-Z]{16}|(?:gh[pousr]_[A-Za-z0-9]{20,})|(?:sk-[A-Za-z0-9_-]{20,}))(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenRegex();

    [GeneratedRegex(@"(?i)(?:^|[;\r\n])\s*(?:password|pwd)\s*=\s*[^;\r\n]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordConnectionStringRegex();

    [GeneratedRegex("""(?im)\b(?:api[_-]?key|client[_-]?secret|access[_-]?token|auth[_-]?token|password|passwd|pwd)\b\s*[:=]\s*['"]?[A-Za-z0-9+/._~=-]{8,}""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();
}
