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

    [Theory]
    [InlineData("Modify these existing files only:")]
    [InlineData("Modify only these three files:")]
    [InlineData("Only these three existing files may be affected:")]
    [InlineData("Only the following three paths may be affected:")]
    [InlineData("Only the 3 existing paths may be affected:")]
    [InlineData("Exactly these existing paths:")]
    public async Task Bounded_scope_header_variants_create_authoritative_allowlist(string heading)
    {
        var context = Context($"{heading}\n- {ExpectedPaths[0]}\n- {ExpectedPaths[1]}\n- {ExpectedPaths[2]}");

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Equal(ExpectedPaths, plan.AffectedFiles.Select(file => file.Path));
        Assert.DoesNotContain(plan.AffectedFiles, file => file.Path == "ManualTarget.csproj");
    }

    [Theory]
    [InlineData("Consider these three files:")]
    [InlineData("Files may include:")]
    [InlineData("Likely affected files:")]
    public void Ambiguous_scope_headers_are_not_authoritative(string heading)
    {
        var constraints = PlanConstraintPolicy.Derive(Context(
            $"{heading}\n- {ExpectedPaths[0]}\n- {ExpectedPaths[1]}\n- {ExpectedPaths[2]}"));

        Assert.Null(constraints.AuthoritativePaths);
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
    public async Task Test_execution_prohibition_preserves_explicit_test_change_but_omits_test_commands()
    {
        var context = TestContext("""
            Modify only:
            - tests/FooTests.cs
            Do not run tests.
            """);

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        var file = Assert.Single(plan.AffectedFiles);
        Assert.Equal("tests/FooTests.cs", file.Path);
        Assert.Equal(PlannedFileAction.Modify, file.Action);
        Assert.Contains(plan.Steps, step => step.Description.Contains("tests", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.ProposedValidationCommands, command => command.Contains("test", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProposedValidationCommands, command => command.Contains("build", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Test_change_prohibition_rejects_explicit_test_file_mutation()
    {
        var context = TestContext("""
            Modify only:
            - tests/FooTests.cs
            Do not add or update tests.
            """);

        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            new FakePlanningEngine().CreatePlanAsync(context));

        Assert.Equal("plan_constraint_violation", exception.Category);
    }

    [Fact]
    public async Task Explicit_test_file_inspect_remains_allowed_without_test_mutation_step()
    {
        var context = TestContext("""
            Inspect only:
            - tests/FooTests.cs
            Do not add or update tests.
            """);

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.Equal(PlannedFileAction.Inspect, Assert.Single(plan.AffectedFiles).Action);
        Assert.DoesNotContain(plan.Steps, step =>
            step.Description.Contains("add or update focused tests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Production_only_plan_does_not_invent_test_change_step()
    {
        var context = Context($"Modify only:\n- {ExpectedPaths[0]}\n- {ExpectedPaths[1]}\n- {ExpectedPaths[2]}");

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.DoesNotContain(plan.Steps, step =>
            step.Description.Contains("add or update focused tests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Target_project_build_and_test_prohibition_removes_both_commands()
    {
        var context = Context($"Modify only:\n- {ExpectedPaths[0]}\nDo not run the target project's build or tests.");

        var plan = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;

        Assert.DoesNotContain(plan.ProposedValidationCommands, command =>
            command.Contains("build", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("test", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Steps, step =>
            step.Description.Contains("run", StringComparison.OrdinalIgnoreCase));
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

    [Theory]
    [InlineData("title")]
    [InlineData("objective")]
    [InlineData("repository-understanding")]
    [InlineData("summary")]
    [InlineData("affected-purpose")]
    [InlineData("affected-path")]
    [InlineData("step-description")]
    [InlineData("step-result")]
    [InlineData("step-path")]
    [InlineData("coverage")]
    [InlineData("risk")]
    [InlineData("assumption")]
    [InlineData("question")]
    [InlineData("validation")]
    public async Task Excluded_path_is_rejected_from_every_current_plan_text_surface(string field)
    {
        const string excluded = "ManualTarget.csproj";
        var previous = (await new FakePlanningEngine().CreatePlanAsync(
            Context("Update the selected repository files."))).Plan;
        var revision = new PlanRevisionNote($"Exclude {excluded}.", Now, previous.Title,
            previous.RepositoryFingerprint, previous);
        var context = Context("Update the selected repository files.", revision,
            previous.AffectedFiles.Select(file => file.Path).ToArray());
        var valid = (await new FakePlanningEngine().CreatePlanAsync(context)).Plan;
        var candidate = InjectExcludedPath(valid, field, excluded);

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

    private static PlanningContext TestContext(string approvedRequirement)
    {
        var metadata = new RepositoryFileMetadata(
            "tests/FooTests.cs", ".cs", 50, 1, "test", true, "src/Foo.cs", ["FooTests"]);
        var snapshot = Snapshot() with
        {
            Files = [metadata],
            TotalDiscoveredFiles = 1,
            EligibleTextFileCount = 1
        };
        var evidence = new EvidenceItem("ET", metadata.RelativePath, 1, 1, "class FooTests",
            "selected test evidence", 50, "test-hash");
        return new PlanningContext(
            approvedRequirement, approvedRequirement, [], [], snapshot, [evidence], Now);
    }

    private static ImplementationPlan InjectExcludedPath(ImplementationPlan plan, string field, string path) => field switch
    {
        "title" => plan with { Title = $"Review {path}" },
        "objective" => plan with { Objective = $"Review {path}" },
        "repository-understanding" => plan with { RepositoryUnderstanding = $"Review {path}" },
        "summary" => plan with { Summary = $"Review {path}" },
        "affected-purpose" => plan with
        {
            AffectedFiles = [plan.AffectedFiles[0] with { Purpose = $"Review {path}" }, .. plan.AffectedFiles.Skip(1)]
        },
        "affected-path" => plan with
        {
            AffectedFiles = [plan.AffectedFiles[0] with { Path = path }, .. plan.AffectedFiles.Skip(1)]
        },
        "step-description" => plan with
        {
            Steps = [plan.Steps[0] with { Description = $"Review {path}" }, .. plan.Steps.Skip(1)]
        },
        "step-result" => plan with
        {
            Steps = [plan.Steps[0] with { ExpectedResult = $"Review {path}" }, .. plan.Steps.Skip(1)]
        },
        "step-path" => plan with
        {
            Steps = [plan.Steps[0] with { AffectedPaths = [path] }, .. plan.Steps.Skip(1)]
        },
        "coverage" => plan with
        {
            RequirementCoverage = [plan.RequirementCoverage[0] with { Requirement = $"Review {path}" }]
        },
        "risk" => plan with { Risks = [$"Review {path}"] },
        "assumption" => plan with { Assumptions = [$"Review {path}"] },
        "question" => plan with { UnresolvedQuestions = [$"Review {path}"] },
        "validation" => plan with { ProposedValidationCommands = [$"review {path}"] },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
    };

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
