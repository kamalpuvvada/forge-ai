using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Mechanical manual-verification planner. It performs no AI reasoning and executes no command.</summary>
public sealed class FakeVerificationPlanEngine : IVerificationPlanEngine
{
    public Task<VerificationPlanEvaluation> GenerateAsync(
        VerificationPlanContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        var cases = new List<VerificationTestCaseCandidate>();
        foreach (var command in context.ApprovedValidationCommands.Take(11))
        {
            var category = Category(command.Command);
            cases.Add(new VerificationTestCaseCandidate(
                cases.Count + 1,
                $"Manually perform approved {Label(category)} verification",
                "Record the observed outcome of one approved validation instruction without granting Forge execution authority.",
                category,
                true,
                ["Open the approved isolated implementation revision in the developer's normal local tooling."],
                [],
                [new VerificationTestStepCandidate(
                    1,
                    "Run the referenced approved validation command manually outside Forge and record the observed result.",
                    command.Id,
                    "The user records whether the command behavior matches the approved implementation plan.")],
                "The user reports an acceptable result and supplies any plan-required evidence description.",
                [],
                ["Recheck affected behavior described by the approved implementation plan."],
                ["Record a concise description of the observed command result."],
                ["Forge does not execute this command or independently verify the reported outcome."],
                null,
                []));
        }

        if (cases.Count < 12)
        {
            var changedFiles = context.ImplementationResult.ChangedFiles
                .OrderBy(file => file.Path, RepositoryPathRules.Comparer).ToArray();
            var included = changedFiles.Take(changedFiles.Length > 10 ? 9 : 10).ToArray();
            var steps = included.Select((file, index) => new VerificationTestStepCandidate(
                index + 1,
                $"Manually inspect the {file.Action.ToString().ToLowerInvariant()} behavior for `{file.Path}`.",
                null,
                $"The user records whether `{file.Path}` matches the approved requirement and plan."))
                .ToList();
            var omitted = changedFiles.Length - included.Length;
            if (omitted > 0)
                steps.Add(new VerificationTestStepCandidate(steps.Count + 1,
                    $"Record that {omitted} additional approved changed file(s) were omitted from this bounded case.",
                    null,
                    "The omission count is visible and no unsupported path is invented."));
            if (steps.Count == 0)
                steps.Add(new VerificationTestStepCandidate(1,
                    "Manually review the bounded approved implementation metadata.", null,
                    "The user records whether the approved implementation behavior matches the requirement and plan."));
            cases.Add(new VerificationTestCaseCandidate(
                cases.Count + 1,
                "Review the approved changed-file behavior",
                "Manually inspect the approved implementation behavior represented by the bounded changed-file review.",
                VerificationTestCategory.ManualBehavior,
                true,
                ["Use the exact approved implementation revision identified by this verification plan."],
                [],
                steps,
                "The user reports whether the approved implementation behaves as expected.",
                [],
                included.Select(file => $"Recheck behavior affected by `{file.Path}`.").Take(6).ToArray(),
                ["Record a concise observation; reference local evidence by safe name when useful."],
                ["This is a mechanical checklist item, not AI-authored repository analysis."],
                null,
                []));
        }

        var omittedEvidence = Math.Max(0,
            context.RepositoryEvidenceFilesInspected - context.RepositoryEvidenceFilesSelected);
        var truncatedDiffs = context.ImplementationResult.ChangedFiles.Count(file => file.DiffTruncated);
        var boundedChangedFileOmissions = context.ImplementationResult.ChangedFiles.Count > 10
            ? context.ImplementationResult.ChangedFiles.Count - 9
            : 0;
        var limitationNotices = new List<string>();
        var omittedCommands = Math.Max(0, context.ApprovedValidationCommands.Count - 11);
        if (omittedCommands > 0)
            limitationNotices.Add($"{omittedCommands} approved validation command(s) were omitted from this bounded mechanical plan.");
        if (boundedChangedFileOmissions > 0)
            limitationNotices.Add($"{boundedChangedFileOmissions} approved changed file(s) were omitted from the bounded changed-file checklist.");
        if (truncatedDiffs > 0)
            limitationNotices.Add($"Diff previews were truncated for {truncatedDiffs} approved changed file(s); complete diff evidence was not supplied here.");
        if (omittedEvidence > 0)
            limitationNotices.Add($"{omittedEvidence} inspected repository file(s) were not selected for the bounded evidence set.");
        else
            limitationNotices.Add($"The bounded repository evidence set contains {context.RepositoryEvidence.Count} item(s); it does not imply complete repository evidence.");
        limitationNotices.Add("Deterministic Fake guidance is mechanical and does not provide AI reasoning.");
        limitationNotices.Add("Forge does not execute or independently verify manual results.");
        var warnings = context.ImplementationResult.Warnings.Distinct(StringComparer.Ordinal).ToArray();
        var warningSlots = Math.Max(0, 8 - limitationNotices.Count);
        if (warnings.Length > warningSlots)
        {
            var includedWarnings = Math.Max(0, warningSlots - 1);
            limitationNotices.AddRange(warnings.Take(includedWarnings));
            limitationNotices.Add($"{warnings.Length - includedWarnings} implementation warning(s) were omitted from this bounded plan.");
        }
        else limitationNotices.AddRange(warnings);
        var candidate = new VerificationPlanCandidate(
            context.ContextFingerprint,
            "Mechanical Fake-mode manual verification guidance derived from approved commands and changed-file metadata.",
            "Verify only the exact approved implementation revision; Forge executes no validation command.",
            ["Use the exact approved implementation revision and record outcomes as user-reported evidence."],
            cases,
            context.ApprovedPlan.Risks.Take(8).ToArray(),
            limitationNotices.Take(8).ToArray(),
            ["Describe observations without secrets, credentials, connection strings, or private absolute paths."],
            VerificationPlanSource.DeterministicFake,
            null,
            null);
        return Task.FromResult(new VerificationPlanEvaluation(candidate));
    }

    private static VerificationTestCategory Category(string command)
    {
        if (command.Contains("lint", StringComparison.OrdinalIgnoreCase)) return VerificationTestCategory.LintOrStaticAnalysis;
        if (command.Contains("test", StringComparison.OrdinalIgnoreCase)) return VerificationTestCategory.UnitTest;
        if (command.Contains("build", StringComparison.OrdinalIgnoreCase) || command.Contains("restore", StringComparison.OrdinalIgnoreCase))
            return VerificationTestCategory.Build;
        return VerificationTestCategory.Other;
    }

    private static string Label(VerificationTestCategory category) => category switch
    {
        VerificationTestCategory.LintOrStaticAnalysis => "lint/static-analysis",
        VerificationTestCategory.UnitTest => "test",
        VerificationTestCategory.Build => "build",
        _ => "manual"
    };
}
