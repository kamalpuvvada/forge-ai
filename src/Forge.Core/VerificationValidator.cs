using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Core;

public static partial class VerificationValidator
{
    public const string InitialPlanLanguageOverrideLimitation =
        "Development-only initial-plan text-classifier override applied. Structural, binding, command, path, and sensitive-content validation remained enforced.";
    public const string InitialPlanLanguageOverrideSafetyNote =
        "MANUAL — NOT EXECUTED BY FORGE. All outcomes require explicit user reporting.";

    public static VerificationPlan FinalizeCandidate(
        VerificationPlanContext context,
        VerificationPlanCandidate candidate,
        int planNumber,
        Guid planId,
        IReadOnlyList<Guid> modelCallIds,
        VerificationLimits limits,
        bool initialPlanLanguageOverrideApplied = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        if (initialPlanLanguageOverrideApplied)
            candidate = ValidateAppliedInitialPlanLanguageOverride(context, candidate, limits, true);
        else
            ValidateCandidate(context, candidate, limits);
        if (planId == Guid.Empty || planNumber is < 1 or > 6 ||
            initialPlanLanguageOverrideApplied && planNumber != 1)
            throw Invalid("The verification plan identity is invalid.");

        var testCases = candidate.TestCases.Select(testCase => new VerificationTestCase(
            Guid.NewGuid(), testCase.Order, testCase.Title.Trim(), testCase.Objective.Trim(), testCase.Category,
            testCase.IsRequired, Trim(testCase.Preconditions), Trim(testCase.TestData),
            testCase.OrderedSteps.Select(step => new VerificationTestStep(
                step.Order, step.Instruction.Trim(), EmptyToNull(step.ApprovedValidationCommandId),
                step.ExpectedObservation.Trim())).ToArray(),
            testCase.ExpectedResult.Trim(), Trim(testCase.NegativeOrEdgeCases), Trim(testCase.RegressionScope),
            Trim(testCase.EvidenceRequirements), Trim(testCase.SafetyNotes), testCase.OriginTestCaseId,
            testCase.RegressionFailureReportIds.ToArray())).ToArray();

        var provisional = new VerificationPlan(
            planId, planNumber, context.ImplementationRevisionId, context.ImplementationResultFingerprint,
            context.ApprovedRequirementFingerprint, context.ApprovedPlanFingerprint, context.ContextFingerprint,
            context.CreatedAt, candidate.Source, candidate.Model, candidate.ReasoningEffort,
            candidate.Summary.Trim(), candidate.Scope.Trim(), Trim(candidate.Preconditions), testCases,
            Trim(candidate.Risks), Trim(candidate.Limitations), Trim(candidate.EvidenceGuidance), string.Empty,
            VerificationPlanStatus.Current, modelCallIds.ToArray(), context.PreviousPlan?.PlanId,
            context.PreviousPlan is null ? null :
                $"Replacement verification after failure analysis {context.FailureAnalysis!.AnalysisId:D} and approved correction proposal {context.CorrectionProposal!.ProposalId:D}.",
            initialPlanLanguageOverrideApplied);
        var plan = provisional with { PlanFingerprint = VerificationFingerprint.ComputePlan(context.TaskId, provisional) };
        ValidatePersistedPlan(context.TaskId, plan, context, limits);
        return plan;
    }

    public static void ValidateCandidate(
        VerificationPlanContext context,
        VerificationPlanCandidate candidate,
        VerificationLimits limits)
        => ValidateCandidateCore(context, candidate, limits, false);

    public static VerificationPlanCandidate ValidateCandidateForGeneration(
        VerificationPlanContext context,
        VerificationPlanCandidate candidate,
        VerificationLimits limits,
        bool allowInitialPlanLanguageOverride,
        out bool initialPlanLanguageOverrideApplied)
    {
        initialPlanLanguageOverrideApplied = false;
        try
        {
            ValidateCandidate(context, candidate, limits);
            return candidate;
        }
        catch (VerificationException exception) when (
            exception.ValidationFailureReason is VerificationValidationFailureReason.ManualExecutionClaim or
                VerificationValidationFailureReason.HistoricalFailureClaim)
        {
            if (!allowInitialPlanLanguageOverride) throw;
            var audited = ValidateAppliedInitialPlanLanguageOverride(context, candidate, limits, true);
            initialPlanLanguageOverrideApplied = true;
            return audited;
        }
    }

