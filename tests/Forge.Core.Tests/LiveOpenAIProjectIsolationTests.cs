using Forge.Testing;

namespace Forge.Core.Tests;

public sealed class LiveOpenAIProjectIsolationTests
{
    [Fact]
    public void Live_project_is_physically_excluded_from_solution_and_standard_test_assembly()
    {
        var root = RepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(root, "ForgeAI.slnx"));
        Assert.DoesNotContain("Forge.LiveOpenAI.Tests", solution, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(LiveOpenAIProjectIsolationTests).Assembly.GetTypes(),
            type => type.Name == "LiveOpenAIImplementationSmokeTests");

        var project = File.ReadAllText(Path.Combine(root, "tests", "Forge.LiveOpenAI.Tests",
            "Forge.LiveOpenAI.Tests.csproj"));
        Assert.Contains("BeforeTargets=\"VSTest\"", project, StringComparison.Ordinal);
        Assert.Contains("$(VSTestTestCaseFilter)", project, StringComparison.Ordinal);
        Assert.Contains("Category=LiveOpenAIImplementation", project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "key")]
    [InlineData("true", "true", null)]
    [InlineData("true", null, "key")]
    [InlineData(null, "true", "key")]
    [InlineData("false", "true", "key")]
    public void Missing_any_environment_gate_is_ineligible(string? enabled, string? filter, string? key)
    {
        Assert.False(LiveOpenAITestGate.IsEligible(enabled, filter, key));
    }

    [Fact]
    public void All_environment_gates_make_the_dedicated_filtered_invocation_eligible()
    {
        Assert.True(LiveOpenAITestGate.IsEligible("true", "true", "environment-only-key"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ForgeAI.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
