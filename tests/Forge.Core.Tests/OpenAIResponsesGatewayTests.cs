#pragma warning disable OPENAI001
using Forge.Infrastructure;
using OpenAI.Responses;

namespace Forge.Core.Tests;

public sealed class OpenAIResponsesGatewayTests
{
    [Fact]
    public void Installed_sdk_response_status_values_map_to_normalized_boundary()
    {
        Assert.Equal(OpenAIResponseStatus.Completed, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Completed));
        Assert.Equal(OpenAIResponseStatus.Incomplete, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Incomplete));
        Assert.Equal(OpenAIResponseStatus.Failed, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Failed));
        Assert.Equal(OpenAIResponseStatus.Cancelled, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Cancelled));
    }

    [Fact]
    public void Installed_sdk_incomplete_reasons_map_distinctly()
    {
        Assert.Equal(OpenAIResponseIncompleteReason.MaxOutputTokens,
            SdkOpenAIResponsesGateway.NormalizeIncompleteReason(ResponseIncompleteStatusReason.MaxOutputTokens));
        Assert.Equal(OpenAIResponseIncompleteReason.ContentFilter,
            SdkOpenAIResponsesGateway.NormalizeIncompleteReason(ResponseIncompleteStatusReason.ContentFilter));
    }
}
