using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class FakeImplementationEligibilityMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly string[] Extensions =
    [
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".jsonc", ".css", ".scss", ".html", ".htm",
        ".xml", ".csproj", ".props", ".targets", ".config", ".slnx", ".md", ".ps1", ".sh",
        ".yml", ".yaml", ".toml", ".txt", ".sql", ".cmd", ".bat", ".sln", ".json"
    ];

    public static IEnumerable<object[]> AllowedPairs() =>
        Extensions.SelectMany(extension => Enum.GetValues<PlannedFileAction>()
            .Where(action => action != PlannedFileAction.Inspect)
            .Select(action => new object[] { extension, action }));

    [Theory]
    [MemberData(nameof(AllowedPairs))]
    public async Task Every_allowed_extension_action_pair_generates_and_revalidates_structured_output(
        string extension,
        PlannedFileAction action)
    {
        var path = $"src/file{extension}";
        var original = action == PlannedFileAction.Create ? null : Original(extension);
        var originalHash = original is null ? null : ImplementationOutputValidator.Hash(original);
        var evidence = action == PlannedFileAction.Create ? Array.Empty<string>() : ["E1"];
        var plan = new ImplementationPlan("Implement", "Objective", "Understanding",
            [new PlannedFileChange(path, action, "Mechanical fixture.", evidence, .9m)],
            [new ImplementationStep(1, "Apply fixture.", [path], evidence, "Changed.")],
            [], [], [], [], [new RequirementCoverageItem("Apply fixture.", [path], [1])],
            "Summary", PlanningSource.DeterministicFake, null, Now, "fingerprint");
        var contextFile = new ImplementationFileContext(path, action, original, originalHash);

        FakeImplementationCapabilityMatrix.ValidatePlan(plan);
        var evaluation = await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, [contextFile], Now));
        ImplementationOutputValidator.Validate(plan, [contextFile], evaluation.Output, new ImplementationLimits());

        var operation = Assert.Single(evaluation.Output.Operations);
        Assert.Equal(action.ToString(), operation.Action.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"value\"")]
    [InlineData("null")]
    public async Task Every_valid_json_root_shape_remains_supported(string original)
    {
        const string path = "config/settings.json";
        var plan = new ImplementationPlan("Implement", "Objective", "Understanding",
            [new PlannedFileChange(path, PlannedFileAction.Modify, "Mechanical fixture.", ["E1"], .9m)],
            [new ImplementationStep(1, "Apply fixture.", [path], ["E1"], "Changed.")],
            [], [], [], [], [new RequirementCoverageItem("Apply fixture.", [path], [1])],
            "Summary", PlanningSource.DeterministicFake, null, Now, "fingerprint");
        var file = new ImplementationFileContext(path, PlannedFileAction.Modify, original,
            ImplementationOutputValidator.Hash(original));

        var evaluation = await new FakeImplementationEngine().GenerateAsync(
            new ImplementationContext("Approved", plan, [file], Now));
        ImplementationOutputValidator.Validate(plan, [file], evaluation.Output, new ImplementationLimits());
    }

    private static string Original(string extension) => extension switch
    {
        ".json" => "{}\n",
        ".xml" or ".csproj" or ".props" or ".targets" or ".config" or ".slnx" or ".html" or ".htm" => "<root />\n",
        ".css" or ".scss" => "body { color: black; }\n",
        _ => "existing content\n"
    };
}
