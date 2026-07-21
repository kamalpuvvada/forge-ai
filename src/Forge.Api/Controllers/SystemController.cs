using Forge.Api.Contracts;
using Forge.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(ForgeAiOptions options, OpenAIConfigurationState configurationState,
    IDeliveryExecutableAvailability? deliveryAvailability = null) : ControllerBase
{
    [HttpGet("capabilities")]
    [ProducesResponseType<SystemCapabilitiesResponse>(StatusCodes.Status200OK)]
    public ActionResult<SystemCapabilitiesResponse> GetCapabilities()
    {
        var isFake = string.Equals(options.Mode, ForgeAiModes.Fake, StringComparison.OrdinalIgnoreCase);
        var isOpenAi = string.Equals(options.Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase);
        var clarificationConfigured = isFake || isOpenAi && options.IsClarificationConfigurationComplete(configurationState.HasApiKey);
        var planningConfigured = isFake || isOpenAi && options.IsPlanningConfigurationComplete(configurationState.HasApiKey);
        var openAiImplementationAvailable = isOpenAi && options.IsImplementationConfigurationComplete(configurationState.HasApiKey);
        var implementationConfigured = isFake || openAiImplementationAvailable;
        var verificationConfigured = isFake || isOpenAi && options.IsVerificationPlanningConfigurationComplete(configurationState.HasApiKey);
        var failureAnalysisConfigured = isFake || isOpenAi && options.IsFailureAnalysisConfigurationComplete(configurationState.HasApiKey);
        var provider = isFake ? "Fake" : isOpenAi ? "OpenAI" : "Unavailable";
        var deliveryGitAvailable = deliveryAvailability?.GitAvailable ?? IsExecutableAvailable("git");
        var deliveryGitHubCliAvailable = deliveryAvailability?.GitHubCliAvailable ?? IsExecutableAvailable("gh");
        var deliveryConfigured = deliveryGitAvailable && deliveryGitHubCliAvailable;
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
            isFake ? "Deterministic Fake" : isOpenAi ? "OpenAI" : "Unavailable",
            isFake ? null : options.ImplementationModel,
            isFake ? null : options.ImplementationReasoningEffort,
            implementationConfigured,
            clarificationConfigured && planningConfigured,
            true,
            isFake || isOpenAi && planningConfigured,
            implementationConfigured,
            true,
            implementationConfigured && failureAnalysisConfigured,
            false,
            implementationConfigured,
            deliveryConfigured,
            isFake,
            openAiImplementationAvailable,
            false,
            deliveryConfigured,
            deliveryConfigured,
            deliveryConfigured,
            provider,
            options.VerificationPlanningModel,
            options.VerificationPlanningReasoningEffort,
            verificationConfigured,
            true,
            false,
            failureAnalysisConfigured,
            failureAnalysisConfigured,
            implementationConfigured && failureAnalysisConfigured,
            deliveryConfigured,
            false,
            deliveryGitAvailable,
            deliveryGitHubCliAvailable));
    }

    private static bool IsExecutableAvailable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : new[] { string.Empty };
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => extensions.Any(extension => System.IO.File.Exists(Path.Combine(directory, name + extension))));
    }
}

public sealed record OpenAIConfigurationState(bool HasApiKey);
