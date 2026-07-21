using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class GitHubCliClientTests
{
    [Fact]
    public async Task Authentication_and_pull_request_use_bounded_arguments_stdin_and_never_request_a_token()
    {
        var runner = new FakeRunner();
        var client = new GitHubCliClient(runner);
        var proposal = Proposal();
        await client.EnsureAuthenticatedAsync();
        var result = await client.CreatePullRequestAsync(proposal);

        Assert.Equal(23, result.Number);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(["auth", "status", "--hostname", "github.com"]));
        var create = runner.Calls.Single(call => call.Arguments.Contains("create"));
        Assert.Equal(proposal.PullRequestBody, create.Input);
        Assert.Contains('—', create.Input!);
        Assert.Contains('…', create.Input!);
        Assert.Contains("--body-file", create.Arguments);
        Assert.Contains("-", create.Arguments);
        Assert.DoesNotContain(runner.Calls.SelectMany(call => call.Arguments), value =>
            value.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Multiple_or_conflicting_pull_requests_remain_visible_for_safe_stop_reconciliation()
    {
        var runner = new FakeRunner { ListTwo = true };
        var results = await new GitHubCliClient(runner).FindPullRequestsAsync(Proposal());
        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.Equal("OPEN", item.State));
    }

    [Fact]
    public async Task Authentication_failure_is_safe_and_never_asks_gh_for_a_token()
    {
        var runner = new FakeRunner { AuthenticationUnavailable = true };

        var exception = await Assert.ThrowsAsync<DeliveryException>(() =>
            new GitHubCliClient(runner).EnsureAuthenticatedAsync());

        Assert.Equal("delivery_authentication_unavailable", exception.Category);
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(runner.Calls.SelectMany(call => call.Arguments), value =>
            value.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    private static DeliveryProposal Proposal()
    {
        var value = new DeliveryProposal(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), new string('a', 64),
            Guid.NewGuid(), new string('b', 64), Guid.NewGuid(), new string('c', 64), new string('d', 40),
            "origin", "acme", "widget", "main", new string('d', 40), "forge-delivery-a1b2c3d4-r1",
            "forge: deliver task a1b2c3d4 revision 1", "Forge AI: bounded change",
            "Manual verification passed — user reported\nRequirement: bounded spec…", ["src/App.cs"],
            new string('e', 64), DateTimeOffset.UtcNow, DeliveryProposalStatus.Approved, DateTimeOffset.UtcNow,
            Guid.NewGuid(), 1);
        return value;
    }

    private sealed class FakeRunner : IDeliveryProcessRunner
    {
        internal sealed record Call(string Executable, IReadOnlyList<string> Arguments, string? Input);
        internal List<Call> Calls { get; } = [];
        internal bool ListTwo { get; init; }
        internal bool AuthenticationUnavailable { get; init; }
        public Task<DeliveryProcessResult> RunAsync(string executable, string workingDirectory,
            IReadOnlyList<string> arguments, string? standardInput = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(executable, arguments.ToArray(), standardInput));
            if (arguments.SequenceEqual(["auth", "status", "--hostname", "github.com"]))
                return Task.FromResult(new DeliveryProcessResult(AuthenticationUnavailable ? 1 : 0,
                    string.Empty, "redacted fixture"));
            if (arguments.Contains("create"))
                return Task.FromResult(new DeliveryProcessResult(0, "https://github.com/acme/widget/pull/23\n", string.Empty));
            var body = "Manual verification passed — user reported\\nRequirement: bounded spec…";
            var one = $"{{\"number\":23,\"url\":\"https://github.com/acme/widget/pull/23\",\"state\":\"OPEN\",\"headRefName\":\"forge-delivery-a1b2c3d4-r1\",\"headRefOid\":\"{new string('d', 40)}\",\"baseRefName\":\"main\",\"title\":\"Forge AI: bounded change\",\"body\":\"{body}\",\"mergedAt\":null,\"commits\":[{{}}],\"files\":[{{\"path\":\"src/App.cs\"}}]}}";
            if (arguments.Contains("list") && ListTwo)
                return Task.FromResult(new DeliveryProcessResult(0, $"[{one},{one.Replace("\"number\":23", "\"number\":22", StringComparison.Ordinal)}]", string.Empty));
            return Task.FromResult(new DeliveryProcessResult(0, arguments.Contains("view") ? one : $"[{one}]", string.Empty));
        }
    }
}
