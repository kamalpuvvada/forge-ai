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
        "add", "allow", "create", "implement", "requirement", "users", "user", "forge", "please",
        "current", "existing", "model", "status", "code", "file", "files", "feature", "support",
        "plan", "include", "only", "not", "per", "solve", "omits"
    };
    private static readonly HashSet<string> WeakTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "task", "data", "service", "application", "repository", "result", "response", "request",
        "model", "status", "requirement", "existing", "current"
    };
    private static readonly HashSet<string> PhraseStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "should", "must", "will",
        "add", "allow", "create", "implement", "users", "user", "forge", "please",
        "include", "only", "not", "per", "solve", "omits"
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
        var requirementText = string.Join(' ', new[] { originalRequirement, approvedRequirementSummary }
            .Concat(clarificationAnswers.SelectMany(answer => new[] { answer.Question, answer.Answer })));
        return SelectCore(snapshot, textFiles, requirementText, null);
    }

    public EvidenceSelection SelectForPlanRevision(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile> textFiles,
        string approvedRequirementSummary,
        string correction) =>
        SelectCore(snapshot, textFiles, $"{approvedRequirementSummary} {correction}", ExtractSignals(correction));

    private EvidenceSelection SelectCore(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile> textFiles,
        string requirementText,
        RequirementSignals? prioritySignals)
    {
        var signals = ExtractSignals(requirementText);
        var seeksClarificationWork = signals.Terms.Any(term => term.StartsWith("clarif", StringComparison.Ordinal)) ||
            signals.Terms.Any(term => term.StartsWith("question", StringComparison.Ordinal));
        var seeksPlanningWork = signals.Phrases.Any(phrase => phrase is "planning engine" or "plan revision" or "plan correction");

        var ranked = textFiles
            .Where(file => IsSafeRelative(file.Metadata.RelativePath) && !RepositoryDiscoveryService.IsSecretFile(file.Metadata.RelativePath))
            .Select(file => Rank(file, signals, prioritySignals, seeksClarificationWork, seeksPlanningWork))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.File.Metadata.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var initial = Diversify(ranked, limits.MaximumEvidenceFiles);
        var seeksFrontendWork = signals.Terms.Any(term => term is "frontend" or "browser" or "screen");
        var companionBoosts = FindCompanionBoosts(initial, ranked, seeksFrontendWork);
        var expandedRanked = ranked.Select(item => companionBoosts.TryGetValue(item.File.Metadata.RelativePath, out var companion)
                ? item with { Score = item.Score + companion.Bonus, Reason = $"{item.Reason}, {companion.Reason}" }
                : item)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.File.Metadata.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ordered = Diversify(expandedRanked, limits.MaximumEvidenceFiles);
        var selected = new List<EvidenceItem>();
        var totalCharacters = 0;
        foreach (var item in ordered)
        {
            if (selected.Count >= limits.MaximumEvidenceFiles) break;
            var snippet = CreateSnippet(item.File.Content, signals.SearchSignals, limits.MaximumEvidenceCharactersPerFile);
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

        return new EvidenceSelection(selected, textFiles.Count, selected.Count, totalCharacters);
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

    private static IReadOnlyList<RankedFile> Diversify(IReadOnlyList<RankedFile> ranked, int maximum)
    {
        if (ranked.Count == 0 || maximum <= 0) return [];
        var chosen = new List<RankedFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(ranked[0]);
        foreach (var module in ranked.Select(item => item.Module).Distinct().Where(module => module is not "Docs" and not "Other"))
        {
            var candidate = ranked.FirstOrDefault(item => item.Module == module && item.Score > 0);
            if (candidate is not null) Add(candidate);
        }

        foreach (var candidate in ranked.Where(item => item.PriorityNameMatch))
        {
            if (chosen.Count >= maximum) break;
            if (CanAdd(candidate)) Add(candidate);
        }

        foreach (var candidate in ranked.Where(item => item.Module != "Docs")
                     .Concat(ranked.Where(item => item.Module == "Docs")))
        {
            if (chosen.Count >= maximum) break;
            if (candidate.Score <= 0 && chosen.Count > 0) break;
            if (!CanAdd(candidate)) continue;
            Add(candidate);
        }

        return chosen.Take(maximum).ToArray();

        void Add(RankedFile candidate)
        {
            if (chosen.Count < maximum && paths.Add(candidate.File.Metadata.RelativePath)) chosen.Add(candidate);
        }


        bool CanAdd(RankedFile candidate)
        {
            if (paths.Contains(candidate.File.Metadata.RelativePath)) return false;
            var moduleLimit = candidate.PriorityNameMatch ? 4 : 3;
            if (candidate.Module != "Docs" && chosen.Count(item => item.Module == candidate.Module) >= moduleLimit) return false;
            return candidate.Module != "Docs" || !chosen.Any(item => item.Module != "Docs") ||
                chosen.Count(item => item.Module == "Docs") < 1;
        }
    }

    private static RankedFile Rank(
        RepositoryTextFile file,
        RequirementSignals signals,
        RequirementSignals? prioritySignals,
        bool seeksClarificationWork,
        bool seeksPlanningWork)
    {
        var rawPath = file.Metadata.RelativePath.ToLowerInvariant();
        var path = NormalizeSearchText(file.Metadata.RelativePath);
        var symbols = NormalizeSearchText(string.Join(' ', file.Metadata.DeclaredSymbols));
        var content = NormalizeSearchText(file.Content);
        var module = ClassifyModule(rawPath);
        var phrasePath = signals.Phrases.Count(path.Contains);
        var phraseSymbols = signals.Phrases.Count(symbols.Contains);
        var phraseContent = signals.Phrases.Sum(phrase => Math.Min(3, Regex.Matches(content, Regex.Escape(phrase)).Count));
        var strongPath = signals.Terms.Count(term => !WeakTerms.Contains(term) && ContainsTerm(path, term));
        var strongSymbols = signals.Terms.Count(term => !WeakTerms.Contains(term) && ContainsTerm(symbols, term));
        var contentMatches = signals.Terms.Sum(term => Math.Min(5, CountTermMatches(content, term)) * (WeakTerms.Contains(term) ? 1 : 2));
        var priorityPhrasePath = prioritySignals?.Phrases.Count(path.Contains) ?? 0;
        var priorityPhraseSymbols = prioritySignals?.Phrases.Count(symbols.Contains) ?? 0;
        var priorityPhraseContent = prioritySignals?.Phrases.Sum(phrase => Math.Min(2, Regex.Matches(content, Regex.Escape(phrase)).Count)) ?? 0;
        var priorityStrongPath = prioritySignals?.Terms.Count(term => !WeakTerms.Contains(term) && ContainsTerm(path, term)) ?? 0;
        var priorityStrongSymbols = prioritySignals?.Terms.Count(term => !WeakTerms.Contains(term) && ContainsTerm(symbols, term)) ?? 0;
        var priorityContent = prioritySignals?.Terms.Sum(term => Math.Min(3, CountTermMatches(content, term)) * (WeakTerms.Contains(term) ? 1 : 2)) ?? 0;
        var priorityCoverage = prioritySignals?.Terms.Count(term => ContainsTerm(path, term) || ContainsTerm(symbols, term) || ContainsTerm(content, term)) ?? 0;
        var roleBonus = file.Metadata.ProbableRole is "entry point" or "configuration" or "project manifest" ? 4 : 0;
        var prefersFrontendLayer = module == "Frontend" &&
            signals.Terms.Any(term => term is "frontend" or "browser" or "screen") &&
            (rawPath.EndsWith("app.tsx") || rawPath.EndsWith("app.ts") || rawPath.EndsWith("api.ts") ||
             rawPath.EndsWith("types.ts") || rawPath.Contains("component"));
        var frontendLayerBonus = prefersFrontendLayer &&
            (rawPath.EndsWith("app.tsx") || rawPath.EndsWith("app.ts")) ? 260 : prefersFrontendLayer ? 90 : 0;
        var taskSurfaceBonus = signals.Terms.Contains("task") && rawPath.Contains("engineeringtask") &&
            module is "API" or "Core" or "Infrastructure" ? 80 : 0;
        var domainRecordBonus = module == "Core" && rawPath.EndsWith("record.cs", StringComparison.Ordinal) &&
            (prioritySignals?.Terms.Contains("record") == true || signals.Terms.Contains("domain")) ? 240 : 0;
        var modelCostSurfaceBonus = module == "Infrastructure" && rawPath.Contains("modelcost") &&
            prioritySignals?.Terms.Any(term => term is "cost" or "pricing" or "snapshot" or "snapshots") == true
                ? 700
                : 0;
        var persistenceSurfaceBonus = rawPath.Contains("sqlite") &&
            (rawPath.Contains("repository") || rawPath.Contains("persistence")) &&
            prioritySignals?.Terms.Any(term => term is "sqlite" or "migration" or "persistence") == true
                ? 700
                : 0;
        var companionBonus = IsCompanion(file.Metadata, rawPath) && (phraseContent + contentMatches + strongSymbols > 0) ? 7 : 0;
        var docsPenalty = module == "Docs" && phrasePath == 0 && phraseContent == 0 ? 14 : 0;
        var clarificationPenalty = !seeksClarificationWork && (rawPath.Contains("clarification") || symbols.Contains("clarification")) ? 300 : 0;
        var planningPenalty = !seeksPlanningWork &&
            (rawPath.Contains("openaiplanning") || rawPath.Contains("fakeplanning") || symbols.Contains("planning engine")) ? 300 : 0;
        var score = phrasePath * 34 + phraseSymbols * 28 + phraseContent * 9 + strongPath * 15 +
            strongSymbols * 12 + contentMatches * 2 + priorityPhrasePath * 50 + priorityPhraseSymbols * 40 +
            priorityPhraseContent * 12 + priorityStrongPath * 20 + priorityStrongSymbols * 16 + priorityContent * 3 +
            Math.Min(5, priorityCoverage) * 20 + roleBonus + frontendLayerBonus + taskSurfaceBonus +
            domainRecordBonus + modelCostSurfaceBonus + persistenceSurfaceBonus + companionBonus +
            (file.Metadata.IsTest ? 3 : 0) -
            docsPenalty - clarificationPenalty - planningPenalty;
        var reasons = new List<string>();
        if (phrasePath + phraseSymbols > 0) reasons.Add("strong requirement phrase in path or symbol");
        if (phraseContent > 0) reasons.Add("strong requirement phrase in content");
        if (strongPath + strongSymbols > 0) reasons.Add("specific requirement term in path or symbol");
        if (contentMatches > 0) reasons.Add("requirement terms in content");
        if (priorityPhrasePath + priorityPhraseSymbols + priorityPhraseContent > 0) reasons.Add("latest correction phrase match");
        if (priorityStrongPath + priorityStrongSymbols + priorityContent > 0) reasons.Add("latest correction term match");
        if (companionBonus > 0) reasons.Add("related contract, entry point, or test companion");
        if (frontendLayerBonus > 0) reasons.Add("frontend requirement layer preference");
        if (taskSurfaceBonus > 0) reasons.Add("task API/domain surface preference");
        if (domainRecordBonus > 0) reasons.Add("explicit domain-record correction preference");
        if (modelCostSurfaceBonus > 0) reasons.Add("explicit model-cost correction preference");
        if (persistenceSurfaceBonus > 0) reasons.Add("explicit SQLite/persistence correction preference");
        reasons.Add($"{module.ToLowerInvariant()} layer");
        if (docsPenalty > 0) reasons.Add("generic documentation de-prioritized");
        if (clarificationPenalty > 0) reasons.Add("unrelated clarification subsystem de-prioritized");
        if (planningPenalty > 0) reasons.Add("unrelated planning adapter de-prioritized");
        var priorityNameMatch = priorityPhrasePath + priorityPhraseSymbols + priorityStrongPath + priorityStrongSymbols > 0;
        return new RankedFile(file, score, string.Join(", ", reasons), module, priorityNameMatch);
    }

    private static bool IsCompanion(RepositoryFileMetadata metadata, string path) => metadata.IsTest ||
        metadata.Association is not null || metadata.ProbableRole is "entry point" or "configuration" or "project manifest" ||
        path.Contains("controller") || path.Contains("contract") || path.EndsWith("types.ts") || path.EndsWith("api.ts");

    private static IReadOnlyDictionary<string, CompanionBoost> FindCompanionBoosts(
        IReadOnlyList<RankedFile> initiallySelected,
        IReadOnlyList<RankedFile> ranked,
        bool seeksFrontendWork)
    {
        var byPath = ranked.ToDictionary(item => NormalizeRelative(item.File.Metadata.RelativePath), StringComparer.OrdinalIgnoreCase);
        var boosts = new Dictionary<string, CompanionBoost>(StringComparer.OrdinalIgnoreCase);
        foreach (var selected in initiallySelected)
        {
            var extension = selected.File.Metadata.Extension;
            if (seeksFrontendWork && extension is (".ts" or ".tsx" or ".js" or ".jsx"))
            {
                foreach (Match match in Regex.Matches(selected.File.Content,
                             "(?m)\\b(?:import|export)\\s+(?:[^'\"]*?\\s+from\\s+)?['\"](?<path>\\.[^'\"]+)['\"]"))
                {
                    var importPath = match.Groups["path"].Value;
                    foreach (var candidatePath in ResolveLocalScriptImport(selected.File.Metadata.RelativePath, importPath, byPath.Keys))
                    {
                        var candidate = byPath[candidatePath];
                        if (!IsMaterialScriptCompanion(candidate.File.Metadata.RelativePath)) continue;
                        AddBoost(selected.File.Metadata.RelativePath, 200, "selected TypeScript/JavaScript companion origin");
                        AddBoost(candidate.File.Metadata.RelativePath, 700, "selected local TypeScript/JavaScript import companion");
                    }
                }
            }

            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && !selected.File.Metadata.IsTest)
            {
                foreach (var candidate in ranked.Where(item => item.File.Metadata.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.Equals(candidate.File.Metadata.RelativePath, selected.File.Metadata.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                        !IsMaterialCSharpCompanion(candidate.File.Metadata)) continue;
                    var selectedReferencesCandidate = candidate.File.Metadata.DeclaredSymbols.Any(symbol => ContainsSymbol(selected.File.Content, symbol));
                    var candidateTestReferencesSelected = candidate.File.Metadata.IsTest &&
                        selected.File.Metadata.DeclaredSymbols.Any(symbol => ContainsSymbol(candidate.File.Content, symbol));
                    if (selectedReferencesCandidate || candidateTestReferencesSelected)
                        AddBoost(candidate.File.Metadata.RelativePath, 80, "selected C# symbol companion");
                }
            }
        }
        return boosts;

        void AddBoost(string path, int bonus, string reason)
        {
            if (!boosts.TryGetValue(path, out var existing) || bonus > existing.Bonus)
                boosts[path] = new CompanionBoost(bonus, reason);
        }

        static bool ContainsSymbol(string content, string symbol) => symbol.Length >= 4 &&
            Regex.IsMatch(content, $@"\b{Regex.Escape(symbol)}\b", RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> ResolveLocalScriptImport(
        string sourcePath,
        string importPath,
        IEnumerable<string> repositoryPaths)
    {
        if (!importPath.StartsWith("./", StringComparison.Ordinal) && !importPath.StartsWith("../", StringComparison.Ordinal))
            return [];
        var sourceDirectory = NormalizeRelative(Path.GetDirectoryName(sourcePath) ?? string.Empty);
        var combined = TryCombineContainedRelativePath(sourceDirectory, importPath);
        if (combined is null) return [];
        var extension = Path.GetExtension(combined);
        var candidates = string.IsNullOrEmpty(extension)
            ? new[] { $"{combined}.ts", $"{combined}.tsx", $"{combined}.js", $"{combined}.jsx", $"{combined}/index.ts", $"{combined}/index.tsx", $"{combined}/index.js", $"{combined}/index.jsx" }
            : [combined];
        var available = repositoryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(available.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? TryCombineContainedRelativePath(string directory, string importPath)
    {
        var segments = directory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in NormalizeRelative(importPath).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (segment.Contains(':')) return null;
            segments.Add(segment);
        }
        var path = string.Join('/', segments);
        return IsSafeRelative(path) ? path : null;
    }

    private static bool IsMaterialScriptCompanion(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("api", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("types", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("utils", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("index", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("service", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("component", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMaterialCSharpCompanion(RepositoryFileMetadata metadata)
    {
        var path = metadata.RelativePath;
        return metadata.IsTest || path.Contains("contract", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("service", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("repository", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("registration", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyModule(string path)
    {
        if (path.StartsWith("docs/") || path.EndsWith("readme.md") || path.EndsWith(".md")) return "Docs";
        if (path.Contains("test") && (path.StartsWith("web/") || path.Contains("frontend") || path.EndsWith(".tsx") || path.EndsWith(".ts"))) return "FrontendTests";
        if (path.Contains("test")) return "BackendTests";
        if (path.StartsWith("web/") || path.Contains("frontend") || path.EndsWith(".tsx") || path.EndsWith(".css")) return "Frontend";
        if (path.Contains(".api/") || path.StartsWith("src/forge.api/") || path.Contains("controller")) return "API";
        if (path.Contains(".infrastructure/") || path.StartsWith("src/forge.infrastructure/")) return "Infrastructure";
        if (path.Contains(".core/") || path.StartsWith("src/forge.core/")) return "Core";
        return "Other";
    }

    private static Snippet CreateSnippet(string content, IReadOnlyList<string> signals, int maximumCharacters)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var matchIndex = Array.FindIndex(lines, line =>
        {
            var searchable = NormalizeSearchText(line);
            return signals.Any(searchable.Contains);
        });
        if (matchIndex < 0) matchIndex = 0;
        var start = Math.Max(0, matchIndex - 4);
        var end = Math.Min(lines.Length - 1, matchIndex + 10);
        var excerpt = string.Join('\n', lines[start..(end + 1)]);
        if (excerpt.Length > maximumCharacters) excerpt = excerpt[..maximumCharacters];
        var actualLines = excerpt.Count(character => character == '\n') + (excerpt.Length > 0 ? 1 : 0);
        return new Snippet(start + 1, start + Math.Max(1, actualLines), excerpt);
    }

    private static RequirementSignals ExtractSignals(string value)
    {
        var raw = Regex.Matches(NormalizeSearchText(value), "[a-z0-9]+")
            .Select(match => match.Value)
            .Where(term => term.Length >= 3)
            .ToArray();
        var terms = raw.Where(term => !StopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var phraseTokens = raw.Select(term => PhraseStopWords.Contains(term) ? string.Empty : term).ToArray();
        var phrases = Enumerable.Range(0, phraseTokens.Length)
            .SelectMany(index => new[] { 3, 2 }.Where(length => index + length <= phraseTokens.Length)
                .Select(length => phraseTokens[index..(index + length)]))
            .Where(parts => parts.All(part => part.Length > 0) && parts.Any(part => !WeakTerms.Contains(part)))
            .Select(parts => string.Join(' ', parts))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(phrase => phrase.Length)
            .ThenBy(phrase => phrase, StringComparer.Ordinal)
            .ToArray();
        return new RequirementSignals(terms, phrases, phrases.Concat(terms).ToArray());
    }

    internal static string NormalizeSearchText(string value)
    {
        var splitAcronyms = Regex.Replace(value, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        var splitWords = Regex.Replace(splitAcronyms, "([a-z0-9])([A-Z])", "$1 $2");
        return Regex.Replace(splitWords.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    }

    private static bool ContainsTerm(string normalizedText, string term) => CountTermMatches(normalizedText, term) > 0;

    private static int CountTermMatches(string normalizedText, string term) =>
        Regex.Matches(normalizedText, $@"(?:^| ){Regex.Escape(term)}[a-z0-9]*(?= |$)").Count;

    private static int FindValueSeparator(string line)
    {
        var equals = line.IndexOf('=');
        var colon = line.IndexOf(':');
        if (equals < 0) return colon;
        if (colon < 0) return equals;
        return Math.Min(equals, colon);
    }

    private static bool IsSafeRelative(string path) => !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");
    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record RequirementSignals(IReadOnlyList<string> Terms, IReadOnlyList<string> Phrases, IReadOnlyList<string> SearchSignals);
    private sealed record RankedFile(RepositoryTextFile File, int Score, string Reason, string Module, bool PriorityNameMatch);
    private sealed record CompanionBoost(int Bonus, string Reason);
    private sealed record Snippet(int StartLine, int EndLine, string Excerpt);
}
