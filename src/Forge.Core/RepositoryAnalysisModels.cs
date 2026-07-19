namespace Forge.Core;

public sealed class RepositoryAnalysisLimits
{
    public int MaximumDiscoveredFiles { get; set; } = 5_000;
    public long MaximumTextFileBytes { get; set; } = 256 * 1024;
    public long MaximumTotalTextBytes { get; set; } = 20 * 1024 * 1024;
    public int MaximumEvidenceFiles { get; set; } = 12;
    public int MaximumEvidenceCharacters { get; set; } = 60_000;
    public int MaximumEvidenceCharactersPerFile { get; set; } = 5_000;
    public int SnapshotMaximumAgeMinutes { get; set; } = 30;
}

public sealed record RepositoryFileMetadata(
    string RelativePath,
    string Extension,
    long SizeBytes,
    int LineCount,
    string ProbableRole,
    bool IsTest,
    string? Association,
    IReadOnlyList<string> DeclaredSymbols,
    bool HasUtf8Bom = false,
    bool IsStrictUtf8 = true);

public sealed record RepositorySnapshot(
    string NormalizedRoot,
    bool IsGitRepository,
    string? Branch,
    string? ShortHeadSha,
    string? FullHeadSha,
    string WorkingTreeStatus,
    int TotalDiscoveredFiles,
    int EligibleTextFileCount,
    int ExcludedFileCount,
    IReadOnlyList<string> DetectedLanguages,
    IReadOnlyList<string> DetectedExtensions,
    IReadOnlyList<string> ProjectFiles,
    IReadOnlyList<string> TestLocations,
    IReadOnlyList<string> Warnings,
    DateTimeOffset AnalyzedAt,
    string Fingerprint,
    IReadOnlyList<RepositoryFileMetadata> Files,
    string? GitStatusHash = null);

public sealed record RepositoryTextFile(RepositoryFileMetadata Metadata, string Content);

public sealed record RepositoryDiscoveryResult(
    RepositorySnapshot Snapshot,
    IReadOnlyList<RepositoryTextFile> TextFiles);

public sealed record RepositorySnapshotReadResult(
    bool IsFresh,
    IReadOnlyList<RepositoryTextFile> TextFiles);

public sealed record EvidenceItem(
    string Id,
    string RelativePath,
    int StartLine,
    int EndLine,
    string Excerpt,
    string ReasonSelected,
    int Score,
    string ContentHash);

public sealed record EvidenceSelection(
    IReadOnlyList<EvidenceItem> Items,
    int FilesInspected,
    int FilesSelected,
    int TotalCharacters);

public interface IRepositoryDiscoveryService
{
    Task<RepositoryDiscoveryResult> DiscoverAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    async Task<RepositorySnapshotReadResult> ReadSnapshotAsync(
        RepositorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var current = await DiscoverAsync(snapshot.NormalizedRoot, cancellationToken);
        return new RepositorySnapshotReadResult(
            string.Equals(current.Snapshot.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal),
            current.TextFiles);
    }
}

public interface IEvidenceSelectionService
{
    EvidenceSelection Select(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile> textFiles,
        string originalRequirement,
        string approvedRequirementSummary,
        IReadOnlyList<ClarificationAnswer> clarificationAnswers);

    EvidenceSelection SelectForPlanRevision(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile> textFiles,
        string approvedRequirementSummary,
        string correction) =>
        Select(snapshot, textFiles, string.Empty, $"{approvedRequirementSummary} {correction}", []);
}

public sealed class RepositoryDiscoveryException(string category, string safeMessage, Exception? inner = null)
    : Exception(safeMessage, inner)
{
    public string Category { get; } = category;
}
