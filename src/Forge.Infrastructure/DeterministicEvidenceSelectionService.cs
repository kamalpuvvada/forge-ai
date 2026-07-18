using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class DeterministicEvidenceSelectionService(RepositoryAnalysisLimits limits) : IEvidenceSelectionService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "should", "must", "will",
        "add", "allow", "create", "implement", "requirement", "users", "user", "forge"
    };
    private static readonly string[] SensitiveKeys =
    [
        "password", "secret", "token", "api key", "apikey", "api_key", "connection string",
        "connectionstring", "connection_string", "client secret", "clientsecret", "client_secret", "private key", "privatekey", "private_key"
    ];

    public EvidenceSelection Select(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile> textFiles,
        string originalRequirement,
        string approvedRequirementSummary,
        IReadOnlyList<ClarificationAnswer> clarificationAnswers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var terms = ExtractTerms(string.Join(' ', new[] { originalRequirement, approvedRequirementSummary }
            .Concat(clarificationAnswers.SelectMany(answer => new[] { answer.Question, answer.Answer }))));

        var ranked = textFiles
            .Where(file => IsSafeRelative(file.Metadata.RelativePath) && !RepositoryDiscoveryService.IsSecretFile(file.Metadata.RelativePath))
            .Select(file => Rank(file, terms))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.File.Metadata.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = new List<EvidenceItem>();
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalCharacters = 0;
        foreach (var item in ranked)
        {
            if (selected.Count >= limits.MaximumEvidenceFiles) break;
            if (!selectedPaths.Add(item.File.Metadata.RelativePath)) continue;
            var snippet = CreateSnippet(item.File.Content, terms, limits.MaximumEvidenceCharactersPerFile);
            var redacted = RedactSensitiveValues(snippet.Excerpt);
            var remaining = limits.MaximumEvidenceCharacters - totalCharacters;
            if (remaining <= 0) break;
            if (redacted.Length > remaining) redacted = redacted[..remaining];
            if (string.IsNullOrWhiteSpace(redacted)) continue;
            selected.Add(new EvidenceItem(
                $"E{selected.Count + 1}",
                item.File.Metadata.RelativePath,
                snippet.StartLine,
                snippet.EndLine,
                redacted,
                item.Reason,
                item.Score,
                Hash(redacted)));
            totalCharacters += redacted.Length;
        }

        return new EvidenceSelection(selected, textFiles.Count, selectedPaths.Count, totalCharacters);
    }

    internal static string RedactSensitiveValues(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lower = line.ToLowerInvariant();
            if (!SensitiveKeys.Any(lower.Contains)) continue;
            var separator = FindValueSeparator(line);
            lines[index] = separator >= 0 ? $"{line[..(separator + 1)]} [REDACTED]" : "[REDACTED]";
        }
        return string.Join('\n', lines);
    }

    private static RankedFile Rank(RepositoryTextFile file, IReadOnlyList<string> terms)
    {
        var path = file.Metadata.RelativePath.ToLowerInvariant();
        var symbols = string.Join(' ', file.Metadata.DeclaredSymbols).ToLowerInvariant();
        var content = file.Content.ToLowerInvariant();
        var pathMatches = terms.Count(path.Contains);
        var symbolMatches = terms.Count(symbols.Contains);
        var contentMatches = terms.Sum(term => Math.Min(5, Regex.Matches(content, Regex.Escape(term)).Count));
        var roleBonus = file.Metadata.ProbableRole is "entry point" or "configuration" or "project manifest" ? 3 : 0;
        var score = pathMatches * 12 + symbolMatches * 10 + contentMatches * 2 + roleBonus + (file.Metadata.IsTest ? 2 : 0);
        var reasons = new List<string>();
        if (pathMatches > 0) reasons.Add("requirement term in path");
        if (symbolMatches > 0) reasons.Add("matching declared symbol");
        if (contentMatches > 0) reasons.Add("requirement term in content");
        if (roleBonus > 0) reasons.Add(file.Metadata.ProbableRole);
        if (reasons.Count == 0) reasons.Add("repository context fallback");
        return new RankedFile(file, score, string.Join(", ", reasons));
    }

    private static Snippet CreateSnippet(string content, IReadOnlyList<string> terms, int maximumCharacters)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var matchIndex = Array.FindIndex(lines, line => terms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)));
        if (matchIndex < 0) matchIndex = 0;
        var start = Math.Max(0, matchIndex - 4);
        var end = Math.Min(lines.Length - 1, matchIndex + 10);
        var excerpt = string.Join('\n', lines[start..(end + 1)]);
        if (excerpt.Length > maximumCharacters) excerpt = excerpt[..maximumCharacters];
        var actualLines = excerpt.Count(character => character == '\n') + (excerpt.Length > 0 ? 1 : 0);
        return new Snippet(start + 1, start + Math.Max(1, actualLines), excerpt);
    }

    private static IReadOnlyList<string> ExtractTerms(string value) => Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+")
        .Select(match => match.Value)
        .Where(term => term.Length >= 3 && !StopWords.Contains(term))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static int FindValueSeparator(string line)
    {
        var equals = line.IndexOf('=');
        var colon = line.IndexOf(':');
        if (equals < 0) return colon;
        if (colon < 0) return equals;
        return Math.Min(equals, colon);
    }

    private static bool IsSafeRelative(string path) => !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record RankedFile(RepositoryTextFile File, int Score, string Reason);
    private sealed record Snippet(int StartLine, int EndLine, string Excerpt);
}
