using Forge.Api.Contracts;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(ForgeAiOptions options, OpenAIConfigurationState configurationState) : ControllerBase
{
    [HttpGet("capabilities")]
    [ProducesResponseType<SystemCapabilitiesResponse>(StatusCodes.Status200OK)]
    public ActionResult<SystemCapabilitiesResponse> GetCapabilities()
    {
        var isFake = string.Equals(options.Mode, ForgeAiModes.Fake, StringComparison.OrdinalIgnoreCase);
        var isOpenAi = string.Equals(options.Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase);
        var clarificationConfigured = isFake || isOpenAi && options.IsClarificationConfigurationComplete(configurationState.HasApiKey);
        var planningConfigured = isFake || isOpenAi && options.IsPlanningConfigurationComplete(configurationState.HasApiKey);
        var implementationConfigured = isFake;
        var provider = isFake ? "Fake" : isOpenAi ? "OpenAI" : "Unavailable";
        return Ok(new SystemCapabilitiesResponse(
            options.Mode,
            provider,
            options.ClarificationModel,
            options.ClarificationReasoningEffort,
            clarificationConfigured,
            provider,
            options.PlanningModel,
            options.PlanningReasoningEffort,
            planningConfigured,
            isFake ? "Deterministic Fake" : "Unavailable",
            isFake ? null : options.ImplementationModel,
            isFake ? null : options.ImplementationReasoningEffort,
            implementationConfigured,
            clarificationConfigured && planningConfigured,
            true,
            isFake || isOpenAi && planningConfigured,
            implementationConfigured,
            false,
            implementationConfigured,
            false));
    }
}

public sealed record OpenAIConfigurationState(bool HasApiKey);
