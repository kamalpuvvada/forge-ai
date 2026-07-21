using System.Text.RegularExpressions;

namespace Forge.Core;

public static partial class SensitiveContentDetector
{
    private const int MaximumInspectedCharacters = 256 * 1024;

    public static bool ContainsSensitiveValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var bounded = value.Length <= MaximumInspectedCharacters ? value : value[..MaximumInspectedCharacters];
        return AuthorizationRegex().IsMatch(bounded) || PrivateKeyRegex().IsMatch(bounded) ||
               CredentialUriRegex().IsMatch(bounded) || AccessTokenRegex().IsMatch(bounded) ||
               JwtRegex().IsMatch(bounded) || HasSignedCredentialQuery(bounded) ||
               PasswordConnectionStringRegex().IsMatch(bounded) || SecretAssignmentRegex().IsMatch(bounded) ||
               HasCredentialGatedEntropy(bounded);
    }

    [GeneratedRegex(@"(?im)\bauthorization\s*:\s*(?:bearer|basic)\s+[A-Za-z0-9+/._~=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s/:@]+:[^\s/@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUriRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AKIA[0-9A-Z]{16}|(?:gh[pousr]_[A-Za-z0-9]{20,})|(?:sk-[A-Za-z0-9_-]{20,}))(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)(?:^|[;\r\n])\s*(?:password|pwd)\s*=\s*[^;\r\n]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordConnectionStringRegex();

    [GeneratedRegex("""(?im)\b(?:api[_-]?key|client[_-]?secret|access[_-]?token|auth[_-]?token|password|passwd|pwd)\b\s*[:=]\s*['"]?[A-Za-z0-9+/._~=-]{8,}""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    private static bool HasCredentialGatedEntropy(string value)
    {
        foreach (Match match in CredentialEntropyRegex().Matches(value))
        {
            var candidate = match.Groups[1].Value;
            var classes = (candidate.Any(char.IsLower) ? 1 : 0) +
                          (candidate.Any(char.IsUpper) ? 1 : 0) +
                          (candidate.Any(char.IsDigit) ? 1 : 0) +
                          (candidate.Any(character => "+/=_-.~".Contains(character, StringComparison.Ordinal)) ? 1 : 0);
            if (classes >= 3 && candidate.Distinct().Take(12).Count() >= 12) return true;
        }
        return false;
    }

    [GeneratedRegex("""(?im)\b(?:credential|secret|token|api[_-]?key|private[_-]?key)\b\s*[:=]\s*['"]?([A-Za-z0-9+/=_\-.~]{24,})""", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialEntropyRegex();

    private static bool HasSignedCredentialQuery(string value)
    {
        var support = SignedQuerySupportRegex().IsMatch(value);
        if (!support) return false;
        foreach (Match match in SignedQuerySignatureRegex().Matches(value))
        {
            var candidate = match.Groups[1].Value;
            if (candidate.Length >= 16 && candidate.Distinct().Take(8).Count() >= 8) return true;
        }
        return false;
    }

    [GeneratedRegex("""(?i)(?:[?&]|\\u0026)(?:sig|signature|x-amz-signature)=([^&#\s"']{16,512})""", RegexOptions.CultureInvariant)]
    private static partial Regex SignedQuerySignatureRegex();

    [GeneratedRegex("""(?i)(?:[?&]|\\u0026)(?:se|sp|sr|sv|expires|permissions|resource|credential|x-amz-credential|x-amz-expires)=[^&#\s"']+""", RegexOptions.CultureInvariant)]
    private static partial Regex SignedQuerySupportRegex();
}
