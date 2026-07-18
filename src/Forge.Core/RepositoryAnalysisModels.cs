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
    IReadOnlyList<string> DeclaredSymbols);

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
    IReadOnlyList<RepositoryFileMetadata> Files);

public sealed record RepositoryTextFile(RepositoryFileMetadata Metadata, string Content);

public sealed record RepositoryDiscoveryResult(
    RepositorySnapshot Snapshot,
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
}

public interface IEvidenceSelectionService
{
    EvidenceSelection Select(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile> textFiles,
        string originalRequirement,
        string approvedRequirementSummary,
        IReadOnlyList<ClarificationAnswer> clarificationAnswers);
}

public sealed class RepositoryDiscoveryException(string category, string safeMessage, Exception? inner = null)
    : Exception(safeMessage, inner)
{
    public string Category { get; } = category;
}