    private static void ValidateCandidateCore(
        VerificationPlanContext context,
        VerificationPlanCandidate candidate,
        VerificationLimits limits,
        bool suppressFragileTextClassifiers)
    {
        if (!string.Equals(candidate.ContextFingerprint, context.ContextFingerprint, StringComparison.Ordinal))
            throw Invalid("The verification plan does not match its approved context.");
        if (!Enum.IsDefined(candidate.Source) || candidate.Source == VerificationPlanSource.OpenAI &&
            string.IsNullOrWhiteSpace(candidate.Model) || candidate.Source == VerificationPlanSource.DeterministicFake &&
            candidate.Model is not null)
            throw Invalid("The verification-plan provenance is invalid.");
        Required(candidate.Summary, limits.MaximumSummaryCharacters, "The verification-plan summary");
        Required(candidate.Scope, limits.MaximumScopeCharacters, "The verification-plan scope");
        Collection(candidate.Preconditions, limits.MaximumPreconditions, limits.MaximumListItemCharacters, "precondition");
        Collection(candidate.Risks, limits.MaximumRisks, limits.MaximumListItemCharacters, "risk");
        Collection(candidate.Limitations, limits.MaximumLimitations, limits.MaximumListItemCharacters, "limitation");
        Collection(candidate.EvidenceGuidance, limits.MaximumEvidenceGuidanceItems,
            limits.MaximumListItemCharacters, "evidence-guidance item");
        if (candidate.TestCases is null || candidate.TestCases.Count is < 1 ||
            candidate.TestCases.Count > limits.MaximumCasesPerPlan || !candidate.TestCases.Any(item => item.IsRequired))
            throw Invalid("The verification plan must contain at least one required bounded test case.");
        if (!candidate.TestCases.Select(item => item.Order).SequenceEqual(Enumerable.Range(1, candidate.TestCases.Count)))
            throw Invalid("Verification test cases must use unique sequential order values.");

        var commandIds = context.ApprovedValidationCommands.Select(command => command.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var testCase in candidate.TestCases)
        {
            Required(testCase.Title, limits.MaximumTitleCharacters, $"Verification case {testCase.Order} title");
            Required(testCase.Objective, limits.MaximumObjectiveCharacters, $"Verification case {testCase.Order} objective");
            Required(testCase.ExpectedResult, limits.MaximumExpectedResultCharacters,
                $"Verification case {testCase.Order} expected result");
            if (!Enum.IsDefined(testCase.Category)) throw Invalid($"Verification case {testCase.Order} has an invalid category.");
            Collection(testCase.Preconditions, limits.MaximumPreconditions, limits.MaximumListItemCharacters, "case precondition");
            Collection(testCase.TestData, limits.MaximumTestDataItems, limits.MaximumListItemCharacters, "test-data item");
            Collection(testCase.NegativeOrEdgeCases, limits.MaximumEdgeCases, limits.MaximumListItemCharacters, "edge case");
            Collection(testCase.RegressionScope, limits.MaximumRegressionItems, limits.MaximumListItemCharacters, "regression item");
            Collection(testCase.EvidenceRequirements, limits.MaximumEvidenceRequirements,
                limits.MaximumEvidenceRequirementCharacters, "evidence requirement");
            Collection(testCase.SafetyNotes, limits.MaximumEvidenceRequirements,
                limits.MaximumSafetyNoteCharacters, "safety note");
            if (context.PreviousPlan is null)
            {
                if (testCase.OriginTestCaseId is not null || testCase.RegressionFailureReportIds.Count > 0)
                    throw Invalid("Initial verification plans cannot reference prior correction evidence.");
            }
            else
            {
                var priorCaseIds = context.PreviousPlan.TestCases.Select(item => item.TestCaseId).ToHashSet();
                var failureEvidence = (context.PreviousFailureEvidence ?? []).ToDictionary(item => item.ResultRevisionId);
                var failureIds = failureEvidence.Keys.ToHashSet();
                if (testCase.OriginTestCaseId is { } origin && !priorCaseIds.Contains(origin))
                    throw Invalid("A replacement verification case referenced an unknown prior case.");
                if (testCase.RegressionFailureReportIds.Any(id => !failureIds.Contains(id)) ||
                    testCase.RegressionFailureReportIds.Count != testCase.RegressionFailureReportIds.Distinct().Count())
                    throw Invalid("A replacement verification case referenced unknown or duplicate failure evidence.");
                if (testCase.RegressionFailureReportIds.Count > 0 && testCase.OriginTestCaseId is null)
                    throw Invalid("A regression verification case must identify its exact prior case.");
                if (testCase.OriginTestCaseId is { } exactOrigin && testCase.RegressionFailureReportIds.Any(id =>
                        failureEvidence[id].TestCaseId != exactOrigin))
                    throw Invalid("A regression verification case referenced failure evidence from a different prior case.");
            }
            if (testCase.OrderedSteps is null || testCase.OrderedSteps.Count is < 1 ||
                testCase.OrderedSteps.Count > limits.MaximumStepsPerCase ||
                !testCase.OrderedSteps.Select(step => step.Order).SequenceEqual(Enumerable.Range(1, testCase.OrderedSteps.Count)))
                throw Invalid($"Verification case {testCase.Order} must contain sequential bounded steps.");
            foreach (var step in testCase.OrderedSteps)
            {
                Required(step.Instruction, limits.MaximumListItemCharacters, "Verification instruction");
                Required(step.ExpectedObservation, limits.MaximumListItemCharacters, "Expected observation");
                var commandId = EmptyToNull(step.ApprovedValidationCommandId);
                if (commandId is not null && !commandIds.Contains(commandId))
                    throw Invalid("The verification plan referenced an unapproved validation command.");
            }
        }

        if (context.PreviousPlan is not null)
        {
            var requiredCoverage = (context.PreviousFailureEvidence ?? []).Select(item => item.ResultRevisionId).ToHashSet();
            var actualCoverage = candidate.TestCases.Where(item => item.IsRequired)
                .SelectMany(item => item.RegressionFailureReportIds).ToHashSet();
            if (requiredCoverage.Count == 0 || !requiredCoverage.SetEquals(actualCoverage))
                throw Invalid("The replacement verification plan does not cover every current failed result.");
        }

        foreach (var (value, testCase, field) in Text(candidate))
            ValidateSafeText(value, context, testCase, field, suppressFragileTextClassifiers);
    }

    public static void ValidatePersistedPlan(
        Guid taskId,
        VerificationPlan plan,
        VerificationPlanContext context,
        VerificationLimits limits)
    {
        if (plan.PlanId == Guid.Empty || plan.ImplementationRevisionId != context.ImplementationRevisionId ||
            !string.Equals(plan.ImplementationResultFingerprint, context.ImplementationResultFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.ApprovedRequirementFingerprint, context.ApprovedRequirementFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.ApprovedPlanFingerprint, context.ApprovedPlanFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.GenerationContextFingerprint, context.ContextFingerprint, StringComparison.Ordinal) ||
            plan.GeneratedAt.Offset != TimeSpan.Zero || plan.TestCases.Any(item => item.TestCaseId == Guid.Empty) ||
            plan.TestCases.Select(item => item.TestCaseId).Distinct().Count() != plan.TestCases.Count)
            throw Invalid("The verification plan binding is invalid.");
        if (context.PreviousPlan is null && (plan.SupersedesPlanId is not null || plan.RegenerationReason is not null) ||
            context.PreviousPlan is not null && (plan.SupersedesPlanId != context.PreviousPlan.PlanId ||
                string.IsNullOrWhiteSpace(plan.RegenerationReason) || plan.RegenerationReason.Length > 500))
            throw Invalid("The replacement verification-plan binding is invalid.");
        var candidate = new VerificationPlanCandidate(
            plan.GenerationContextFingerprint, plan.Summary, plan.Scope, plan.Preconditions,
            plan.TestCases.Select(item => new VerificationTestCaseCandidate(
                item.Order, item.Title, item.Objective, item.Category, item.IsRequired, item.Preconditions,
                item.TestData, item.OrderedSteps.Select(step => new VerificationTestStepCandidate(
                    step.Order, step.Instruction, step.ApprovedValidationCommandId, step.ExpectedObservation)).ToArray(),
                item.ExpectedResult, item.NegativeOrEdgeCases, item.RegressionScope, item.EvidenceRequirements,
                item.SafetyNotes, item.OriginTestCaseId, item.RegressionFailureReportIds)).ToArray(),
            plan.Risks, plan.Limitations, plan.EvidenceGuidance, plan.Source, plan.Model, plan.ReasoningEffort);
        if (plan.InitialPlanLanguageOverrideApplied)
            ValidateAppliedInitialPlanLanguageOverride(context, candidate, limits, false);
        else
            ValidateCandidate(context, candidate, limits);
        var withoutFingerprint = plan with { PlanFingerprint = string.Empty };
        if (!IsLowerSha256(plan.PlanFingerprint) || !string.Equals(plan.PlanFingerprint,
                VerificationFingerprint.ComputePlan(taskId, withoutFingerprint), StringComparison.Ordinal))
            throw Invalid("The verification plan fingerprint is invalid.");
        ValidateJsonBounds(plan, limits);
    }

