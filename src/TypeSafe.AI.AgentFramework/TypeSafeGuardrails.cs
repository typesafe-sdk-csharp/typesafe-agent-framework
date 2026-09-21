using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>Creates agents that screen output before framework-managed history is committed.</summary>
public static class TypeSafeGuardrails
{
    /// <summary>Creates a buffered, history-protected agent over a stateless model client.</summary>
    /// <remarks>Tool effects cannot be rolled back. Provider-managed conversations and per-call persistence are unsupported.</remarks>
    public static AIAgent CreateChatClientAgent(IChatClient modelClient, ITypeSafeClient client,
        IReadOnlyDictionary<string, TypeSafeQuestion> screeningQuestions, Func<SystemOneResponse, bool> shouldBlock,
        ChatClientAgentOptions? options = null,
        string blockMessage = "Your request violates safety guidelines and cannot be processed.", string? model = null,
        ITypeSafeContentExtractor? inputExtractor = null, ITypeSafeContentExtractor? outputExtractor = null)
    {
        ArgumentNullException.ThrowIfNull(modelClient);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(screeningQuestions);
        ArgumentNullException.ThrowIfNull(shouldBlock);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockMessage);
        if (screeningQuestions.Count == 0) throw new ArgumentException("Screening questions are required.", nameof(screeningQuestions));
        options ??= new ChatClientAgentOptions();
        if (options.RequirePerServiceCallChatHistoryPersistence)
            throw new NotSupportedException("Protected history requires end-of-run persistence.");
        StatelessClient.Validate(options.ChatOptions);
        var snapshot = EvaluationSupport.Snapshot(screeningQuestions);
        var agentOptions = new ChatClientAgentOptions
        {
            Id = options.Id, Name = options.Name, Description = options.Description,
            ChatOptions = options.ChatOptions?.Clone(), ChatHistoryProvider = options.ChatHistoryProvider,
            AIContextProviders = options.AIContextProviders?.ToArray(), UseProvidedChatClientAsIs = true,
            RequirePerServiceCallChatHistoryPersistence = false
        };
        var loop = new TypeSafeExpansionChatClient(new StatelessClient(modelClient));
        var screened = new OutputClient(loop, new ScreeningEvaluator(client, snapshot, model), shouldBlock, blockMessage,
            outputExtractor ?? TypeSafeContentConverter.ScreeningExtractor);
        return new TypeSafeGuardrail(new ChatClientAgent(screened, agentOptions), client, snapshot, shouldBlock,
            blockMessage, model, contentExtractor: inputExtractor);
    }

    private sealed class StatelessClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        internal static void Validate(ChatOptions? options)
        {
            if (options?.ConversationId is not null || options?.ContinuationToken is not null || options?.AllowBackgroundResponses == true)
                throw new NotSupportedException("Protected history requires a stateless model: conversation IDs, continuation tokens and background responses are unsupported.");
        }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Validate(options);
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            if (response.ConversationId is not null || response.ContinuationToken is not null)
                throw new NotSupportedException("The model returned provider-managed conversation state; protected history requires a stateless provider.");
            return response;
        }
    }

    private sealed class OutputClient(IChatClient inner, ScreeningEvaluator evaluator,
        Func<SystemOneResponse, bool> shouldBlock,
        string blockMessage, ITypeSafeContentExtractor extractor) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            StatelessClient.Validate(options);
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            var evaluation = await evaluator.EvaluateAsync(response.Messages, extractor, cancellationToken).ConfigureAwait(false);
            if (evaluation is null || !shouldBlock(evaluation)) return response;
            // Do not copy provider metadata or raw representations from the rejected response.
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, blockMessage))
            {
                FinishReason = ChatFinishReason.ContentFilter,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["typesafe.guardrail_blocked"] = true, ["typesafe.guardrail_stage"] = "output"
                }
            };
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
    }
}
