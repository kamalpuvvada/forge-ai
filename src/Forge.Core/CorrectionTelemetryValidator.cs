namespace Forge.Core;

internal static class CorrectionTelemetryValidator
{
    internal static void Validate(EngineeringTask task,
        IReadOnlyList<VerificationLogicalCallRecord> logicalCalls,
        IReadOnlyList<Guid> modelCallIds,
        IReadOnlyList<VerificationProviderResponseTelemetry> responses,
        int logicalCount, int physicalCount, int possibleCount, int undispatchedCount, int activeCount,
        DateTimeOffset startedAt, DateTimeOffset leaseExpiresAt, DateTimeOffset updatedAt,
        DateTimeOffset? completedAt, ModelCallStage stage, bool terminal)
    {
        if (logicalCount != logicalCalls.Count || logicalCount < 0 ||
            logicalCalls.Select(item => item.LogicalCallId).Distinct().Count() != logicalCalls.Count ||
            logicalCalls.Any(item => item.LogicalCallId == Guid.Empty || item.StartedAt.Offset != TimeSpan.Zero ||
                item.StartedAt < startedAt || item.StartedAt > leaseExpiresAt) ||
            modelCallIds.Distinct().Count() != modelCallIds.Count || modelCallIds.Count > logicalCount ||
            responses.Select(item => item.LogicalCallId).Distinct().Count() != responses.Count ||
            responses.Any(item => logicalCalls.All(call => call.LogicalCallId != item.LogicalCallId))) Corrupt();

        var physical = 0; var possible = 0; var undispatched = 0; var active = 0;
        foreach (var logical in logicalCalls)
        {
            var response = responses.SingleOrDefault(item => item.LogicalCallId == logical.LogicalCallId);
            var call = modelCallIds.Contains(logical.LogicalCallId)
                ? task.ModelCalls.SingleOrDefault(item => item.Id == logical.LogicalCallId)
                : null;
            if (modelCallIds.Contains(logical.LogicalCallId) && call is null || call is not null &&
                (call.Stage != stage || call.StartedAt != logical.StartedAt || call.StartedAt.Offset != TimeSpan.Zero ||
                 call.CompletedAt.Offset != TimeSpan.Zero || call.CompletedAt < call.StartedAt ||
                 call.CompletedAt > updatedAt || call.VerificationDispatchDisposition is null ||
                 !SafeIdentifier(call.ProviderResponseId) || !SafeIdentifier(call.ProviderRequestId) ||
                 !VerificationUsage.IsInternallyConsistent(call.InputTokens, call.CachedInputTokens,
                     call.OutputTokens, call.ReasoningTokens))) Corrupt();
            if (response is not null)
            {
                ValidateResponse(response, startedAt, updatedAt);
                if (call is not null && (call.VerificationDispatchDisposition != VerificationCallDispatchDisposition.ResponseReceived ||
                    call.ProviderHttpStatusCode != response.HttpStatusCode || call.ProviderResponseId != response.ProviderResponseId ||
                    call.ProviderRequestId != response.ProviderRequestId || call.InputTokens != response.InputTokens ||
                    call.CachedInputTokens != response.CachedInputTokens || call.OutputTokens != response.OutputTokens ||
                    call.ReasoningTokens != response.ReasoningTokens || call.StartedAt != response.StartedAt ||
                    call.CompletedAt < response.ReceivedAt || call.ProviderUsageAvailability is { } availability &&
                        availability != response.EffectiveUsageAvailability)) Corrupt();
                physical++;
            }
            else if (call?.VerificationDispatchDisposition == VerificationCallDispatchDisposition.ResponseReceived)
            {
                if (call.ProviderHttpStatusCode is not (>= 100 and <= 599)) Corrupt();
                physical++;
            }
            else if (call?.VerificationDispatchDisposition == VerificationCallDispatchDisposition.PossiblyDispatched)
                possible++;
            else if (call?.VerificationDispatchDisposition == VerificationCallDispatchDisposition.DefinitelyNotDispatched)
                undispatched++;
            else if (terminal) possible++;
            else active++;
        }
        if (physical != physicalCount || possible != possibleCount || undispatched != undispatchedCount ||
            active != activeCount || physical + possible + undispatched + active != logicalCount ||
            terminal && active != 0 || completedAt is { } completed &&
                (completed.Offset != TimeSpan.Zero || completed < startedAt || completed > updatedAt) ||
            terminal != (completedAt is not null)) Corrupt();
    }

    private static void ValidateResponse(VerificationProviderResponseTelemetry response,
        DateTimeOffset attemptStartedAt, DateTimeOffset attemptUpdatedAt)
    {
        if (response.StartedAt.Offset != TimeSpan.Zero || response.ReceivedAt.Offset != TimeSpan.Zero ||
            response.StartedAt < attemptStartedAt || response.ReceivedAt < response.StartedAt ||
            response.ReceivedAt > attemptUpdatedAt || response.DispatchDisposition != VerificationCallDispatchDisposition.ResponseReceived ||
            response.HttpStatusCode is < 100 or > 599 || !Enum.IsDefined(response.Status) ||
            !SafeIdentifier(response.ProviderResponseId) || !SafeIdentifier(response.ProviderRequestId) ||
            response.IncompleteReason is { Length: > 80 } ||
            !VerificationUsage.IsInternallyConsistent(response.InputTokens, response.CachedInputTokens,
                response.OutputTokens, response.ReasoningTokens) ||
            response.UsageAvailable is { } legacy &&
                !VerificationUsage.LegacyBooleanMatches(legacy, response.EffectiveUsageAvailability)) Corrupt();
        var compatible = response.Status switch
        {
            VerificationProviderResponseStatus.Completed => response.HttpStatusCode is >= 200 and < 300,
            VerificationProviderResponseStatus.Incomplete or VerificationProviderResponseStatus.Failed or
                VerificationProviderResponseStatus.Cancelled => response.HttpStatusCode is >= 200 and < 600,
            _ => false
        };
        if (!compatible) Corrupt();
    }

    private static bool SafeIdentifier(string? value) => value is null || value.Length is > 0 and <= 200 &&
        value.All(character => character is >= '!' and <= '~') &&
        !value.Contains('/') && !value.Contains('\\') && !SensitiveContentDetector.ContainsSensitiveValue(value);

    private static void Corrupt() => throw new TaskDataCorruptException(
        "Stored correction data is invalid or incomplete. The task cannot be resumed automatically.");
}