    public static ManualCaseResultRevision CreateCaseResult(
        UpdateManualVerificationCaseCommand command,
        ManualVerificationAttempt attempt,
        VerificationPlan plan,
        DateTimeOffset now,
        VerificationLimits limits)
    {
        if (attempt.Status != ManualVerificationAttemptStatus.InProgress)
            throw Invalid("Completed manual verification attempts are immutable.");
        if (!plan.TestCases.Any(item => item.TestCaseId == command.TestCaseId))
            throw Invalid("The verification case does not belong to the current plan.");
        if (!Enum.IsDefined(command.Result) || command.Result == ManualVerificationCaseResult.NotStarted)
            throw Invalid("A saved verification result must resolve the selected case.");
        Optional(command.Notes, limits.MaximumNotesCharacters, "Verification notes");
        Optional(command.ActualResult, limits.MaximumActualResultCharacters, "Actual result");
        Evidence(command.EvidenceDescriptions, limits);
        Optional(command.NotApplicableReason, limits.MaximumListItemCharacters, "Not-applicable reason");
        if (command.Result == ManualVerificationCaseResult.NotApplicable && string.IsNullOrWhiteSpace(command.NotApplicableReason))
            throw Invalid("NotApplicable requires a reason.");
        if (command.Result != ManualVerificationCaseResult.NotApplicable && command.NotApplicableReason is not null)
            throw Invalid("A not-applicable reason is valid only for NotApplicable.");
        if (command.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked)
            ValidateFailure(command.FailureDetails, limits);
        else if (command.FailureDetails is not null)
            throw Invalid("Failure details are valid only for Failed or Blocked results.");
        var current = attempt.ResultRevisions.Where(item => item.TestCaseId == command.TestCaseId)
            .OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
        if ((current?.RevisionNumber ?? 0) >= limits.MaximumResultRevisionsPerCase)
            throw Invalid("The verification case has reached its result-revision limit.");
        foreach (var value in ResultText(command)) ValidateUserText(value);
        return new ManualCaseResultRevision(
            Guid.NewGuid(), (current?.RevisionNumber ?? 0) + 1, attempt.AttemptId, command.TestCaseId,
            command.Result, now, TrimOptional(command.Notes), TrimOptional(command.ActualResult),
            Trim(command.EvidenceDescriptions), TrimOptional(command.NotApplicableReason), command.FailureDetails,
            current?.ResultRevisionId, command.CommandId);
    }

    public static void ValidateCompletion(
        CompleteManualVerificationCommand command,
        ManualVerificationAttempt attempt,
        VerificationPlan plan,
        VerificationLimits limits)
    {
        if (attempt.Status != ManualVerificationAttemptStatus.InProgress)
            throw Invalid("Completed manual verification attempts are immutable.");
        if (!command.ConfirmedByHuman)
            throw Invalid("Explicit human confirmation is required to complete manual verification.");
        Optional(command.Summary, limits.MaximumNotesCharacters, "Verification completion summary");
        if (command.Summary is not null) ValidateUserText(command.Summary);
        var current = VerificationFingerprint.CurrentResults(attempt).ToDictionary(item => item.TestCaseId);
        if (command.Passed)
        {
            foreach (var regressionCase in plan.TestCases.Where(item => item.RegressionFailureReportIds.Count > 0))
                if (!current.TryGetValue(regressionCase.TestCaseId, out var regressionResult) ||
                    regressionResult.Result != ManualVerificationCaseResult.Passed)
                    throw Invalid("Every regression verification case must be Passed before completion.");
            foreach (var testCase in plan.TestCases.Where(item => item.IsRequired))
            {
                if (!current.TryGetValue(testCase.TestCaseId, out var result) ||
                    result.Result is not (ManualVerificationCaseResult.Passed or ManualVerificationCaseResult.NotApplicable))
                    throw Invalid("Every required verification case must be Passed or valid NotApplicable before completion.");
                if (testCase.EvidenceRequirements.Count > 0 && result.EvidenceDescriptions.Count == 0)
                    throw Invalid("A required verification case is missing its required user-reported evidence description.");
            }
            if (current.Values.Any(result => result.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked))
                throw Invalid("Manual verification cannot pass while any case is Failed or Blocked.");
        }
        else
        {
            var failures = current.Values.Where(result => result.Result is ManualVerificationCaseResult.Failed or
                ManualVerificationCaseResult.Blocked).ToArray();
            if (failures.Length == 0 || failures.Any(result => result.FailureDetails is null))
                throw Invalid("Failed manual verification requires at least one Failed or Blocked case with failure details.");
        }
    }

    public static void ValidatePersistedCaseResult(ManualCaseResultRevision result, VerificationLimits limits)
    {
        if (!Enum.IsDefined(result.Result) || result.Result == ManualVerificationCaseResult.NotStarted)
            throw Invalid("A stored verification result is invalid.");
        Optional(result.Notes, limits.MaximumNotesCharacters, "Verification notes");
        Optional(result.ActualResult, limits.MaximumActualResultCharacters, "Actual result");
        Evidence(result.EvidenceDescriptions, limits);
        Optional(result.NotApplicableReason, limits.MaximumListItemCharacters, "Not-applicable reason");
        if (result.Result == ManualVerificationCaseResult.NotApplicable && string.IsNullOrWhiteSpace(result.NotApplicableReason) ||
            result.Result != ManualVerificationCaseResult.NotApplicable && result.NotApplicableReason is not null)
            throw Invalid("The stored not-applicable result is invalid.");
        if (result.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked)
            ValidateFailure(result.FailureDetails, limits);
        else if (result.FailureDetails is not null)
            throw Invalid("Stored failure details are invalid.");
        foreach (var value in ResultText(result)) ValidateUserText(value);
    }

