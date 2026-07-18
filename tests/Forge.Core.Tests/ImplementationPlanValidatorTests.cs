using Forge.Core;

namespace Forge.Core.Tests;

public sealed class ImplementationPlanValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("All tests passed.")]
    [InlineData("The tests have passed.")]
    [InlineData("The build succeeded.")]
    [InlineData("The build was successful.")]
    [InlineData("Validation completed successfully.")]
    [InlineData("The command ran successfully.")]
    [InlineData("The PDF was manually verified.")]
    [InlineData("The result has been validated.")]
    [InlineData("The PDF has been manually verified.")]
    [InlineData("The tests have been successful.")]
    [InlineData("No errors were found.")]
    [InlineData("Lint is passing.")]
    [InlineData("Tests are currently passing.")]
    [InlineData("tEsTs HaVe PaSsEd!")]
    [InlineData("Status: the build succeeded; implementation remains unchanged.")]
    [InlineData("The scope is bounded. Validation completed successfully. Review can continue.")]
    public void Narrative_fields_reject_claims_that_validation_already_ran(string claim)
    {
        var exception = Assert.Throws<PlanningException>(() => Validate(ValidPlan() with { Summary = claim }));

        Assert.Equal("invalid_plan", exception.Category);
        Assert.Equal(ImplementationPlanValidator.ValidationAlreadyPerformedMessage, exception.Message);
    }

    [Theory]
    [InlineData("All tests must pass.")]
    [InlineData("All tests should pass.")]
    [InlineData("Run dotnet test.")]
    [InlineData("Confirm that the tests pass.")]
    [InlineData("Expected result: the build succeeds.")]
    [InlineData("Success criterion: lint passes.")]
    [InlineData("Ensure the export completes successfully.")]
    [InlineData("The PDF must be manually verified.")]
    [InlineData("Validation should confirm that no errors occur.")]
    [InlineData("The implementation is expected to pass existing tests.")]
    [InlineData("The build should succeed.")]
    [InlineData("The build is expected to be successful.")]
    [InlineData("Manually verify the PDF.")]
    public void Narrative_fields_allow_future_required_expected_and_imperative_language(string criterion)
    {
        Validate(ValidPlan() with { Summary = criterion });
    }

    [Fact]
    public void Proposed_commands_allow_success_criteria_but_reject_unmistakable_history()
    {
        Validate(ValidPlan() with { ProposedValidationCommands = ["Run dotnet test and confirm that all tests pass."] });

        var exception = Assert.Throws<PlanningException>(() =>
            Validate(ValidPlan() with { ProposedValidationCommands = ["Tests were run and passed."] }));

        Assert.Equal(ImplementationPlanValidator.ValidationAlreadyPerformedMessage, exception.Message);
    }

    [Fact]
    public void Expected_results_allow_outcomes_but_reject_unmistakable_history()
    {
        var plan = ValidPlan();
        Validate(plan with
        {
            Steps = [plan.Steps[0] with { ExpectedResult = "Expected result: the build succeeds and lint is passing." }]
        });

        var exception = Assert.Throws<PlanningException>(() => Validate(plan with
        {
            Steps = [plan.Steps[0] with { ExpectedResult = "The build has already succeeded." }]
        }));

        Assert.Equal(ImplementationPlanValidator.ValidationAlreadyPerformedMessage, exception.Message);
    }

    [Fact]
    public void Repository_understanding_and_summary_use_strict_completed_run_detection()
    {
        var plan = ValidPlan();
        Assert.Throws<PlanningException>(() => Validate(plan with { RepositoryUnderstanding = "Evidence E1 is selected. Lint is passing." }));
        Assert.Throws<PlanningException>(() => Validate(plan with { Summary = "The build was successful." }));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("objective")]
    [InlineData("affected-purpose")]
    [InlineData("step-description")]
    [InlineData("risk")]
    [InlineData("assumption")]
    [InlineData("unresolved-question")]
    public void Every_narrative_field_rejects_completed_validation_claims(string field)
    {
        var plan = ValidPlan();
        var invalid = field switch
        {
            "title" => plan with { Title = "Validation completed successfully." },
            "objective" => plan with { Objective = "The build succeeded." },
            "affected-purpose" => plan with
            {
                AffectedFiles = [plan.AffectedFiles[0] with { Purpose = "The PDF was manually verified." }]
            },
            "step-description" => plan with
            {
                Steps = [plan.Steps[0] with { Description = "Tests are currently passing." }]
            },
            "risk" => plan with { Risks = ["No errors were found."] },
            "assumption" => plan with { Assumptions = ["The tests have passed."] },
            "unresolved-question" => plan with { UnresolvedQuestions = ["The command ran successfully."] },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        var exception = Assert.Throws<PlanningException>(() => Validate(invalid));
        Assert.Equal(ImplementationPlanValidator.ValidationAlreadyPerformedMessage, exception.Message);
    }

    [Fact]
    public void Imperative_step_descriptions_remain_valid()
    {
        var plan = ValidPlan();
        Validate(plan with
        {
            Steps = [plan.Steps[0] with { Description = "Run the tests and manually verify the PDF." }]
        });
    }

    private static void Validate(ImplementationPlan plan) =>
        ImplementationPlanValidator.Validate(plan, Snapshot(), Evidence());

    private static ImplementationPlan ValidPlan() => new(
        "Plan export",
        "Add bounded export behavior.",
        "Evidence E1 identifies the application surface.",
        [new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Add the export behavior.", ["E1"], 0.9m)],
        [new ImplementationStep(1, "Add the export behavior.", ["src/App.cs"], ["E1"], "The export should be available.")],
        ["dotnet test ForgeAI.slnx"],
        ["Output size may need a bound."],
        ["The existing API shape remains stable."],
        [],
        "A focused implementation plan.",
        PlanningSource.OpenAI,
        "gpt-5.6-sol",
        Now,
        "fingerprint");

    private static RepositorySnapshot Snapshot() => new(
        @"C:\private\target-repository", true, "main", "abc1234", "abc123456789", "clean",
        1, 1, 0, ["C#/.NET"], [".cs"], ["ForgeAI.slnx"], ["tests"], [], Now, "fingerprint",
        [new RepositoryFileMetadata("src/App.cs", ".cs", 200, 10, "source", false, null, ["App"])]);

    private static IReadOnlyList<EvidenceItem> Evidence() =>
        [new EvidenceItem("E1", "src/App.cs", 1, 10, "public class App { }", "application surface", 40, "hash")];
}
