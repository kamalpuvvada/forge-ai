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
