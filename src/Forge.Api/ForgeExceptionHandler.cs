using Forge.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api;

public sealed class ForgeExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ForgeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail, code) = exception switch
        {
            EngineeringTaskNotFoundException => (404, "Engineering task not found",
                "The requested engineering task was not found.", "task_not_found"),
            WorkflowException => (409, "Invalid workflow action", exception.Message, "workflow_conflict"),
            TaskConcurrencyException => (409, "Task changed concurrently", exception.Message, "task_concurrency_conflict"),
            TaskDataCorruptException => (409, "Stored task data is invalid", exception.Message, "task_data_corrupt"),
            TaskPersistenceException => (503, "Task persistence unavailable",
                "Task persistence is temporarily unavailable. Retry the request after storage access is restored.",
                "task_persistence_unavailable"),
            RepositoryDiscoveryException discovery => discovery.Category switch
            {
                "inaccessible_path" => (403, "Repository path inaccessible", discovery.Message, "repository_inaccessible"),
                "unsafe_path" => (400, "Unsafe repository path", discovery.Message, "repository_unsafe_path"),
                "analysis_limits" => (422, "Repository analysis limit", discovery.Message, "repository_analysis_limit"),
                _ => (400, "Repository path invalid", discovery.Message, "repository_missing_path")
            },
            ImplementationException implementation => MapImplementationFailure(implementation),
            CorrectionException correction => MapCorrectionFailure(correction),
            DeliveryException delivery => MapDeliveryFailure(delivery),
            VerificationException verification => MapVerificationFailure(verification),
            PlanningProviderException { Category: "missing_direct_evidence" } provider => (
                422,
                "Repository evidence does not support the plan",
                provider.Message,
                "missing_direct_evidence"),
            PlanningProviderException provider => (
                provider.Category is "rate_limit" or "timeout" ? 503 : 502,
                "AI planning failure",
                provider.Message,
                $"planning_{provider.Category}"),
            PlanningException planning => planning.Category switch
            {
                "stale_snapshot" => (409, "Repository snapshot is stale", planning.Message, "stale_snapshot"),
                "insufficient_evidence" => (422, "Insufficient repository evidence", planning.Message, "insufficient_evidence"),
                "planning_configuration" => (503, "Planning unavailable", planning.Message, "planning_configuration"),
                "plan_constraint_violation" => (422, "Implementation plan conflicts with approved scope",
                    planning.Message, "plan_constraint_violation"),
                "plan_revision_no_change" => (422, "Plan correction made no structural change",
                    planning.Message, "plan_revision_no_change"),
                "plan_revision_restore_failure" => (409, "Previous proposed plan could not be restored",
                    planning.Message, "plan_revision_restore_failure"),
                _ => (422, "Invalid implementation plan", planning.Message, "invalid_plan")
            },
            ClarificationConfigurationException => (503, "AI configuration error", exception.Message, "ai_configuration"),
            ClarificationProviderException provider => (
                provider.Category is "rate_limit" or "timeout" ? 503 : 502,
                "AI provider failure",
                provider.Message,
                $"ai_{provider.Category}"),
            ArgumentException => (400, "Invalid request", exception.Message, "invalid_request"),
            _ => (500, "Unexpected server failure", "The server could not complete the request.", "server_error")
        };

        if (status == 500)
            logger.LogError(exception, "Unhandled Forge API failure. TraceId: {TraceId}", httpContext.TraceIdentifier);
        else
            logger.LogWarning("Forge API request failed safely with {Code}. TraceId: {TraceId}", code, httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions = { ["code"] = code, ["traceId"] = httpContext.TraceIdentifier }
            },
            Exception = exception
        });
    }

    private static (int Status, string Title, string Detail, string Code) MapImplementationFailure(
        ImplementationException exception)
    {
        var safe = SensitiveContentDetector.ContainsSensitiveValue(exception.Category) ||
                   SensitiveContentDetector.ContainsSensitiveValue(exception.Message)
            ? new ImplementationException("implementation_failure",
                "Implementation generation failed safely.", exception.RecoveryRequired)
            : exception;
        return safe.Category switch
        {
            "implementation_configuration" or "implementation_workspace_configuration" =>
                (503, "Implementation unavailable", safe.Message, safe.Category),
            "implementation_rate_limit" or "implementation_timeout" =>
                (503, "AI implementation temporarily unavailable", safe.Message, safe.Category),
            "implementation_authentication" or "implementation_permission" or
                "implementation_model_unavailable" or "implementation_provider_error" or
                "implementation_invalid_request" =>
                (502, "AI implementation failure", safe.Message, safe.Category),
            "implementation_repository_not_git" or "implementation_repository_dirty" or
                "implementation_base_changed" or "implementation_repository_state" or
                "implementation_workspace_conflict" or "implementation_active_checkout_changed" or
                "implementation_recovery_required" =>
                (409, "Implementation workspace conflict", safe.Message, safe.Category),
            "implementation_unsafe_path" =>
                (400, "Unsafe implementation path", safe.Message, safe.Category),
            _ => (422, "Implementation generation rejected", safe.Message, safe.Category)
        };
    }

    private static (int Status, string Title, string Detail, string Code) MapCorrectionFailure(
        CorrectionException exception)
    {
        var message = SensitiveContentDetector.ContainsSensitiveValue(exception.Message)
            ? "The correction workflow failed safely."
            : exception.Message;
        return exception.Category switch
        {
            "correction_stale_binding" or "correction_recovery_required" or "correction_revision_limit" =>
                (409, "Correction state changed", message, exception.Category),
            "failure_analysis_configuration" or "failure_analysis_rate_limit" or "failure_analysis_timeout" =>
                (503, "Failure analysis unavailable", message, exception.Category),
            "failure_analysis_authentication" or "failure_analysis_permission" or
                "failure_analysis_model_unavailable" or "failure_analysis_provider_error" or
                "failure_analysis_invalid_request" or "failure_analysis_invalid_structured_output" or
                "failure_analysis_incomplete_response" or "failure_analysis_unexpected_output" =>
                (502, "Failure-analysis provider failure", message, exception.Category),
            "unsupported_failure_classification" or "correction_unsupported_classification" =>
                (409, "Correction route unavailable", message, exception.Category),
            _ => (422, "Correction rejected", message, exception.Category)
        };
    }

    internal static (int Status, string Title, string Detail, string Code) MapDeliveryFailure(
        DeliveryException exception)
    {
        var message = SensitiveContentDetector.ContainsSensitiveValue(exception.Message)
            ? "Delivery failed safely."
            : exception.Message;
        return exception.Category switch
        {
            "delivery_authentication_unavailable" => (503, "GitHub authentication unavailable", message, exception.Category),
            "delivery_remote_invalid" or "delivery_remote_conflict" or "delivery_branch_conflict" or
                "delivery_push_destination_mismatch" or
                "delivery_stale_binding" or "delivery_workspace_unavailable" or
                "delivery_scope_mismatch" or "delivery_recovery_required" =>
                (409, "Delivery state changed", message, exception.Category),
            "delivery_failed_before_mutation" => (503, "Delivery failed before mutation", message, exception.Category),
            _ => (422, "Delivery rejected", message, exception.Category)
        };
    }

    internal static (int Status, string Title, string Detail, string Code) MapVerificationFailure(
        VerificationException exception)
    {
        var safeMessage = SensitiveContentDetector.ContainsSensitiveValue(exception.Message)
            ? "Manual verification could not be updated safely."
            : exception.Message;
        return exception.Category switch
        {
            "verification_plan_not_found" or "verification_attempt_not_found" or "verification_case_not_found" =>
                (404, "Verification record not found", safeMessage, exception.Category),
            "verification_configuration" or "verification_authentication" or "verification_permission" or
                "verification_model_unavailable" or "verification_rate_limit" or "verification_timeout" or
                "verification_cancelled" or "verification_provider_error" or "verification_invalid_request" =>
                (503, "Verification planning unavailable", safeMessage, exception.Category),
            "verification_refusal" or "verification_output_truncated" or "verification_content_filter" or
                "verification_empty_response" or "verification_unexpected_output" or
                "verification_incomplete_response" or "verification_invalid_structured_output" or
                "verification_validation_rejected" =>
                (502, "Verification-plan provider failure", safeMessage, exception.Category),
            "verification_stale_binding" or "verification_workflow" =>
                (409, "Verification state changed", safeMessage, exception.Category),
            _ => (422, "Manual verification rejected", safeMessage, exception.Category)
        };
    }
}