    public static void ValidateJsonBounds<T>(T value, VerificationLimits limits)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (json.Length > limits.MaximumPersistedJsonCharacters ||
            Encoding.UTF8.GetByteCount(json) > limits.MaximumPersistedJsonBytes)
            throw Invalid("Verification data exceeds its persisted size limit.");
    }

    private static void ValidateFailure(VerificationFailureDetails? failure, VerificationLimits limits)
    {
        if (failure is null) throw Invalid("Failed or Blocked verification requires failure details.");
        Required(failure.Title, limits.MaximumTitleCharacters, "Failure title");
        Required(failure.ExpectedResult, limits.MaximumExpectedResultCharacters, "Failure expected result");
        Required(failure.ActualResult, limits.MaximumActualResultCharacters, "Failure actual result");
        Collection(failure.ReproductionSteps, limits.MaximumReproductionSteps, limits.MaximumListItemCharacters, "reproduction step");
        Collection(failure.EnvironmentNotes, limits.MaximumEnvironmentNotes, limits.MaximumListItemCharacters, "environment note");
        Optional(failure.ErrorMessage, limits.MaximumFailureErrorCharacters, "Failure error message");
        Evidence(failure.EvidenceDescriptions, limits);
        if (!Enum.IsDefined(failure.Severity)) throw Invalid("Failure severity is invalid.");
    }

    private static void Evidence(IReadOnlyList<string> values, VerificationLimits limits)
    {
        if (values is null || values.Count > limits.MaximumEvidenceDescriptions)
            throw Invalid("User-reported evidence exceeds its collection limit.");
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > limits.MaximumEvidenceDescriptionBytes)
                throw Invalid("A user-reported evidence description is empty or oversized.");
        }
    }

    private static void ValidateSafeText(
        string value,
        VerificationPlanContext context,
        VerificationTestCaseCandidate? testCase,
        VerificationPlanTextField field,
        bool suppressFragileTextClassifiers)
    {
        var inspected = AllowsApprovedManualContext(field)
            ? RedactExactApprovedManualTestData(value, context.ApprovedManualTestData ?? [])
            : value;
        if (SensitiveContentDetector.ContainsSensitiveValue(inspected) || HasUnsafeLocalPath(inspected) ||
            HasUnsafePlanUrl(value, field, context.ApprovedManualTestData ?? []))
            throw Invalid("The verification plan contains sensitive or absolute local data.");
        var executionClaim = !suppressFragileTextClassifiers &&
            (CompletedValidationStatusClaim().IsMatch(value) ||
             CompletedBehaviorExecutionClaim().IsMatch(value) ||
             CompletedActorClaim().IsMatch(value) ||
             StandaloneAlreadyCompletedClaim().IsMatch(value));
        var nonOverridableExecutionClaim = NonOverridableCompletedExecutionClaim().IsMatch(value);
        var historicalFailureReference = !suppressFragileTextClassifiers &&
            ExplicitHistoricalFailureClaim().IsMatch(value);
        if (historicalFailureReference && (context.PreviousPlan is null ||
                HasUnknownHistoricalIdentity(value, context) ||
                testCase is not null && !IsAllowedHistoricalFailureReference(value, context, testCase)))
            throw Invalid("The verification plan contains an invalid historical failure reference.",
                VerificationValidationFailureReason.HistoricalFailureClaim);
        if (suppressFragileTextClassifiers && HistoricalGuid().IsMatch(value))
            throw Invalid("The verification plan contains an invalid historical failure reference.");
        if (nonOverridableExecutionClaim || executionClaim &&
            !IsAllowedHistoricalFailureReference(value, context, testCase))
            throw Invalid("The verification plan claimed that manual verification had already been performed.",
                VerificationValidationFailureReason.ManualExecutionClaim);
        var allowed = context.ApprovedPlan.AffectedFiles.Select(item => RepositoryPathRules.Normalize(item.Path))
            .Concat(context.RepositoryEvidence.Select(item => RepositoryPathRules.Normalize(item.RelativePath)))
            .ToHashSet(RepositoryPathRules.Comparer);
        var approvedDirectories = allowed.Select(PathDirectory).Where(directory => directory.Length > 0)
            .ToHashSet(RepositoryPathRules.Comparer);
        foreach (var commandPath in context.ApprovedValidationCommands.SelectMany(command =>
                     CrediblePathCandidates(command.Command, approvedDirectories)))
        {
            var normalized = RepositoryPathRules.Normalize(commandPath.Trim('`', '\'', '"', '.', ',', ';', ':', ')', ']', '}'));
            if (!RepositoryPathRules.IsSafeRelativePath(normalized, 300)) continue;
            allowed.Add(normalized);
            var directory = PathDirectory(normalized);
            if (directory.Length > 0) approvedDirectories.Add(directory);
        }
        foreach (var candidate in CrediblePathCandidates(value, approvedDirectories))
        {
            var path = RepositoryPathRules.Normalize(candidate.Trim('`', '\'', '"', '.', ',', ';', ':', ')', ']', '}'));
            if (RepositoryPathRules.IsSafeRelativePath(path, 300) && !allowed.Contains(path))
                throw Invalid("The verification plan referenced a repository path outside approved evidence.");
        }
    }

    private static bool IsAllowedHistoricalFailureReference(
        string value,
        VerificationPlanContext context,
        VerificationTestCaseCandidate? testCase)
    {
        if (context.PreviousPlan is null || context.PreviousFailureEvidence is not { Count: > 0 } ||
            CurrentExecutionSuccessClaim().IsMatch(value) || HasUnknownHistoricalIdentity(value, context))
            return false;
        if (testCase is null)
            return PriorFailureMarker().IsMatch(value) && FailureReferenceMarker().IsMatch(value);
        if (testCase.OriginTestCaseId is not { } origin || testCase.RegressionFailureReportIds.Count == 0)
            return false;
        var failures = context.PreviousFailureEvidence.ToDictionary(item => item.ResultRevisionId);
        if (!context.PreviousPlan.TestCases.Any(item => item.TestCaseId == origin) ||
            testCase.RegressionFailureReportIds.Any(id => !failures.TryGetValue(id, out var failure) ||
                failure.TestCaseId != origin))
            return false;
        var allowedTextIdentities = testCase.RegressionFailureReportIds.Append(origin)
            .Append(context.PreviousPlan.PlanId)
            .Append(context.FailureAnalysis?.FailedAttemptId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        return HistoricalGuid().Matches(value).Select(match => Guid.Parse(match.Value))
            .All(allowedTextIdentities.Contains);
    }

    private static bool HasUnknownHistoricalIdentity(string value, VerificationPlanContext context)
    {
        var identities = HistoricalGuid().Matches(value).Select(match => Guid.Parse(match.Value)).ToArray();
        if (identities.Length == 0) return false;
        var allowed = (context.PreviousFailureEvidence ?? []).Select(item => item.ResultRevisionId)
            .Concat(context.PreviousPlan?.TestCases.Select(item => item.TestCaseId) ?? [])
            .Append(context.PreviousPlan?.PlanId ?? Guid.Empty)
            .Append(context.FailureAnalysis?.FailedAttemptId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        return identities.Any(id => !allowed.Contains(id));
    }

    private static IEnumerable<string> CrediblePathCandidates(string value, HashSet<string> approvedDirectories)
    {
        var withoutUrls = UrlCandidate().Replace(value, " URL reference ");
        withoutUrls = RelativeUrlCandidate().Replace(withoutUrls, match =>
            HasStrongNeutralReferenceContext(withoutUrls, match) ? " " : StripQueryOrFragment(match.Value));
        withoutUrls = QueryBearingRootFileCandidate().Replace(withoutUrls, match =>
            HasStrongNeutralReferenceContext(withoutUrls, match) ? " " : StripQueryOrFragment(match.Value));
        var slashMatches = SlashTokenCandidate().Matches(withoutUrls).Cast<Match>().ToArray();
        var pointerRanges = slashMatches.Where(match => IsJsonPointer(match.Groups[1].Value) &&
                !approvedDirectories.Any(directory => match.Groups[1].Value.TrimStart('/').StartsWith(
                    directory + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(match => (match.Index, End: match.Index + match.Length)).ToArray();
        foreach (var match in slashMatches)
        {
            var candidate = match.Groups[1].Value.Trim('`', '\'', '"', '.', ',', ';', ':', ')', ']', '}');
            var normalized = RepositoryPathRules.Normalize(candidate);
            var underApprovedDirectory = approvedDirectories.Any(directory => normalized.TrimStart('/').StartsWith(
                directory + "/", StringComparison.OrdinalIgnoreCase));
            if (IsMimeType(normalized) || IsJsonPointer(normalized) && !underApprovedDirectory) continue;
            var finalSegment = normalized.Split('/').LastOrDefault() ?? string.Empty;
            if (KnownExtensionlessFile().IsMatch(finalSegment) || RecognizedExtension().IsMatch(finalSegment) ||
                underApprovedDirectory)
                yield return normalized.TrimStart('/');
        }
        foreach (Match match in KnownExtensionlessToken().Matches(withoutUrls))
            if (IsExplicitFreeTextPathReference(withoutUrls, match)) yield return match.Groups[1].Value;
        foreach (Match match in RootFilenameToken().Matches(withoutUrls))
        {
            if (slashMatches.Any(slash => match.Index >= slash.Index && match.Index < slash.Index + slash.Length)) continue;
            var candidate = match.Groups[1].Value;
            if (CSharpNamespace().IsMatch(candidate) || IsMimeType(candidate)) continue;
            yield return candidate;
        }
    }

    private static string PathDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? string.Empty : path[..separator];
    }

    internal static IReadOnlyList<string> ExtractApprovedManualTestData(EngineeringTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var source = string.Join('\n', new[]
        {
            task.RequirementSummary ?? string.Empty
        }.Concat(task.ClarificationAnswers.SelectMany(answer => new[] { answer.Question, answer.Answer })));
        if (source.Length > 64 * 1024) source = source[..(64 * 1024)];
        var values = new List<string>();
        void Add(string candidate)
        {
            candidate = candidate.TrimEnd('.', ',', ';', ':');
            if (!IsSafeApprovedManualTestData(candidate) || values.Contains(candidate, StringComparer.Ordinal) ||
                values.Count == 8) return;
            values.Add(candidate);
        }
        foreach (Match match in ApprovedCredentialValue().Matches(source))
        {
            var start = Math.Max(0, match.Index - 96);
            var end = Math.Min(source.Length, match.Index + match.Length + 96);
            if (!DemoTestMarker().IsMatch(source[start..end])) continue;
            Add(match.Groups[1].Value);
            if (values.Count == 8) break;
        }
        foreach (var answer in task.ClarificationAnswers)
        {
            var pair = answer.Question + " " + answer.Answer;
            if (!CredentialLabel().IsMatch(answer.Question) || !DemoTestMarker().IsMatch(pair)) continue;
            var exact = ApprovedLiteralOnly().Match(answer.Answer.Trim());
            if (exact.Success) Add(exact.Groups[1].Value);
        }
        return values;
    }

    private static bool IsSafeApprovedManualTestData(string value)
    {
        var normalized = value.ToLowerInvariant();
        if (value.Length is < 3 or > 32 || SensitiveContentDetector.ContainsSensitiveValue(value) ||
            normalized is "input" or "field" or "value" or "masked" or "masking" or "required" or
                "should" or "must" or "can") return false;
        var classes = (value.Any(char.IsLower) ? 1 : 0) + (value.Any(char.IsUpper) ? 1 : 0) +
                      (value.Any(char.IsDigit) ? 1 : 0) + (value.Any(character => "._-".Contains(character)) ? 1 : 0);
        return value.Length < 16 || classes < 3 || value.Distinct().Take(12).Count() < 12;
    }

    private static bool AllowsApprovedManualContext(VerificationPlanTextField field) => field is
        VerificationPlanTextField.Objective or VerificationPlanTextField.Precondition or
        VerificationPlanTextField.TestData or VerificationPlanTextField.OrderedInstruction or
        VerificationPlanTextField.ExpectedObservation or VerificationPlanTextField.ExpectedResult or
        VerificationPlanTextField.EvidenceRequirement or VerificationPlanTextField.RegressionScope or
        VerificationPlanTextField.SafetyNote or VerificationPlanTextField.EvidenceGuidance;

    private static string RedactExactApprovedManualTestData(string value, IReadOnlyList<string> approved)
    {
        var inspected = value;
        foreach (var literal in approved.Where(IsSafeApprovedManualTestData).Distinct(StringComparer.Ordinal))
        {
            var pattern = $@"(?<![A-Za-z0-9._-]){Regex.Escape(literal)}(?![A-Za-z0-9._-])";
            inspected = Regex.Replace(inspected, pattern, "[APPROVED DEMO TEST DATA]",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return inspected;
    }

    private static bool HasUnsafePlanUrl(
        string value,
        VerificationPlanTextField field,
        IReadOnlyList<string> approvedManualTestData)
    {
        foreach (Match match in PlanUrlCandidate().Matches(value))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            if (!AllowsApprovedManualContext(field) || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
                !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) && uri.Host != "127.0.0.1")
                return true;
            string path;
            try { path = Uri.UnescapeDataString(uri.AbsolutePath); }
            catch (UriFormatException) { return true; }
            var parameters = uri.Query + "&" + uri.Fragment;
            if (path.Contains('\\') || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..") ||
                path.Any(char.IsControl) || SensitiveUrlParameter().IsMatch(parameters) ||
                SensitiveContentDetector.ContainsSensitiveValue(parameters) ||
                !string.Equals(parameters, RedactExactApprovedManualTestData(parameters, approvedManualTestData),
                    StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void ValidateUserText(string value)
    {
        if (SensitiveContentDetector.ContainsSensitiveValue(value) || HasUnsafeLocalPath(value))
            throw Invalid("User-reported verification evidence contains sensitive or unsafe absolute local data.");
    }

    private static bool HasUnsafeLocalPath(string value)
    {
        if (FileUri().IsMatch(value) || ConfusableTraversal().IsMatch(value) ||
            QueryOrFragmentTraversal().IsMatch(value)) return true;
        if (TraversalPath().IsMatch(value)) return true;
        var localText = UrlCandidate().Replace(value, " ");
        if (AbsoluteLocalPath().IsMatch(localText) || TraversalPath().IsMatch(localText)) return true;
        var decoded = value;
        for (var pass = 0; pass < 2; pass++)
        {
            try { decoded = Uri.UnescapeDataString(decoded); }
            catch (UriFormatException) { return true; }
            if (TraversalPath().IsMatch(decoded) || ConfusableTraversal().IsMatch(decoded) ||
                QueryOrFragmentTraversal().IsMatch(decoded)) return true;
            localText = UrlCandidate().Replace(decoded, " ");
            if (TraversalPath().IsMatch(localText) || AbsoluteLocalPath().IsMatch(localText)) return true;
        }
        return false;
    }

    private static bool IsMimeType(string value) => MimeType().IsMatch(value);

    private static bool IsJsonPointer(string value)
    {
        if (value.StartsWith("#/", StringComparison.Ordinal)) value = value[1..];
        if (!value.StartsWith("/", StringComparison.Ordinal) || value.Contains('\\')) return false;
        var segments = value.Split('/').Skip(1).ToArray();
        return segments.Length > 0 && segments.All(segment => segment.Length > 0 && ValidPointerSegment(segment));
    }

    private static bool ValidPointerSegment(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
            if (segment[index] == '~' && (index + 1 >= segment.Length || segment[++index] is not ('0' or '1')))
                return false;
        return segment.All(character => !char.IsControl(character) && character is not ' ' and not '"' and not '`');
    }

    private static bool IsExplicitFreeTextPathReference(string value, Match match)
    {
        var before = value[..match.Index];
        var after = value[(match.Index + match.Length)..];
        if (before.EndsWith('`') || before.EndsWith('"') || before.EndsWith('\'') ||
            after.StartsWith('`') || after.StartsWith('"') || after.StartsWith('\'')) return true;
        var context = before[^Math.Min(before.Length, 48)..] + match.Value + after[..Math.Min(after.Length, 48)];
        return ExplicitPathContext().IsMatch(context);
    }

    private static bool HasStrongNeutralReferenceContext(string value, Match match)
    {
        var before = value[..match.Index];
        var after = value[(match.Index + match.Length)..];
        var context = before[^Math.Min(before.Length, 56)..] + match.Value + after[..Math.Min(after.Length, 32)];
        return StrongNeutralReferenceContext().IsMatch(context);
    }

    private static string StripQueryOrFragment(string value)
    {
        var separator = value.IndexOfAny(['?', '#']);
        return separator < 0 ? value : value[..separator];
    }

    private static void Required(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Invalid($"{label} is required.");
        if (value.Length > maximum || Encoding.UTF8.GetByteCount(value) > maximum * 4L)
            throw Invalid($"{label} exceeds its allowed length.");
    }

    private static void Optional(string? value, int maximum, string label)
    {
        if (value is null) return;
        Required(value, maximum, label);
    }

    private static void Collection(IReadOnlyList<string>? values, int maximumCount, int maximumLength, string label)
    {
        if (values is null || values.Count > maximumCount)
            throw Invalid($"The verification {label} collection exceeds its allowed size.");
        foreach (var value in values) Required(value, maximumLength, $"Verification {label}");
    }

    private static IEnumerable<(string Value, VerificationTestCaseCandidate? TestCase, VerificationPlanTextField Field)> Text(
        VerificationPlanCandidate candidate)
    {
        yield return (candidate.Summary, null, VerificationPlanTextField.Other);
        yield return (candidate.Scope, null, VerificationPlanTextField.Other);
        foreach (var value in candidate.Preconditions)
            yield return (value, null, VerificationPlanTextField.Precondition);
        foreach (var value in candidate.Risks.Concat(candidate.Limitations))
            yield return (value, null, VerificationPlanTextField.Other);
        foreach (var value in candidate.EvidenceGuidance)
            yield return (value, null, VerificationPlanTextField.EvidenceGuidance);
        foreach (var testCase in candidate.TestCases)
        {
            yield return (testCase.Title, testCase, VerificationPlanTextField.Other);
            yield return (testCase.Objective, testCase, VerificationPlanTextField.Objective);
            yield return (testCase.ExpectedResult, testCase, VerificationPlanTextField.ExpectedResult);
            foreach (var value in testCase.Preconditions)
                yield return (value, testCase, VerificationPlanTextField.Precondition);
            foreach (var value in testCase.TestData)
                yield return (value, testCase, VerificationPlanTextField.TestData);
            foreach (var value in testCase.NegativeOrEdgeCases)
                yield return (value, testCase, VerificationPlanTextField.Other);
            foreach (var value in testCase.RegressionScope)
                yield return (value, testCase, VerificationPlanTextField.RegressionScope);
            foreach (var value in testCase.EvidenceRequirements)
                yield return (value, testCase, VerificationPlanTextField.EvidenceRequirement);
            foreach (var value in testCase.SafetyNotes)
                yield return (value, testCase, VerificationPlanTextField.SafetyNote);
            foreach (var step in testCase.OrderedSteps)
            {
                yield return (step.Instruction, testCase, VerificationPlanTextField.OrderedInstruction);
                yield return (step.ExpectedObservation, testCase, VerificationPlanTextField.ExpectedObservation);
            }
        }
    }

    private enum VerificationPlanTextField
    {
        Other,
        Objective,
        Precondition,
        TestData,
        OrderedInstruction,
        ExpectedObservation,
        ExpectedResult,
        EvidenceRequirement,
        RegressionScope,
        SafetyNote,
        EvidenceGuidance
    }

    private static IEnumerable<string> ResultText(UpdateManualVerificationCaseCommand command)
    {
        if (command.Notes is not null) yield return command.Notes;
        if (command.ActualResult is not null) yield return command.ActualResult;
        if (command.NotApplicableReason is not null) yield return command.NotApplicableReason;
        foreach (var value in command.EvidenceDescriptions) yield return value;
        if (command.FailureDetails is not { } failure) yield break;
        yield return failure.Title;
        yield return failure.ExpectedResult;
        yield return failure.ActualResult;
        if (failure.ErrorMessage is not null) yield return failure.ErrorMessage;
        foreach (var value in failure.ReproductionSteps.Concat(failure.EnvironmentNotes)
                     .Concat(failure.EvidenceDescriptions)) yield return value;
    }

    private static IEnumerable<string> ResultText(ManualCaseResultRevision result)
    {
        if (result.Notes is not null) yield return result.Notes;
        if (result.ActualResult is not null) yield return result.ActualResult;
        if (result.NotApplicableReason is not null) yield return result.NotApplicableReason;
        foreach (var value in result.EvidenceDescriptions) yield return value;
        if (result.FailureDetails is not { } failure) yield break;
        yield return failure.Title;
        yield return failure.ExpectedResult;
        yield return failure.ActualResult;
        if (failure.ErrorMessage is not null) yield return failure.ErrorMessage;
        foreach (var value in failure.ReproductionSteps.Concat(failure.EnvironmentNotes)
                     .Concat(failure.EvidenceDescriptions)) yield return value;
    }

    private static IReadOnlyList<string> Trim(IEnumerable<string> values) => values.Select(value => value.Trim()).ToArray();
    private static string? TrimOptional(string? value) => value is null ? null : value.Trim();
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static bool IsLowerSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static VerificationPlanCandidate ValidateAppliedInitialPlanLanguageOverride(
        VerificationPlanContext context,
        VerificationPlanCandidate candidate,
        VerificationLimits limits,
        bool requireRuntimeEligibility)
    {
        if (candidate.Source != VerificationPlanSource.OpenAI || context.PreviousPlan is not null ||
            context.PreviousFailureEvidence is { Count: > 0 } || context.FailureAnalysis is not null ||
            context.CorrectionProposal is not null || requireRuntimeEligibility &&
            !context.InitialPlanLanguageOverrideEligible || candidate.TestCases.Any(testCase =>
                testCase.OriginTestCaseId is not null || testCase.RegressionFailureReportIds.Count > 0) ||
            !requireRuntimeEligibility && (!candidate.Limitations.Contains(
                InitialPlanLanguageOverrideLimitation, StringComparer.Ordinal) || candidate.TestCases.Any(testCase =>
                !testCase.SafetyNotes.Contains(InitialPlanLanguageOverrideSafetyNote, StringComparer.Ordinal))))
            throw Invalid("The initial verification-plan language override is not eligible for this plan.");

        var audited = candidate with
        {
            Limitations = AppendOnce(candidate.Limitations, InitialPlanLanguageOverrideLimitation),
            TestCases = candidate.TestCases.Select(testCase => testCase with
            {
                SafetyNotes = AppendOnce(testCase.SafetyNotes, InitialPlanLanguageOverrideSafetyNote)
            }).ToArray()
        };
        foreach (var (value, _, _) in Text(audited))
            if (NonOverridableCompletedExecutionClaim().IsMatch(value))
                throw Invalid("The verification plan claimed that manual verification had already been performed.",
                    VerificationValidationFailureReason.ManualExecutionClaim);
        ValidateCandidateCore(context, audited, limits, true);
        return audited;
    }

    private static IReadOnlyList<string> AppendOnce(IReadOnlyList<string> values, string required) =>
        values.Contains(required, StringComparer.Ordinal) ? values : values.Append(required).ToArray();

    private static VerificationException Invalid(
        string message,
        VerificationValidationFailureReason reason = VerificationValidationFailureReason.General) =>
        new("verification_validation", message, validationFailureReason: reason);

    [GeneratedRegex("""(?i)(?:[a-z]:[\\/]|file://|\\\\[^\\/\s]+[\\/][^\s]+|(?:^|[\s'"`(])/(?:home|users|private|tmp|var|opt|etc)/[^\s'"`]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteLocalPath();

    [GeneratedRegex("""(?:^|[/\\\s'"`(])\.\.(?:[/\\]|$)""", RegexOptions.CultureInvariant)]
    private static partial Regex TraversalPath();

    [GeneratedRegex("""(?:[?&#=]|^)\.\.(?:[/\\]|$)""", RegexOptions.CultureInvariant)]
    private static partial Regex QueryOrFragmentTraversal();

    [GeneratedRegex(@"(?i)\b(?:manual\s+(?:verification|review|attempt)|verification|this\s+(?:test|case)|tests?|build|lint|validation|checks?|all\s+checks|commands?)\b\s+(?:(?:was|were|has|have|had|is|are)\s+(?:already\s+)?(?:been\s+)?(?:run|ran|tested|passed|failed|successful|completed|executed|verified|confirmed|performed|succeeded|worked)|(?:already\s+)?(?:ran|passed|failed|completed|executed|verified|confirmed|performed|succeeded|worked))\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedValidationStatusClaim();

    [GeneratedRegex(@"(?i)\b(?:(?:[a-z][a-z0-9-]*\s+){0,4}flow|application|implementation|corrected\s+(?:implementation|behavior)|behavior)\b\s+(?:(?:was|were|has|have|had|is|are)\s+(?:already\s+)?(?:been\s+)?(?:run|ran|tested|completed|executed|verified|confirmed|performed|worked)|(?:already\s+)?(?:ran|tested|completed|executed|verified|confirmed|performed|worked))\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedBehaviorExecutionClaim();

    [GeneratedRegex(@"(?i)\b(?:forge|the\s+user|user|human|operator|reviewer)\b\s+(?:(?:was|were|has|have|had)\s+(?:already\s+)?(?:been\s+)?(?:run|tested|completed|executed|verified|confirmed|performed)|(?:already\s+)?(?:ran|tested|passed|failed|completed|executed|verified|confirmed|performed|approved))\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompletedActorClaim();

    [GeneratedRegex(@"(?i)\b(?:already|previously)\s+(?:been\s+)?(?:run|tested|passed|completed|executed|verified|confirmed|performed)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneAlreadyCompletedClaim();

    [GeneratedRegex(@"(?i)\b(?:forge\s+(?:executed|ran|verified|confirmed)|verification\s+(?:already\s+passed|has\s+passed)|manual\s+verification\s+was\s+(?:completed|performed)|(?:build\s+was|tests\s+were)\s+executed\s+by\s+forge|user\s+already\s+confirmed|human\s+already\s+approved)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NonOverridableCompletedExecutionClaim();

    [GeneratedRegex("""(?i)\b(?:username|user|password|passwd|pwd)\b\s*(?:(?::|=)|(?:is|use)\s+)?['\"]?([A-Za-z0-9._-]{3,32})""", RegexOptions.CultureInvariant)]
    private static partial Regex ApprovedCredentialValue();

    [GeneratedRegex(@"(?i)\b(?:username|user|password|passwd|pwd)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialLabel();

    [GeneratedRegex("""^['\"]?([A-Za-z0-9._-]{3,32})['\"]?[.,;:]?$""", RegexOptions.CultureInvariant)]
    private static partial Regex ApprovedLiteralOnly();

    [GeneratedRegex(@"(?i)\b(?:demo|demo[- ]only|test|testing|test[- ]only|sample|example|synthetic|non[- ]production)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DemoTestMarker();

    [GeneratedRegex("""(?i)\b[a-z][a-z0-9+.-]*://[^\s'\"`]+""", RegexOptions.CultureInvariant)]
    private static partial Regex PlanUrlCandidate();

    [GeneratedRegex(@"(?i)(?:^|[?&#;])(?:access[_-]?token|auth[_-]?token|token|api[_-]?key|key|password|passwd|pwd|username|user|client[_-]?secret|secret|credential|session|jwt|bearer|code|sig|signature)=", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveUrlParameter();

    [GeneratedRegex(@"(?i)\b(?:previous(?:ly)?|prior|historical|superseded|attempt\s+1)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PriorFailureMarker();

    [GeneratedRegex(@"(?i)\b(?:fail(?:ed|ure)?|blocked|user[- ]reported|repeat|recheck)\b", RegexOptions.CultureInvariant)]
    private static partial Regex FailureReferenceMarker();

    [GeneratedRegex(@"(?i)\b(?:(?:(?:previous(?:ly)?|prior|historical)\b.{0,100}\b(?:fail(?:ed|ure)?|blocked|result|verification|manual\s+(?:review|attempt|verification)))|superseded\s+(?:verification\s+)?plan|attempt\s+1|origin\s+test\s+case|regression\s+failure\s+report|failed\s+result\s+revision|(?:repeat|recheck)\s+(?:the\s+)?(?:previous(?:ly)?\s+)?(?:failed\s+verification|failure\s+result)(?:\s+(?:revision\s+)?(?:id\s+)?[A-Za-z0-9-]+)?|(?:attempt|plan|analysis|proposal|result(?:\s+revision)?)\s+(?:id\s+)?[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitHistoricalFailureClaim();

    [GeneratedRegex(@"(?i)\b(?:corrected\s+(?:implementation|behavior).{0,60}(?:verified|passed|confirmed|successful)|forge.{0,60}(?:verified|confirmed|executed|passed)|this\s+(?:test|case).{0,60}(?:executed|passed|completed|successful)|manual\s+verification\s+has\s+already\s+passed)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CurrentExecutionSuccessClaim();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalGuid();

    [GeneratedRegex("""(?<![A-Za-z0-9])([/\\]?[A-Za-z0-9_~+.-]+(?:[/\\][A-Za-z0-9_~+.-]+)+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SlashTokenCandidate();

    [GeneratedRegex(@"(?i)\.(?:cs|fs|vb|tsx?|jsx?|json|ya?ml|xml|md|txt|html?|css|scss|sql|py|java|kt|go|rs|rb|php|sh|ps1|csproj|fsproj|vbproj|slnx?|props|targets|config|toml|ini)$", RegexOptions.CultureInvariant)]
    private static partial Regex RecognizedExtension();

    [GeneratedRegex(@"(?i)^(?:Dockerfile|Makefile|Procfile|Jenkinsfile|Gemfile|Rakefile|Vagrantfile|\.gitignore|\.gitattributes|\.editorconfig)$", RegexOptions.CultureInvariant)]
    private static partial Regex KnownExtensionlessFile();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.-])((?:Dockerfile|Makefile|Procfile|Jenkinsfile|Gemfile|Rakefile|Vagrantfile|\.gitignore|\.gitattributes|\.editorconfig))(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KnownExtensionlessToken();

    [GeneratedRegex(@"(?i)^(?:application|text|image|audio|video|multipart|message|font|model)/[A-Za-z0-9!#$&^_.+*-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MimeType();

    [GeneratedRegex("""(?i)\bhttps?://[^\s'"`]+""", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCandidate();

    [GeneratedRegex("""(?i)(?<![A-Za-z0-9_.-])(?:\.?[A-Za-z0-9_.~-]+/)+[A-Za-z0-9_.~-]+(?:\?[^\s'"`#]+|#[^\s'"`]+)(?![A-Za-z0-9])""", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeUrlCandidate();

    [GeneratedRegex("""(?i)(?<![A-Za-z0-9_.-])(?:(?:Dockerfile|Makefile|Procfile|Jenkinsfile|Gemfile|Rakefile|Vagrantfile|\.gitignore|\.gitattributes|\.editorconfig)|[A-Za-z0-9_.~-]+\.(?:cs|fs|vb|tsx?|jsx?|json|ya?ml|xml|md|txt|html?|css|scss|sql|py|java|kt|go|rs|rb|php|sh|ps1|csproj|fsproj|vbproj|slnx?|props|targets|config|toml|ini))(?:\?[^\s'"`#]+|#[^\s'"`]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex QueryBearingRootFileCandidate();

    [GeneratedRegex(@"(?i)\bfile://", RegexOptions.CultureInvariant)]
    private static partial Regex FileUri();

    [GeneratedRegex(@"(?:\.\.|[A-Za-z]:)[∕⁄＼]", RegexOptions.CultureInvariant)]
    private static partial Regex ConfusableTraversal();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.-])([A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*\.(?:cs|fs|vb|tsx?|jsx?|json|ya?ml|xml|md|txt|html?|css|scss|sql|py|java|kt|go|rs|rb|php|sh|ps1|csproj|fsproj|vbproj|slnx?|props|targets|config|toml|ini))(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RootFilenameToken();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpNamespace();

    [GeneratedRegex(@"(?i)(?:\b(?:inspect|open|edit|modify|update|delete|create|read|write|path|file|repository|repo)\b|\b(?:exists?|present|located|stored)\b|\bthe\s+(?:Dockerfile|Makefile|Procfile|Jenkinsfile|Gemfile|Rakefile|Vagrantfile|\.gitignore|\.gitattributes|\.editorconfig)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPathContext();

    [GeneratedRegex(@"(?i)\b(?:url|uri|route|hyperlink|link|reference|example)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NeutralReferenceContext();

    [GeneratedRegex(@"(?i)\b(?:documentation\s+url|relative\s+url|hyperlink|browser\s+(?:route|path)|relative[- ]link|reference\s+example|url\s+(?:may|might|can)\s+look)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StrongNeutralReferenceContext();
}
