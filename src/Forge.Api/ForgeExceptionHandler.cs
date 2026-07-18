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
            KeyNotFoundException => (404, "Engineering task not found", exception.Message, "task_not_found"),
            WorkflowException => (409, "Invalid workflow action", exception.Message, "workflow_conflict"),
            RepositoryDiscoveryException discovery => discovery.Category switch
            {
                "inaccessible_path" => (403, "Repository path inaccessible", discovery.Message, "repository_inaccessible"),
                "unsafe_path" => (400, "Unsafe repository path", discovery.Message, "repository_unsafe_path"),
                "analysis_limits" => (422, "Repository analysis limit", discovery.Message, "repository_analysis_limit"),
                _ => (400, "Repository path invalid", discovery.Message, "repository_missing_path")
            },
            PlanningException planning => planning.Category switch
            {
                "stale_snapshot" => (409, "Repository snapshot is stale", planning.Message, "stale_snapshot"),
                "planning_configuration" => (503, "Planning unavailable", planning.Message, "planning_configuration"),
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
}
