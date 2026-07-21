using System.Text;
using System.Text.Json;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class OpenAIVerificationPlanEngineTests
{
    [Fact]
    public void Verification_configuration_and_strict_schema_are_bounded()
    {
        var options = new ForgeAiOptions();
        Assert.Equal("gpt-5.6-sol", options.VerificationPlanningModel);
        Assert.Equal("medium", options.VerificationPlanningReasoningEffort);
        Assert.Equal(8_000, options.VerificationPlanningMaxOutputTokens);
        options.ValidateSyntax();
        using var schema = JsonDocument.Parse(OpenAIVerificationPlanEngine.ResponseSchema);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(12, schema.RootElement.GetProperty("properties").GetProperty("testCases").GetProperty("maxItems").GetInt32());
        Assert.Contains("Never claim", OpenAIVerificationPlanEngine.DeveloperInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_context_is_bounded_and_excludes_private_execution_identity()
    {
        var context = Context();
        var canonical = OpenAIVerificationPlanEngine.BuildCanonicalContext(context);
        Assert.True(Encoding.UTF8.GetByteCount(canonical) <= OpenAIVerificationPlanEngine.MaximumCanonicalContextBytes);
        Assert.DoesNotContain("C:/repo", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceToken", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerRef", canonical, StringComparison.OrdinalIgnoreCase);
        using var parsed = JsonDocument.Parse(canonical);
        Assert.Equal(VerificationTrustLabels.ManualNotExecuted,
            parsed.RootElement.GetProperty("trustBoundary").GetString());
    }

    [Fact]
    public void Duplicate_properties_are_rejected_before_deserialization()
    {
        var context = Context();
        var json = ValidJson(context).Replace("\"summary\":", "\"summary\":\"first\",\"summary\":", StringComparison.Ordinal);
        var exception = Assert.Throws<VerificationException>(() => OpenAIVerificationPlanEngine.Parse(json));
        Assert.Equal("verification_invalid_structured_output", exception.Category);
    }

    [Fact]
    public void Complete_valid_wire_output_is_accepted_for_local_validation()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        {
            Model = "gpt-5.6-sol",
            ReasoningEffort = "medium"
        };
        var plan = VerificationValidator.FinalizeCandidate(context, candidate, 1, Guid.NewGuid(), [], new VerificationLimits());
        Assert.Single(plan.TestCases);
        Assert.Equal(context.ContextFingerprint, plan.GenerationContextFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", plan.PlanFingerprint);
    }

    [Theory]
    [InlineData("Manually verify the successful login behavior.")]
    [InlineData("Confirm that the password input is masked.")]
    [InlineData("Check the layout at a narrow viewport.")]
    [InlineData("Submit demo / forge123 and record the observed message.")]
    [InlineData("Review the changed files.")]
    [InlineData("Validate the invalid-credential outcome manually.")]
    [InlineData("The user should verify the successful login behavior.")]
    [InlineData("The expected observation is a safe invalid-credential message.")]
    [InlineData("Test valid and invalid credentials in the browser manually.")]
    [InlineData("Review the exact approved changed file `src/App.cs`.")]
    public void Prospective_manual_instructions_are_not_completed_execution_claims(string wording)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = wording };

        VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits());
    }

    [Fact]
    public void Prospective_guidance_is_allowed_across_every_manual_plan_field()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var original = Assert.Single(candidate.TestCases);
        var manualCase = original with
        {
            Title = "Responsive manual browser review",
            Objective = "Validate the invalid-credential outcome manually.",
            Preconditions = ["Check the layout at a narrow viewport."],
            TestData = ["Submit demo / forge123 and record the observed message."],
            OrderedSteps = [new VerificationTestStepCandidate(1,
                "Manually verify the successful login behavior.", null,
                "The expected observation is a safe success message.")],
            ExpectedResult = "The password input should remain masked.",
            NegativeOrEdgeCases = ["Test invalid credentials manually."],
            RegressionScope = ["Review the exact approved changed file `src/App.cs`."],
            EvidenceRequirements = ["Record the observed message as a user-reported outcome."],
            SafetyNotes = ["Forge must not execute this manual check."]
        };
        candidate = candidate with
        {
            Summary = "The user should verify the exact approved behavior.",
            Scope = "Review the changed files and responsive layout.",
            Preconditions = ["Use the exact approved revision before manual review."],
            Risks = ["A narrow viewport may expose a layout issue."],
            Limitations = ["Manual user-reported results are required."],
            EvidenceGuidance = ["Record observations without credentials."],
            TestCases = [manualCase]
        };

        VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits());
    }

    [Fact]
    public void Approved_login_demo_data_paths_and_loopback_urls_are_accepted_in_manual_fields()
    {
        var context = LoginContext();
        var approvedPasswordInstruction = "Enter password" + ": " + "forge123";
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var original = Assert.Single(candidate.TestCases);
        var loginCase = original with
        {
            Objective = "Confirm the password input is masked.",
            TestData = ["Enter username demo", "Enter password forge123", approvedPasswordInstruction],
            OrderedSteps =
            [
                new VerificationTestStepCandidate(1,
                    "Review `src/App.jsx` and `src/App.css`, then open http://localhost:5173.", null,
                    "The expected observation is a masked password input."),
                new VerificationTestStepCandidate(2,
                    "Open https://127.0.0.1:5173/login and review `index.html`.", null,
                    "The user should record the observed message.")
            ],
            ExpectedResult = "The demo user can report the expected login outcome manually.",
            EvidenceRequirements = ["Record the manual browser observations without adding credentials."],
            RegressionScope = ["Review the exact approved files `src/App.jsx`, `src/App.css`, and `index.html`."],
            SafetyNotes = ["MANUAL — NOT EXECUTED BY FORGE"]
        };

        Assert.True(SensitiveContentDetector.ContainsSensitiveValue(approvedPasswordInstruction));
        VerificationValidator.ValidateCandidate(context, candidate with { TestCases = [loginCase] },
            new VerificationLimits());
    }

    [Fact]
    public void Initial_login_plan_allows_prospective_failure_outcome_language()
    {
        var context = LoginContext();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var original = Assert.Single(candidate.TestCases);
        var loginCase = original with
        {
            Objective = "Verify the approved failure message for incorrect credentials.",
            OrderedSteps =
            [
                new VerificationTestStepCandidate(1,
                    "After each failed login submission, confirm that the form remains visible.", null,
                    "The failure result should be Username or password is incorrect.")
            ],
            ExpectedResult = "Record the observed failure message.",
            NegativeOrEdgeCases = ["Confirm empty credentials display the approved failure text."],
            RegressionScope = ["Record the unsuccessful-login result as a prospective negative case."],
            OriginTestCaseId = null,
            RegressionFailureReportIds = []
        };

        VerificationValidator.ValidateCandidate(context, candidate with { TestCases = [loginCase] },
            new VerificationLimits());
    }

    public static IEnumerable<object[]> ExplicitInitialHistoricalClaims()
    {
        yield return ["Repeat the prior failed attempt."];
        yield return ["Repeat the previous verification failure."];
        yield return ["Use the superseded verification plan."];
        yield return ["This covers attempt 1."];
        yield return ["Recheck failure result 123."];
        yield return [$"Use failed result revision {Guid.NewGuid():D}."];
        yield return [$"Use result {Guid.NewGuid():D}."];
        yield return [$"Use analysis {Guid.NewGuid():D}."];
        yield return [$"Use proposal {Guid.NewGuid():D}."];
    }

    [Theory]
    [MemberData(nameof(ExplicitInitialHistoricalClaims))]
    public void Initial_plan_rejects_explicit_or_invented_historical_claims(string wording)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = wording };

        var exception = Assert.Throws<VerificationException>(() =>
            VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits()));

        Assert.Equal("The verification plan contains an invalid historical failure reference.", exception.Message);
    }

    [Fact]
    public void Initial_plan_rejects_structured_historical_bindings()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var original = Assert.Single(candidate.TestCases);

        var originException = Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(
            context, candidate with { TestCases = [original with { OriginTestCaseId = Guid.NewGuid() }] },
            new VerificationLimits()));
        var resultException = Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(
            context, candidate with
            {
                TestCases = [original with { RegressionFailureReportIds = [Guid.NewGuid()] }]
            }, new VerificationLimits()));

        Assert.Equal("Initial verification plans cannot reference prior correction evidence.", originException.Message);
        Assert.Equal("Initial verification plans cannot reference prior correction evidence.", resultException.Message);
    }

    public static IEnumerable<object[]> RejectedLoginPlanValues()
    {
        yield return ["Enter password" + ": " + "unapproved-value"];
        yield return ["api_key" + ": " + "sk-" + new string('a', 24)];
        yield return [SyntheticSensitiveValues.BearerAuthorization()];
        yield return ["ghp_" + new string('a', 24)];
        yield return ["https://" + "user" + ":" + "synthetic" + "@localhost:5173/login"];
        yield return [@"C:\Users\Example\project\src\App.jsx"];
        yield return ["/Users/example/project/src/App.jsx"];
        yield return [@"\\server\share\src\App.jsx"];
        yield return ["file:///Users/example/project/src/App.jsx"];
        yield return ["Review `src/Other.jsx` manually."];
        yield return ["Open https://example.invalid/login manually."];
        yield return ["Open http://localhost:5173/login?token=synthetic manually."];
        yield return ["Open http://localhost:5173/login#" + "forge123" + " manually."];
    }

    [Theory]
    [MemberData(nameof(RejectedLoginPlanValues))]
    public void Unapproved_secrets_paths_and_urls_remain_rejected(string value)
    {
        var context = LoginContext();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var original = Assert.Single(candidate.TestCases);
        var testCase = original with { TestData = [value] };

        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(context,
            candidate with { TestCases = [testCase] }, new VerificationLimits()));
    }

    [Fact]
    public void Approved_demo_value_allowance_is_limited_to_manual_plan_fields()
    {
        var context = LoginContext();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = "password" + ": " + "forge123" };

        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(context, candidate,
            new VerificationLimits()));
    }

    [Theory]
    [InlineData("Manual verification passed.")]
    [InlineData("The login flow was tested successfully.")]
    [InlineData("The build completed.")]
    [InlineData("Forge verified the behavior.")]
    [InlineData("The user confirmed the result.")]
    [InlineData("The application was executed and worked.")]
    [InlineData("All checks passed.")]
    [InlineData("The test was already performed.")]
    [InlineData("The implementation is already verified.")]
    [InlineData("The validation has passed.")]
    [InlineData("The verification has passed.")]
    [InlineData("Forge confirmed the behavior.")]
    [InlineData("The build succeeded.")]
    [InlineData("The human approved the result.")]
    public void Completed_execution_claims_are_still_rejected(string wording)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = wording };

        var exception = Assert.Throws<VerificationException>(() =>
            VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits()));

        Assert.Equal("verification_validation", exception.Category);
        Assert.Equal("The verification plan claimed that manual verification had already been performed.",
            exception.Message);
    }

    [Fact]
    public async Task Initial_plan_language_override_is_opt_in_and_preserves_openai_audit_and_usage()
    {
        var context = Context();
        var output = ValidJson(context).Replace(
            "Concise manual verification guidance.",
            "Test unsuccessful credentials; confirm the previous failed login submission shows the failure message and whether the successful behavior was verified against the expected result.",
            StringComparison.Ordinal);

        var disabled = await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(new QueueGateway(Envelope(output))).GenerateAsync(context));
        Assert.Equal(VerificationGenerationAttemptStatus.RejectedProviderOutput, disabled.DurableStatus);

        var evaluation = await Engine(new QueueGateway(Envelope(output)), true).GenerateAsync(context);
        var plan = VerificationValidator.FinalizeCandidate(context, evaluation.Candidate, 1, Guid.NewGuid(),
            evaluation.ModelCalls.Select(call => call.Id).ToArray(), new VerificationLimits(),
            evaluation.InitialPlanLanguageOverrideApplied);

        Assert.True(evaluation.InitialPlanLanguageOverrideApplied);
        Assert.True(plan.InitialPlanLanguageOverrideApplied);
        Assert.Equal(VerificationPlanSource.OpenAI, plan.Source);
        Assert.Equal("gpt-5.6-sol", plan.Model);
        Assert.Contains(VerificationValidator.InitialPlanLanguageOverrideLimitation, plan.Limitations);
        Assert.All(plan.TestCases, testCase => Assert.Contains(
            VerificationValidator.InitialPlanLanguageOverrideSafetyNote, testCase.SafetyNotes));
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.True(call.Succeeded);
        Assert.Equal("request-id", call.ProviderRequestId);
        Assert.Equal("response-id", call.ProviderResponseId);
        Assert.Equal(100, call.InputTokens);
        Assert.Equal(50, call.OutputTokens);
        Assert.NotNull(call.EstimatedCostUsd);
    }

    public static IEnumerable<object[]> NonOverridableCompletedClaims()
    {
        yield return ["Forge executed the tests."];
        yield return ["Forge ran the tests."];
        yield return ["Forge verified the behavior."];
        yield return ["Forge confirmed the behavior."];
        yield return ["Verification already passed."];
        yield return ["Verification has passed."];
        yield return ["Manual verification was completed."];
        yield return ["Manual verification was performed."];
        yield return ["Build was executed by Forge."];
        yield return ["Tests were executed by Forge."];
        yield return ["User already confirmed the result."];
        yield return ["Human already approved the result."];
    }

    [Theory]
    [MemberData(nameof(NonOverridableCompletedClaims))]
    public void Initial_plan_language_override_never_accepts_bounded_completed_claims(string wording)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = wording };

        var exception = Assert.Throws<VerificationException>(() =>
            VerificationValidator.ValidateCandidateForGeneration(context, candidate, new VerificationLimits(), true,
                out _));

        Assert.Equal(VerificationValidationFailureReason.ManualExecutionClaim,
            exception.ValidationFailureReason);
    }

    public static IEnumerable<object[]> UnsafeOverrideValues()
    {
        yield return ["api_key" + ": " + "sk-" + new string('a', 24)];
        yield return [SyntheticSensitiveValues.BearerAuthorization()];
        yield return [@"C:\Users\Example\project\src\App.cs"];
        yield return ["Review `src/Unapproved.cs` manually."];
        yield return [$"Use plan {Guid.NewGuid():D}."];
    }

    [Theory]
    [MemberData(nameof(UnsafeOverrideValues))]
    public void Initial_plan_language_override_preserves_content_path_and_historical_validation(string unsafeValue)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        {
            Model = "gpt-5.6-sol", ReasoningEffort = "medium",
            Summary = "Test unsuccessful credentials and confirm the previous failed login submission shows the expected failure message."
        };
        var testCase = Assert.Single(candidate.TestCases) with { TestData = [unsafeValue] };

        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidateForGeneration(
            context, candidate with { TestCases = [testCase] }, new VerificationLimits(), true, out _));
    }

    [Fact]
    public void Initial_plan_language_override_preserves_approved_command_validation()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        {
            Model = "gpt-5.6-sol", ReasoningEffort = "medium",
            Summary = "Test unsuccessful credentials and confirm the previous failed login submission shows the expected failure message."
        };
        var testCase = Assert.Single(candidate.TestCases);
        var step = Assert.Single(testCase.OrderedSteps) with { ApprovedValidationCommandId = "V999" };

        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidateForGeneration(
            context, candidate with { TestCases = [testCase with { OrderedSteps = [step] }] },
            new VerificationLimits(), true, out _));
    }

    [Fact]
    public async Task Realistic_prospective_openai_manual_plan_preserves_binding_telemetry_and_manual_boundary()
    {
        var context = Context();
        var output = ValidJson(context)
            .Replace("Concise manual verification guidance.",
                "Manually verify successful and invalid credential behavior.", StringComparison.Ordinal)
            .Replace("Observe the approved behavior.",
                "Confirm that the password input is masked.", StringComparison.Ordinal)
            .Replace("Inspect the approved behavior manually.",
                "Test the responsive layout and submit demo / forge123 manually.", StringComparison.Ordinal)
            .Replace("The user observes the expected behavior.",
                "The expected observation is a safe user-reported message.", StringComparison.Ordinal);
        var evaluation = await Engine(new QueueGateway(Envelope(output))).GenerateAsync(context);
        var plan = VerificationValidator.FinalizeCandidate(context, evaluation.Candidate, 1, Guid.NewGuid(),
            evaluation.ModelCalls.Select(call => call.Id).ToArray(), new VerificationLimits());

        Assert.Equal(VerificationPlanSource.OpenAI, plan.Source);
        Assert.Equal("gpt-5.6-sol", plan.Model);
        Assert.Equal(context.ImplementationRevisionId, plan.ImplementationRevisionId);
        Assert.Equal(context.ImplementationResultFingerprint, plan.ImplementationResultFingerprint);
        Assert.All(plan.TestCases, testCase =>
        {
            Assert.Equal(VerificationTestCategory.ManualBehavior, testCase.Category);
            Assert.All(testCase.OrderedSteps, step => Assert.Null(step.ApprovedValidationCommandId));
        });
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.True(call.Succeeded);
        Assert.Equal("response-id", call.ProviderResponseId);
        Assert.Equal(100, call.InputTokens);
        Assert.Equal(50, call.OutputTokens);
        Assert.NotNull(call.EstimatedCostUsd);
    }

    [Fact]
    public async Task Fake_openai_login_plan_accepts_only_approved_demo_data_paths_and_loopback_url()
    {
        var context = LoginContext();
        var testDataJson = JsonSerializer.Serialize(new[]
            { "Enter username demo", "Enter password" + ": " + "forge123" });
        var output = ValidJson(context)
            .Replace("\"testData\":[]", "\"testData\":" + testDataJson,
                StringComparison.Ordinal)
            .Replace("\"objective\":\"Observe the approved behavior.\"",
                "\"objective\":\"Verify the approved failure message for incorrect credentials.\"",
                StringComparison.Ordinal)
            .Replace("Inspect the approved behavior manually.",
                "Open http://localhost:5173, review src/App.jsx and src/App.css, then submit invalid credentials; after the failed login submission, inspect index.html manually.",
                StringComparison.Ordinal)
            .Replace("The user observes the expected behavior.",
                "The failure result should be Username or password is incorrect.",
                StringComparison.Ordinal);
        var evaluation = await Engine(new QueueGateway(Envelope(output))).GenerateAsync(context);
        var plan = VerificationValidator.FinalizeCandidate(context, evaluation.Candidate, 1, Guid.NewGuid(),
            evaluation.ModelCalls.Select(call => call.Id).ToArray(), new VerificationLimits());

        Assert.Equal(VerificationPlanSource.OpenAI, plan.Source);
        Assert.Equal("gpt-5.6-sol", plan.Model);
        Assert.Contains(Assert.Single(plan.TestCases).TestData,
            value => value.Contains("forge123", StringComparison.Ordinal));
        Assert.Contains("http://localhost:5173", plan.TestCases[0].OrderedSteps[0].Instruction,
            StringComparison.Ordinal);
        Assert.Contains("failure message", plan.TestCases[0].Objective, StringComparison.Ordinal);
        Assert.Contains("failed login submission", plan.TestCases[0].OrderedSteps[0].Instruction,
            StringComparison.Ordinal);
        Assert.Contains("failure result", plan.TestCases[0].OrderedSteps[0].ExpectedObservation,
            StringComparison.Ordinal);
        Assert.Null(plan.SupersedesPlanId);
        Assert.Equal(1, plan.PlanNumber);
        Assert.All(plan.TestCases, testCase =>
        {
            Assert.Null(testCase.OriginTestCaseId);
            Assert.Empty(testCase.RegressionFailureReportIds);
        });
        Assert.All(plan.TestCases.SelectMany(testCase => testCase.OrderedSteps),
            step => Assert.Null(step.ApprovedValidationCommandId));
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.True(call.Succeeded);
        Assert.Equal("response-id", call.ProviderResponseId);
        Assert.NotNull(call.EstimatedCostUsd);
    }

    [Fact]
    public async Task Exact_final_projection_is_scanned_before_provider_dispatch()
    {
        var secret = SyntheticSensitiveValues.Jwt();
        var original = Context();
        var contexts = new List<VerificationPlanContext>
        {
            Rebind(original with { ApprovedValidationCommands = [new ApprovedValidationCommand("V1", $"dotnet test --token {secret}")] }),
            original with { ApprovedPlan = original.ApprovedPlan with { Risks = [$"Risk {secret}"] } },
            original with { ApprovedPlan = original.ApprovedPlan with { Assumptions = [$"Assume {secret}"] } },
            original with { ApprovedPlan = original.ApprovedPlan with { RequirementCoverage = [new RequirementCoverageItem($"Coverage {secret}", ["src/App.cs"], [1])] } },
            original with { ImplementationResult = original.ImplementationResult with { Warnings = [$"Warning {secret}"] } },
            original with { RepositoryEvidence = [original.RepositoryEvidence[0] with { Excerpt = $"Evidence {secret}" }] },
            Rebind(original with { ImplementationResult = original.ImplementationResult with { ChangedFiles = [original.ImplementationResult.ChangedFiles[0] with { DiffPreview = $"diff {secret}" }] } })
        };

        foreach (var context in contexts)
        {
            var gateway = new CountingGateway();
            var exception = await Assert.ThrowsAsync<VerificationException>(() => Engine(gateway).GenerateAsync(context));
            Assert.Equal("verification_sensitive_context", exception.Category);
            Assert.Equal(0, gateway.RequestCount);
        }
    }

    [Theory]
    [InlineData("rate_limit", 429)]
    [InlineData("provider_error", 502)]
    [InlineData("provider_error", 503)]
    public async Task Explicit_retryable_provider_response_is_durably_checkpointed_before_one_retry(
        string category, int status)
    {
        var context = Context();
        var gateway = new QueueGateway(
            new OpenAITransportException(category, "safe", statusCode: status,
                dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived),
            Envelope(ValidJson(context)));
        var observer = new TrackingObserver();

        var evaluation = await Engine(gateway).GenerateAsync(context, observer);

        Assert.Equal(2, evaluation.ModelCalls.Count);
        Assert.Equal(new[]
        {
            VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.RetryableProviderResponse,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.ResponseReceived
        }, observer.Checkpoints);
        Assert.Single(observer.Responses);
        Assert.Equal(VerificationCallDispatchDisposition.ResponseReceived,
            observer.Responses[0].DispatchDisposition);
        Assert.Equal(status, observer.TransportOutcomes[0].Call.ProviderHttpStatusCode);
    }

    [Fact]
    public async Task Ambiguous_transport_failure_is_not_retried_and_remains_durably_ambiguous()
    {
        var gateway = new QueueGateway(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DispatchMayHaveOccurred));
        var observer = new TrackingObserver();

        var exception = await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(gateway).GenerateAsync(Context(), observer));

        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, exception.DurableStatus);
        Assert.Equal(new[] { VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.AmbiguousAfterDispatch }, observer.Checkpoints);
        Assert.Single(gateway.Requests);
        Assert.Equal(VerificationCallDispatchDisposition.PossiblyDispatched,
            Assert.Single(observer.TransportOutcomes).Disposition);
    }

    [Fact]
    public async Task Timeout_or_cancellation_after_dispatch_is_durably_ambiguous_and_never_retried()
    {
        var gateway = new QueueGateway(new OperationCanceledException("simulated timeout after dispatch"));
        var observer = new TrackingObserver();

        var exception = await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(gateway).GenerateAsync(Context(), observer));

        Assert.Equal("verification_timeout", exception.Category);
        Assert.Equal(VerificationGenerationAttemptStatus.AmbiguousAfterDispatch, exception.DurableStatus);
        Assert.Equal(new[] { VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.AmbiguousAfterDispatch }, observer.Checkpoints);
        Assert.Single(gateway.Requests);
        Assert.Equal(VerificationCallDispatchDisposition.PossiblyDispatched,
            Assert.Single(observer.TransportOutcomes).Disposition);
    }

    [Fact]
    public async Task Definitely_pre_dispatch_failure_is_checkpointed_and_may_retry_once()
    {
        var context = Context();
        var gateway = new QueueGateway(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch),
            Envelope(ValidJson(context)));
        var observer = new TrackingObserver();

        await Engine(gateway).GenerateAsync(context, observer);

        Assert.Equal(new[] { VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.FailedBeforeDispatch,
            VerificationDispatchCheckpoint.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.ResponseReceived }, observer.Checkpoints);
        Assert.Equal(VerificationCallDispatchDisposition.DefinitelyNotDispatched,
            Assert.Single(observer.TransportOutcomes).Disposition);
    }

    [Fact]
    public async Task Missing_usage_is_persisted_as_unavailable_before_output_parsing_without_zero_coercion()
    {
        var context = Context();
        var envelope = Envelope(ValidJson(context)) with
        {
            InputTokens = null, CachedInputTokens = null, OutputTokens = null, ReasoningTokens = null,
            UsageAvailable = false
        };
        var observer = new TrackingObserver();

        var evaluation = await Engine(new QueueGateway(envelope)).GenerateAsync(context, observer);

        var response = Assert.Single(observer.Responses);
        Assert.False(response.UsageAvailable);
        Assert.Null(response.InputTokens);
        Assert.Null(response.OutputTokens);
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.False(call.ProviderUsageAvailable);
        Assert.Null(call.InputTokens);
        Assert.Null(call.EstimatedCostUsd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Partial_usage_preserves_valid_fields_and_only_prices_when_input_and_output_are_known(
        bool outputKnown)
    {
        var context = Context();
        var envelope = Envelope(ValidJson(context)) with
        {
            InputTokens = 100, CachedInputTokens = null, OutputTokens = outputKnown ? 25 : null,
            ReasoningTokens = null, UsageAvailable = false,
            UsageAvailability = VerificationUsageAvailability.Partial
        };
        var observer = new TrackingObserver();

        var evaluation = await Engine(new QueueGateway(envelope)).GenerateAsync(context, observer);

        var response = Assert.Single(observer.Responses);
        Assert.Equal(VerificationUsageAvailability.Partial, response.EffectiveUsageAvailability);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(outputKnown ? 25 : null, response.OutputTokens);
        var call = Assert.Single(evaluation.ModelCalls);
        Assert.Equal(VerificationUsageAvailability.Partial, call.ProviderUsageAvailability);
        Assert.Equal(100, call.InputTokens);
        if (outputKnown) Assert.NotNull(call.EstimatedCostUsd);
        else Assert.Null(call.EstimatedCostUsd);
    }

    [Fact]
    public async Task Rejected_structured_output_still_records_normalized_response_telemetry_first()
    {
        var observer = new TrackingObserver();

        await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(new QueueGateway(Envelope("{ malformed"))).GenerateAsync(Context(), observer));

        var response = Assert.Single(observer.Responses);
        Assert.Equal("response-id", response.ProviderResponseId);
        Assert.True(response.UsageAvailable);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(200, response.HttpStatusCode);
    }

    [Fact]
    public async Task Locally_rejected_completed_response_is_retryable_only_as_rejected_provider_output()
    {
        var context = Context();
        var output = ValidJson(context).Replace(
            "\"summary\":\"Concise manual verification guidance.\"",
            "\"summary\":\"Manual verification has already passed.\"",
            StringComparison.Ordinal);
        var gateway = new QueueGateway(Envelope(output));
        var observer = new TrackingObserver();

        var exception = await Assert.ThrowsAsync<VerificationProviderException>(() =>
            Engine(gateway).GenerateAsync(context, observer));

        Assert.Equal(VerificationGenerationAttemptStatus.RejectedProviderOutput, exception.DurableStatus);
        Assert.Equal(VerificationGenerationAttemptSemantics.ValidationRejectedCategory, exception.Category);
        Assert.Contains("another provider request may incur a charge", exception.Message, StringComparison.Ordinal);
        Assert.Single(gateway.Requests);
        Assert.Single(observer.Responses);
        var call = Assert.Single(exception.ModelCalls);
        Assert.False(call.Succeeded);
        Assert.Equal(VerificationCallDispatchDisposition.ResponseReceived, call.VerificationDispatchDisposition);
        Assert.Equal("request-id", call.ProviderRequestId);
        Assert.Equal("response-id", call.ProviderResponseId);
        Assert.Equal(100, call.InputTokens);
        Assert.Equal(50, call.OutputTokens);
        Assert.NotNull(call.EstimatedCostUsd);
    }

    [Fact]
    public void Slash_separated_prose_is_not_a_path_but_credible_unapproved_paths_are_rejected()
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium" };
        var prose = candidate with { Summary = "Save/Retry request/response pass/fail client/server date/time and/or behavior." };
        VerificationValidator.ValidateCandidate(context, prose, new VerificationLimits());

        var path = candidate with { Summary = "Inspect `src/Unapproved.cs` manually." };
        Assert.Throws<VerificationException>(() => VerificationValidator.ValidateCandidate(context, path,
            new VerificationLimits()));
    }

    [Theory]
    [InlineData("Inspect `request/response` and \"application/json\" results.")]
    [InlineData("Compare `pass/fail`, client/server, read/write, and input/output behavior.")]
    [InlineData("Check application/problem+json and text/plain representations.")]
    [InlineData("Check `APPLICATION/JSON` and `Text/Plain` representations.")]
    [InlineData("Observe JSON Pointer /items/0/name without treating it as a local file.")]
    [InlineData("Observe JSON Pointer /items/schema.json and fragment #/items/schema.json.")]
    [InlineData("Observe escaped pointers /items/a~1b and /items/a~0b.")]
    [InlineData("A relative documentation URL may look like docs/schema.json?view=compact.")]
    [InlineData("The browser route is docs/schema.json#example.")]
    [InlineData("Use the hyperlink reference `docs/schema.json?download=1` in documentation.")]
    [InlineData("Compare the XML namespace urn:example:items and C# token System.Collections.Generic.")]
    [InlineData("A Dockerfile is commonly used for container builds, while a Makefile is a general build convention.")]
    [InlineData("Inspect the exact approved path `SRC/App.CS` manually.")]
    [InlineData("Inspect the exact approved path `src/App.cs?view=compact` manually.")]
    public void Prose_data_syntax_urls_and_exact_approved_paths_are_accepted(string summary)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = summary };

        VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits());
    }

    [Theory]
    [InlineData("Inspect C:\\private\\file.txt.")]
    [InlineData("Inspect C:/private/file.txt.")]
    [InlineData("Inspect \\\\server\\share\\file.txt.")]
    [InlineData("Inspect /home/user/file.txt.")]
    [InlineData("Inspect /Users/name/file.txt.")]
    [InlineData("Inspect ../secret.txt.")]
    [InlineData("Inspect ..\\secret.txt.")]
    [InlineData("Inspect src\\..\\secret.txt.")]
    [InlineData("Inspect src/..\\secret.txt.")]
    [InlineData("Inspect %2e%2e%2fsecret.txt.")]
    [InlineData("Inspect %252e%252e%252fsecret.txt.")]
    [InlineData("Inspect `src/Unapproved.cs`.")]
    [InlineData("Inspect `src/Unapproved`.")]
    [InlineData("Inspect `config/private.json`.")]
    [InlineData("Inspect src/Unapproved.cs?view=compact.")]
    [InlineData("Modify config/secret.json#current.")]
    [InlineData("Open `Directory.Build.props?raw=1` from the repository.")]
    [InlineData("Examine src/Unapproved.cs?view=compact.")]
    [InlineData("Change config/secret.json#current.")]
    [InlineData("Patch package.json?raw=true.")]
    [InlineData("Load Directory.Build.props?raw=1.")]
    [InlineData("Verify contents of src/Unapproved.cs?view=compact.")]
    [InlineData("Consult app.config?raw=1 in the repository.")]
    [InlineData("Use the repository file Dockerfile?download=1.")]
    [InlineData("Manipulate src/Unapproved.cs#section.")]
    [InlineData("Inspect src/App.cs?next=../secret.txt.")]
    [InlineData("Inspect src/App.cs?next=%252e%252e%252fsecret.txt.")]
    [InlineData("Inspect Dockerfile.")]
    [InlineData("Inspect Makefile.")]
    [InlineData("Inspect .gitignore.")]
    [InlineData("The Dockerfile exists in the repository.")]
    [InlineData("Open Directory.Build.props.")]
    [InlineData("Inspect package.json.")]
    [InlineData("Inspect file:///private/file.txt.")]
    [InlineData("Inspect ..∕secret.txt.")]
    public void Absolute_traversal_and_credible_unapproved_paths_are_rejected(string summary)
    {
        var context = Context();
        var candidate = OpenAIVerificationPlanEngine.Parse(ValidJson(context)) with
        { Model = "gpt-5.6-sol", ReasoningEffort = "medium", Summary = summary };

        Assert.Throws<VerificationException>(() =>
            VerificationValidator.ValidateCandidate(context, candidate, new VerificationLimits()));
    }

    private static VerificationPlanContext Context()
    {
        var task = VerificationWorkflowTests.ApprovedImplementation();
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        task.BeginVerificationPlanGeneration(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, revision.RevisionId, revision.ResultFingerprint!), DateTimeOffset.UtcNow);
        return VerificationWorkflowService.CreateContext(task, DateTimeOffset.UtcNow);
    }

    private static VerificationPlanContext LoginContext()
    {
        var context = Context();
        var evidence = context.RepositoryEvidence[0];
        return Rebind(context with
        {
            ApprovedRequirement = "Use demo username demo and demo-only password forge123 for manual login review.",
            ApprovedManualTestData = ["demo", "forge123"],
            RepositoryEvidence =
            [
                evidence with { Id = "E1", RelativePath = "src/App.jsx", ContentHash = new string('1', 64) },
                evidence with { Id = "E2", RelativePath = "src/App.css", ContentHash = new string('2', 64) },
                evidence with { Id = "E3", RelativePath = "index.html", ContentHash = new string('3', 64) }
            ]
        });
    }

    private static VerificationPlanContext Rebind(VerificationPlanContext context)
    {
        var unbound = context with { ContextFingerprint = string.Empty };
        return unbound with { ContextFingerprint = VerificationFingerprint.ComputeContext(unbound) };
    }

    private static OpenAIVerificationPlanEngine Engine(
        IOpenAIResponsesGateway gateway,
        bool allowInitialPlanLanguageOverride = false) => new(
        new ForgeAiOptions
        {
            Mode = ForgeAiModes.OpenAI,
            VerificationPlanningModel = "gpt-5.6-sol",
            VerificationPlanningReasoningEffort = "medium",
            VerificationPlanningMaxOutputTokens = 8_000,
            VerificationPlanningTimeoutSeconds = 30
        }, gateway, new ModelCostCalculator(ForgeAiOptions.DefaultPricing()), TimeProvider.System,
        allowInitialPlanLanguageOverride);

    private static OpenAIResponseEnvelope Envelope(string output) => new(
        "response-id", output, 100, 0, 50, 10, ProviderRequestId: "request-id",
        OutputItems: [new OpenAIResponseOutputItem(OpenAIResponseOutputItemKind.Message, "assistant",
            [new OpenAIResponseContent(OpenAIResponseContentKind.OutputText, output)])]);

    private sealed class CountingGateway : IOpenAIResponsesGateway
    {
        public int RequestCount { get; private set; }
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            throw new InvalidOperationException("Provider invocation was not expected.");
        }
    }

    private sealed class QueueGateway(params object[] outcomes) : IOpenAIResponsesGateway
    {
        private readonly Queue<object> queue = new(outcomes);
        public List<OpenAIResponseRequest> Requests { get; } = [];
        public Task<OpenAIResponseEnvelope> CreateResponseAsync(OpenAIResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var outcome = queue.Dequeue();
            return outcome is Exception exception
                ? Task.FromException<OpenAIResponseEnvelope>(exception)
                : Task.FromResult((OpenAIResponseEnvelope)outcome);
        }
    }

    private sealed class TrackingObserver : IVerificationGenerationObserver
    {
        public List<VerificationDispatchCheckpoint> Checkpoints { get; } = [];
        public List<VerificationProviderResponseTelemetry> Responses { get; } = [];
        public List<(ModelCallRecord Call, VerificationCallDispatchDisposition Disposition)> TransportOutcomes { get; } = [];
        public Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid physicalCallId,
            CancellationToken cancellationToken = default)
        {
            Assert.NotEqual(Guid.Empty, physicalCallId);
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }

        public Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
            CancellationToken cancellationToken = default)
        {
            Responses.Add(response);
            Checkpoints.Add(VerificationDispatchCheckpoint.ResponseReceived);
            return Task.CompletedTask;
        }

        public Task RecordTransportFailureAsync(Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
            ModelCallRecord modelCall, VerificationCallDispatchDisposition disposition, string safeFailureMessage,
            CancellationToken cancellationToken = default)
        {
            TransportOutcomes.Add((modelCall, disposition));
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }
    }

    private static string ValidJson(VerificationPlanContext context) => JsonSerializer.Serialize(new
    {
        contextFingerprint = context.ContextFingerprint,
        summary = "Concise manual verification guidance.", scope = "Exact approved revision only.",
        preconditions = new[] { "Use the exact approved revision." },
        testCases = new[]
        {
            new
            {
                order = 1, title = "Manual behavior check", objective = "Observe the approved behavior.",
                category = "ManualBehavior", isRequired = true,
                preconditions = Array.Empty<string>(), testData = Array.Empty<string>(),
                orderedSteps = new[] { new { order = 1, instruction = "Inspect the approved behavior manually.", approvedValidationCommandId = "", expectedObservation = "The user observes the expected behavior." } },
                expectedResult = "The user reports the expected behavior.", negativeOrEdgeCases = Array.Empty<string>(),
                regressionScope = Array.Empty<string>(), evidenceRequirements = Array.Empty<string>(),
                safetyNotes = new[] { "Forge does not execute this check." }, originTestCaseId = "",
                regressionFailureReportIds = Array.Empty<string>()
            }
        },
        risks = Array.Empty<string>(), limitations = new[] { "Manual user report only." },
        evidenceGuidance = new[] { "Do not include secrets." }
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
