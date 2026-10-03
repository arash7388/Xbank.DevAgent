using Anthropic;
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Maf.Handoff.Core;

public static class ModelChatClientFactory
{
    public static IChatClient Create(WorkflowSettings settings)
    {
        settings.Validate();
        Uri endpoint = new(settings.ModelEndpoint);
        if (endpoint.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            AnthropicClient client = new() { ApiKey = settings.ApiKey };
            return client.AsIChatClient(settings.Model);
        }

        if (endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            // The provider-native adapter writes TextReasoningContent back to DeepSeek's
            // reasoning_content field on every assistant tool-call turn.
            DeepSeekUsageCapture usageCapture = new() { InnerHandler = new HttpClientHandler() };
            HttpClient httpClient = new(usageCapture);
            IChatClient deepSeekClient = new DeepSeekClient(settings.ApiKey, new DeepSeekClientOptions
                { Transport = new HttpClientPipelineTransport(httpClient) })
                .GetChatClient(settings.Model)
                .AsIChatClient();
            return new DeepSeekToolHistoryChatClient(
                new DeepSeekUsagePreservingChatClient(deepSeekClient, usageCapture, httpClient));
        }

        OpenAIClient openAiClient = new(
            new ApiKeyCredential(settings.ApiKey),
            new OpenAIClientOptions { Endpoint = endpoint });

        return openAiClient.GetChatClient(settings.Model).AsIChatClient();
    }
}
