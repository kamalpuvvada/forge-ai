using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class PlanConstraintPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fake_plan_uses_explicit_only_allowlist_without_promoting_unrelated_evidence()
    {
        var context = Context(ApprovedRequirement());

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Equal(4, context.Evidence.Count);
        Assert.Equal(ExpectedPaths, plan.AffectedFiles.Select(file => file.Path));
        Assert.All(plan.AffectedFiles, file => Assert.Equal(PlannedFileAction.Modify, file.Action));
        Assert.DoesNotContain(plan.AffectedFiles, file => file.Path == "ManualTarget.csproj");
        Assert.Equal(ExpectedPaths, Assert.Single(plan.RequirementCoverage).AffectedPaths);
    }

    [Fact]
    public async Task Fake_revision_replaces_previous_scope_and_removes_tests_and_repository_commands()
    {
        var initialContext = Context("Update the selected repository files.");
        var previous = (await new FakePlanningEngine().CreatePlanAsync(initialContext)).Plan;
        Assert.Contains(previous.AffectedFiles, file => file.Path == "ManualTarget.csproj");
        Assert.NotEmpty(previous.ProposedValidationCommands);
        var correction = """
            Only the three named files may be affected.
            Remove ManualTarget.csproj everywhere.
            Do not add or update tests.
            Do not propose or run build, test, lint, restore or repository validation commands.
            Exactly three Modify actions. No Create, Delete, or Inspect actions.
            """;
        var revision = new PlanRevisionNote(correction, Now, previous.Title, previous.RepositoryFingerprint, previous);
        var context = Context(ApprovedRequirement(), revision, previous.AffectedFiles.Select(file => file.Path).ToArray());

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Equal(ExpectedPaths, plan.AffectedFiles.Select(file => file.Path));
        Assert.All(plan.AffectedFiles, file => Assert.Equal(PlannedFileAction.Modify, file.Action));
        Assert.Empty(plan.ProposedValidationCommands);
        Assert.DoesNotContain(plan.Steps, step => step.Description.Contains("tests", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Steps, step => step.Description.Contains("run", StringComparison.OrdinalIgnoreCase));
        var review = Assert.Single(plan.Steps, step => step.Description.Contains("bounded diff", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("file list", review.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hashes", review.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("byte and line counts", review.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active checkout remains unchanged", review.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PlanText(plan), value => value.Contains("ManualTarget.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Latest_correction_explicit_allowlist_replaces_approved_allowlist()
    {
        var initial = Context(ApprovedRequirement());
        var previous = (await new FakePlanningEngine().CreatePlanAsync(initial)).Plan;
        var correction = """
            Modify these files only:
            - config/settings.json
            - README.md
            """;
        var revision = new PlanRevisionNote(correction, Now, previous.Title, previous.RepositoryFingerprint, previous);
        var context = Context(ApprovedRequirement(), revision, previous.AffectedFiles.Select(file => file.Path).ToArray());

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Equal(new[] { "config/settings.json", "README.md" },
            plan.AffectedFiles.Select(file => file.Path));
        Assert.DoesNotContain(plan.AffectedFiles, file => file.Path == "src/GreetingService.cs");
    }

    [Fact]
    public async Task Test_execution_prohibition_omits_test_changes_and_test_commands()
    {
        var context = Context($"""
            Modify only:
            - {ExpectedPaths[0]}
            - {ExpectedPaths[1]}
            - {ExpectedPaths[2]}
            Do not run tests.
            """);

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.DoesNotContain(plan.Steps, step => step.Description.Contains("tests", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.ProposedValidationCommands, command => command.Contains("test", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProposedValidationCommands, command => command.Contains("build", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Diff_review_only_directive_produces_no_commands_and_truthful_bounded_review()
    {
        var context = Context($"""
            Modify only:
            - {ExpectedPaths[0]}
            - {ExpectedPaths[1]}
            - {ExpectedPaths[2]}
            Validation is limited to reviewing Forge diff metadata.
            """);

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Empty(plan.ProposedValidationCommands);
        Assert.Contains(plan.Steps, step =>
            step.Description.Contains("bounded diff previews", StringComparison.OrdinalIgnoreCase) &&
            step.Description.Contains("active checkout remains unchanged", StringComparison.OrdinalIgnoreCase));
        ImplementationPlanValidator.Validate(plan, context.Snapshot, context.Evidence);
    }

    [Fact]
    public async Task Normalized_path_matching_is_case_insensitive()
    {
        var context = Context("""
            Modify only:
            - SRC\GreetingService.cs
            - CONFIG\settings.json
            - readme.md
            """);

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Equal(3, plan.AffectedFiles.Count);
        Assert.Contains(plan.AffectedFiles, file => RepositoryPathRules.Comparer.Equals(file.Path, "src/GreetingService.cs"));
        Assert.All(plan.AffectedFiles, file => Assert.Equal(PlannedFileAction.Modify, file.Action));
    }

    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("C:\\Temp\\secret.cs")]
    [InlineData("/tmp/secret.cs")]
    public async Task Unsafe_explicit_scope_fails_safely(string unsafePath)
    {
        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            new FakePlanningEngine().CreatePlanAsync(Context($"Modify only:\n- {unsafePath}")));

        Assert.Equal("plan_constraint_violation", exception.Category);
        Assert.Equal(PlanConstraintPolicy.UnsafeConstraintMessage, exception.Message);
        Assert.DoesNotContain(unsafePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_existing_path_without_direct_evidence_fails_closed()
    {
        var context = Context(ApprovedRequirement()) with
        {
            Evidence = Evidence().Where(item => item.RelativePath != "config/settings.json").ToArray()
        };

        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            new FakePlanningEngine().CreatePlanAsync(context));

        Assert.Equal("plan_constraint_violation", exception.Category);
        Assert.Equal(PlanConstraintPolicy.MissingConstraintEvidenceMessage, exception.Message);
    }

    [Fact]
    public async Task Excluding_every_authoritative_path_fails_closed_without_evidence_fallback()
    {
        var context = Context("""
            Modify only:
            - src/GreetingService.cs
            Exclude src/GreetingService.cs.
            """);

        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            new FakePlanningEngine().CreatePlanAsync(context));

        Assert.Equal("plan_constraint_violation", exception.Category);
        Assert.Equal(PlanConstraintPolicy.ConstraintViolationMessage, exception.Message);
    }

    [Fact]
    public async Task Ambiguous_file_mentions_do_not_become_hard_scope_constraints()
    {
        var context = Context("Consider files such as src/GreetingService.cs while deciding the implementation scope.");

        var constraints = PlanConstraintPolicy.Derive(context);
        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Null(constraints.AuthoritativePaths);
        Assert.Equal(4, plan.AffectedFiles.Count);
    }

    [Fact]
    public async Task Structural_revision_that_changes_only_wording_fails_safely()
    {
        var initial = Context(ApprovedRequirement());
        var previous = (await new FakePlanningEngine().CreatePlanAsync(initial)).Plan;
        var correction = "Only the three named files may be affected.";
        var revision = new PlanRevisionNote(correction, Now, previous.Title, previous.RepositoryFingerprint, previous);
        var revised = Context(ApprovedRequirement(), revision, previous.AffectedFiles.Select(file => file.Path).ToArray());

        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            new FakePlanningEngine().CreatePlanAsync(revised));

        Assert.Equal("plan_revision_no_change", exception.Category);
        Assert.Equal(PlanConstraintPolicy.NoOpRevisionMessage, exception.Message);
    }

    [Fact]
    public async Task Candidate_trust_gate_rejects_excluded_path_outside_affected_files()
    {
        var previous = (await new FakePlanningEngine().CreatePlanAsync(
            Context("Update the selected repository files."))).Plan;
        var correction = "Exclude ManualTarget.csproj.";
        var revision = new PlanRevisionNote(correction, Now, previous.Title, previous.RepositoryFingerprint, previous);
        var context = Context("Update the selected repository files.", revision,
            previous.AffectedFiles.Select(file => file.Path).ToArray());
        var candidate = previous with
        {
            AffectedFiles = previous.AffectedFiles.Where(file => file.Path != "ManualTarget.csproj").ToArray(),
            Steps = previous.Steps.Select(step => step with
            {
                AffectedPaths = step.AffectedPaths.Where(path => path != "ManualTarget.csproj").ToArray(),
                Description = step.Order == 1 ? "Exclude ManualTarget.csproj from the proposed scope." : step.Description
            }).ToArray(),
            RequirementCoverage = previous.RequirementCoverage.Select(item => item with
            {
                AffectedPaths = item.AffectedPaths.Where(path => path != "ManualTarget.csproj").ToArray()
            }).ToArray()
        };

        var exception = Assert.Throws<PlanningException>(() =>
            PlanConstraintPolicy.ValidateCandidate(candidate, context));

        Assert.Equal("plan_constraint_violation", exception.Category);
        Assert.Equal(PlanConstraintPolicy.ConstraintViolationMessage, exception.Message);
    }

    private static PlanningContext Context(
        string approvedRequirement,
        PlanRevisionNote? revision = null,
        IReadOnlyList<string>? previousPaths = null) =>
        new(
            approvedRequirement,
            approvedRequirement,
            [],
            [],
            Snapshot(),
            Evidence(),
            Now,
            revision,
            previousPaths);

    private static string ApprovedRequirement() => """
        Modify only:
        - src/GreetingService.cs
        - config/settings.json
        - README.md
        Do not add or update tests.
        Do not propose or run builds, tests, lint, restore or target-repository validation commands.
        """;

    private static readonly string[] ExpectedPaths =
        ["src/GreetingService.cs", "config/settings.json", "README.md"];

    private static RepositorySnapshot Snapshot()
    {
        var files = new[]
        {
            Metadata("src/GreetingService.cs", ".cs", "source"),
            Metadata("config/settings.json", ".json", "configuration"),
            Metadata("README.md", ".md", "documentation"),
            Metadata("ManualTarget.csproj", ".csproj", "project manifest")
        };
        return new RepositorySnapshot(
            "C:/safe-repository", true, "main", "1234567", new string('1', 40), "clean",
            files.Length, files.Length, 0, ["C#/.NET"], [".cs", ".json", ".md", ".csproj"],
            ["ManualTarget.slnx"], [], [], Now, "constraint-fingerprint", files);
    }

    private static IReadOnlyList<EvidenceItem> Evidence() =>
        Snapshot().Files.Select((file, index) => new EvidenceItem(
            $"E{index + 1}", file.RelativePath, 1, 1, $"content for {file.RelativePath}",
            "selected fixture evidence", 50, $"hash-{index + 1}")).ToArray();

    private static RepositoryFileMetadata Metadata(string path, string extension, string role) =>
        new(path, extension, 50, 1, role, false, null, [Path.GetFileNameWithoutExtension(path)]);

    private static IEnumerable<string> PlanText(ImplementationPlan plan) =>
        plan.AffectedFiles.SelectMany(file => new[] { file.Path, file.Purpose })
            .Concat(plan.Steps.SelectMany(step => new[] { step.Description, step.ExpectedResult }.Concat(step.AffectedPaths)))
            .Concat(plan.RequirementCoverage.SelectMany(item => new[] { item.Requirement }.Concat(item.AffectedPaths)))
            .Concat(plan.ProposedValidationCommands)
            .Concat(plan.Risks)
            .Concat(plan.Assumptions)
            .Concat(plan.UnresolvedQuestions);
}
