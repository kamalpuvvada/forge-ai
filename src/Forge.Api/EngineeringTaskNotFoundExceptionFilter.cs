using Forge.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Forge.Api;

public sealed class EngineeringTaskNotFoundExceptionFilter(
    ILogger<EngineeringTaskNotFoundExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not EngineeringTaskNotFoundException) return;

        var traceId = context.HttpContext.TraceIdentifier;
        logger.LogInformation("Engineering task was not found. TraceId: {TraceId}", traceId);
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Engineering task not found",
            Detail = "The requested engineering task was not found.",
            Extensions =
            {
                ["code"] = "task_not_found",
                ["traceId"] = traceId
            }
        })
        {
            StatusCode = StatusCodes.Status404NotFound
        };
        context.ExceptionHandled = true;
    }
}
