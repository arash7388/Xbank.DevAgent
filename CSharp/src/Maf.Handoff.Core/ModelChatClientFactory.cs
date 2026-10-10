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
        => Create(settings, new HttpClientHandler());

    internal static IChatClient Create(WorkflowSettings settings, HttpMessageHandler handler)
    {
        settings.Validate();
        Uri endpoint = new(settings.ModelEndpoint);
        if (endpoint.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            HttpClient httpClient = new(new AiRequestRetryHandler(handler))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            AnthropicClient client = new() { ApiKey = settings.ApiKey, HttpClient = httpClient, MaxRetries = 0 };
            return new TransportOwnedChatClient(client.AsIChatClient(settings.Model), httpClient);
        }

        if (endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            // The provider-native adapter writes TextReasoningContent back to DeepSeek's
            // reasoning_content field on every assistant tool-call turn.
            DeepSeekUsageCapture usageCapture = new() { InnerHandler = new AiRequestRetryHandler(handler) };
            HttpClient httpClient = new(usageCapture);
            IChatClient deepSeekClient = new DeepSeekClient(settings.ApiKey, new DeepSeekClientOptions
                { Transport = new HttpClientPipelineTransport(httpClient), RetryPolicy = new ClientRetryPolicy(0) })
                .GetChatClient(settings.Model)
                .AsIChatClient();
            return new DeepSeekToolHistoryChatClient(
                new DeepSeekUsagePreservingChatClient(deepSeekClient, usageCapture, httpClient));
        }

        HttpMessageHandler transportHandler = new AiRequestRetryHandler(handler);
        if (AifaRequestContext.IsAifa(endpoint))
            transportHandler = new AifaRequestHandler(settings.AifaContext!, transportHandler);
        HttpClient openAiTransport = new(transportHandler);
        OpenAIClient openAiClient = new(
            new ApiKeyCredential(settings.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new HttpClientPipelineTransport(openAiTransport),
                RetryPolicy = new ClientRetryPolicy(0),
            });

        IChatClient chatClient = openAiClient.GetChatClient(settings.Model).AsIChatClient();
        if (AifaRequestContext.IsAifa(endpoint))
            chatClient = new AifaThinkingChatClient(chatClient, settings);
        return new TransportOwnedChatClient(chatClient, openAiTransport);
    }
}
