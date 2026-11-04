using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Xbank.DevAgent.Core;

// Apply AIFA's extension through the SDK JSON patch so regular and streaming calls use the same body.
internal sealed class AifaThinkingChatClient(IChatClient innerClient, WorkflowSettings settings)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Configure(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Configure(options), cancellationToken);

    private ChatOptions Configure(ChatOptions? options)
    {
        ChatOptions configured = options?.Clone() ?? new ChatOptions();
        var existingFactory = configured.RawRepresentationFactory;
        configured.RawRepresentationFactory = client =>
        {
            ChatCompletionOptions raw = existingFactory?.Invoke(client) as ChatCompletionOptions ?? new();
#pragma warning disable OPENAI001, SCME0001 // AIFA extends the OpenAI-compatible request body.
            raw.Patch.Set("$.enable_thinking"u8, true);
            raw.ReasoningEffortLevel ??= new ChatReasoningEffortLevel(settings.Provider?.ReasoningEffort ?? "low");
#pragma warning restore OPENAI001, SCME0001
            return raw;
        };
        return configured;
    }
}
