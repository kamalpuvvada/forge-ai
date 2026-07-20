using System.Diagnostics;
using Forge.Core;
using Forge.Infrastructure;
using Forge.Testing;
using Microsoft.Data.Sqlite;

namespace Forge.LiveOpenAI.Tests;

public sealed class LiveOpenAIImplementationSmokeTests
{
    [LiveOpenAIImplementationFact]
    [Trait("Category", "LiveOpenAIImplementation")]
    public async Task Disposable_repository_structured_implementation_smoke()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ??
                     throw new InvalidOperationException("The gated live smoke test requires OPENAI_API_KEY.");
        var temporaryParent = Path.Combine(Path.GetTempPath(), "forge-live-openai-tests");
        var root = Path.Combine(temporaryParent, $"forge-live-openai-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var database = Path.Combine(root, "forge-smoke.db");
        await LiveSmokeCleanupCoordinator.ExecuteAsync(root, async () =>
        {
            Directory.CreateDirectory(Path.Combine(repository, "src"));
            const string path = "src/Greeting.cs";
            const string original = "public static class Greeting { public static string Value => \"hello\"; }\n";
            await File.WriteAllTextAsync(Path.Combine(repository, "src", "Greeting.cs"), original);
            await GitAsync(repository, "init");
            await GitAsync(repository, "config", "user.email", "forge-live-smoke@example.invalid");
            await GitAsync(repository, "config", "user.name", "Forge Live Smoke");
            await GitAsync(repository, "config", "core.autocrlf", "false");
            await GitAsync(repository, "add", ".");
            await GitAsync(repository, "commit", "-m", "smoke baseline");
            var baseSha = (await GitAsync(repository, "rev-parse", "HEAD")).Trim();
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Smoke (Id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            var plan = new ImplementationPlan("Update greeting", "Return a friendly greeting.",
                "The approved source contains one greeting class.",
                [new PlannedFileChange(path, PlannedFileAction.Modify, "Change the greeting value.", [], .9m)],
                [new ImplementationStep(1, "Change the greeting.", [path], [], "The replacement returns hello world.")],
                [], [], [], [], [new RequirementCoverageItem("Return hello world.", [path], [1])],
                "Update the one approved greeting source.", PlanningSource.DeterministicFake, null,
                DateTimeOffset.UtcNow, new string('a', 64));
            var planFingerprint = ImplementationReviewFingerprint.ComputePlan(plan);
            var hash = ImplementationOutputValidator.Hash(original);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(original);
            var file = new ImplementationFileContext(path, PlannedFileAction.Modify, original, hash, bytes,
                ImplementationContextIdentity.ComputeSource(baseSha, planFingerprint, path,
                    PlannedFileAction.Modify, hash, bytes));
            var context = new ImplementationContext("Change the approved greeting to return hello world.",
                plan, [file], DateTimeOffset.UtcNow, planFingerprint, baseSha, [], [], 0);
            context = context with { ContextFingerprint = ImplementationContextIdentity.ComputeGlobal(context) };
            var options = new ForgeAiOptions { Mode = ForgeAiModes.OpenAI };
            var engine = new OpenAIImplementationEngine(options, new SdkOpenAIResponsesGateway(apiKey),
                new ModelCostCalculator(options.Pricing), TimeProvider.System);

            var evaluation = await engine.GenerateAsync(context);

            var operation = Assert.Single(evaluation.Output.Operations);
            Assert.Equal(path, operation.Path);
            Assert.Equal(ImplementationOperationAction.Modify, operation.Action);
            Assert.Contains("hello world", operation.Content!, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(evaluation.ModelCalls);
            Assert.Equal(string.Empty, await GitAsync(repository, "status", "--porcelain=v1", "--untracked-files=all"));
        }, () =>
        {
            SqliteConnection.ClearAllPools();
            DisposableTestDirectoryCleanup.Delete(root, temporaryParent);
        }, message => Console.Error.WriteLine(message));
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!process.Start())
            throw new InvalidOperationException("Git could not start for the disposable live smoke repository.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Disposable smoke Git step failed with exit code {process.ExitCode}: {error}");
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}

public sealed class LiveOpenAIImplementationFactAttribute : FactAttribute
{
    public LiveOpenAIImplementationFactAttribute()
    {
        if (!LiveOpenAITestGate.IsEligible(
                Environment.GetEnvironmentVariable("FORGE_ENABLE_LIVE_OPENAI_TEST"),
                Environment.GetEnvironmentVariable("FORGE_LIVE_TEST_EXPLICIT_FILTER"),
                Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            Skip = "Live OpenAI implementation smoke testing requires both explicit gates and OPENAI_API_KEY.";
    }
}
