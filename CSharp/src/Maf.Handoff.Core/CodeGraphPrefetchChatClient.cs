using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Performs one local CodeGraph lookup when C# involvement becomes visible, without spending another model turn.
/// </summary>
internal sealed class CodeGraphPrefetchChatClient(IChatClient innerClient) : IChatClient
{
    private const int QueryCharacterLimit = 256;
    private static readonly Regex CSharpEvidencePattern = new(
        @"(?:\.(?:cs|csproj|sln)(?=$|[^a-z0-9])|(?<!\w)c#(?!\w)|\bdotnet\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SourceFilePattern = new(
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\.(?:cs|csproj)(?=$|[^a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DeclaredSymbolPattern = new(
        @"\b(?:class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NamedSymbolPattern = new(
        @"\b(?<name>[A-Z][A-Za-z0-9_]*(?:Client|Service|Controller|Manager|Repository|Handler|Factory|Provider|Options|Settings|Model))\b",
        RegexOptions.CultureInvariant);
    private int _prefetchAttempted;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (ChatMessage[] preparedMessages, ChatOptions? preparedOptions) =
            await PrepareAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return await innerClient.GetResponseAsync(
            preparedMessages, preparedOptions, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (ChatMessage[] preparedMessages, ChatOptions? preparedOptions) =
            await PrepareAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in innerClient
            .GetStreamingResponseAsync(preparedMessages, preparedOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // This decorator does not own the provider client.
    }

    private async Task<(ChatMessage[] Messages, ChatOptions? Options)> PrepareAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ChatMessage[] materialized = [.. messages];
        if (HasCodeGraphCall(materialized))
        {
            Interlocked.Exchange(ref _prefetchAttempted, 1);
        }

        AIFunction? getContext = options?.Tools?
            .OfType<AIFunction>()
            .FirstOrDefault(tool => tool.Name == CodeGraphToolFactory.GetContextName);
        bool shouldPrefetch = getContext is not null &&
            HasCSharpEvidence(materialized) &&
            Interlocked.CompareExchange(ref _prefetchAttempted, 1, 0) == 0;
        if (shouldPrefetch)
        {
            string symbol = BuildSymbolQuery(materialized);
            AIFunctionArguments arguments = new()
            {
                ["query"] = symbol,
                ["maxNodes"] = 10,
                ["tokenBudget"] = 1_200,
            };

            try
            {
                object? result = await getContext!.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                materialized =
                [
                    .. materialized,
                    new ChatMessage(
                        ChatRole.User,
                        $"Read-only CodeGraph context prefetched locally for symbol '{symbol}':\n{result}"),
                ];
            }
            catch
            {
                // CodeGraph is an optimization. Existing repository tools remain the source-of-truth fallback.
            }
        }

        return Volatile.Read(ref _prefetchAttempted) == 0
            ? (materialized, options)
            : (materialized, WithoutCodeGraphTools(options));
    }

    private static ChatOptions? WithoutCodeGraphTools(ChatOptions? options)
    {
        if (options?.Tools is null)
        {
            return options;
        }

        ChatOptions projected = options.Clone();
        projected.Tools =
        [
            .. options.Tools.Where(tool => tool is not AIFunction function ||
                !IsCodeGraphTool(function.Name)),
        ];
        return projected;
    }

    private static bool HasCodeGraphCall(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Any(call => IsCodeGraphTool(call.Name));

    private static bool IsCodeGraphTool(string name) =>
        name is CodeGraphToolFactory.FindSymbolName or
            CodeGraphToolFactory.GetNeighborsName or
            CodeGraphToolFactory.GetContextName or
            CodeGraphToolFactory.GetContextByIdName;

    private static bool HasCSharpEvidence(IEnumerable<ChatMessage> messages) =>
        EvidenceParts(messages).Any(CSharpEvidencePattern.IsMatch);

    private static string BuildSymbolQuery(IEnumerable<ChatMessage> messages)
    {
        string[] evidence = [.. EvidenceParts(messages)];
        foreach (string part in evidence.Reverse())
        {
            Match file = SourceFilePattern.Match(part);
            if (file.Success)
            {
                return Limit(file.Groups["name"].Value);
            }
        }

        foreach (string part in evidence.Reverse())
        {
            Match declaration = DeclaredSymbolPattern.Match(part);
            if (declaration.Success)
            {
                return Limit(declaration.Groups["name"].Value);
            }
        }

        foreach (string part in evidence.Reverse())
        {
            Match namedSymbol = NamedSymbolPattern.Match(part);
            if (namedSymbol.Success)
            {
                return Limit(namedSymbol.Groups["name"].Value);
            }
        }

        return "Program";
    }

    private static string Limit(string value) =>
        value.Length <= QueryCharacterLimit ? value : value[..QueryCharacterLimit];

    private static IEnumerable<string> EvidenceParts(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                yield return message.Text;
            }

            foreach (AIContent content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    foreach (object? value in call.Arguments?.Values ?? [])
                    {
                        if (value?.ToString() is { Length: > 0 } argument)
                        {
                            yield return argument;
                        }
                    }
                }
                else if (content is FunctionResultContent result &&
                    result.Result?.ToString() is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
        }
    }
}
