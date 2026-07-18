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
    public ActionResult<SystemCapabilitiesResponse> GetCapabilities() => Ok(new SystemCapabilitiesResponse(
        options.Mode,
        options.ClarificationModel,
        options.ClarificationReasoningEffort,
        string.Equals(options.Mode, ForgeAiModes.Fake, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(options.Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase) &&
             options.IsOpenAiConfigurationComplete(configurationState.HasApiKey)),
        true,
        string.Equals(options.Mode, ForgeAiModes.Fake, StringComparison.OrdinalIgnoreCase),
        false,
        false,
        false,
        false));
}

public sealed record OpenAIConfigurationState(bool HasApiKey);
