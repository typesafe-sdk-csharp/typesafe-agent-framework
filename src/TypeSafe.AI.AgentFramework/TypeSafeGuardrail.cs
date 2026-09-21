using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// A security and compliance middleware agent (<see cref="DelegatingAIAgent"/>) that screens
/// inputs (and optionally outputs) against TypeSafe verification questions to prevent prompt injection,
/// policy violations, toxicity, and hallucinations before reaching inner models.
/// </summary>
public class TypeSafeGuardrail : DelegatingAIAgent
{
    private readonly ScreeningEvaluator _evaluator;
    private readonly Func<SystemOneResponse, bool> _shouldBlock;
    private readonly string _blockMessage;
    private readonly bool _screenOutput;
    private readonly ITypeSafeContentExtractor _inputExtractor;
    private readonly ITypeSafeContentExtractor _outputExtractor;

    /// <summary>Initializes a new instance of <see cref="TypeSafeGuardrail"/>.</summary>
    public TypeSafeGuardrail(
        AIAgent innerAgent,
        ITypeSafeClient client,
        IReadOnlyDictionary<string, TypeSafeQuestion> screeningQuestions,
        Func<SystemOneResponse, bool> shouldBlock,
        string blockMessage = "Your request violates safety guidelines and cannot be processed.",
        string? model = null,
        bool screenOutput = false,
        ITypeSafeContentExtractor? contentExtractor = null,
        ITypeSafeContentExtractor? outputContentExtractor = null)
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(screeningQuestions);
        ArgumentNullException.ThrowIfNull(shouldBlock);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockMessage);
        if (screeningQuestions.Count == 0)
        {
            throw new ArgumentException("At least one screening question is required.", nameof(screeningQuestions));
        }

        _evaluator = new ScreeningEvaluator(client, EvaluationSupport.Snapshot(screeningQuestions), model);
        _shouldBlock = shouldBlock;
        _blockMessage = blockMessage;
        _screenOutput = screenOutput;
        _inputExtractor = contentExtractor ?? TypeSafeContentConverter.ScreeningExtractor;
        _outputExtractor = outputContentExtractor ?? contentExtractor ?? TypeSafeContentConverter.ScreeningExtractor;
    }

    /// <summary>Initializes a new instance of <see cref="TypeSafeGuardrail"/> using a question builder lambda.</summary>
    public TypeSafeGuardrail(
        AIAgent innerAgent,
        ITypeSafeClient client,
        Func<QuestionBuilder, QuestionBuilder> screeningQuestions,
        Func<SystemOneResponse, bool> shouldBlock,
        string blockMessage = "Your request violates safety guidelines and cannot be processed.",
        string? model = null,
        bool screenOutput = false,
        ITypeSafeContentExtractor? contentExtractor = null,
        ITypeSafeContentExtractor? outputContentExtractor = null)
        : this(innerAgent, client, Questions.Build(screeningQuestions), shouldBlock, blockMessage, model, screenOutput, contentExtractor, outputContentExtractor)
    {
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Agent Framework materializes lazy input for the ambient run context before calling this method.
        messages = (ReferenceEquals(CurrentRunContext?.Agent, this) ? CurrentRunContext.RequestMessages : messages).ToArray();
        var preScreenResult = await _evaluator.EvaluateAsync(messages, _inputExtractor, cancellationToken).ConfigureAwait(false);
        if (preScreenResult is not null)
        {
            if (_shouldBlock(preScreenResult))
            {
                return CreateBlockedResponse(preScreenResult, "input");
            }
        }

        // Pass through to inner agent
        var response = await base.RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);

        // Optional post-turn output screening
        if (_screenOutput && response.Messages.Count > 0)
        {
            var postScreenResult = await _evaluator.EvaluateAsync(response.Messages, _outputExtractor, cancellationToken).ConfigureAwait(false);
            if (postScreenResult is not null)
            {
                if (_shouldBlock(postScreenResult))
                {
                    return CreateBlockedResponse(postScreenResult, "output");
                }
            }
        }

        return response;
    }

    /// <inheritdoc />
    /// <remarks>When output screening is enabled, streaming is routed through the buffered
    /// non-streaming path so the complete inner response can be screened before any update is
    /// delivered; streaming cannot bypass output screening. With input-only screening, updates
    /// stream through from the inner agent without buffering.</remarks>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Agent Framework materializes lazy input for the ambient run context before calling this method.
        messages = (ReferenceEquals(CurrentRunContext?.Agent, this) ? CurrentRunContext.RequestMessages : messages).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (_screenOutput)
        {
            // Buffer through the non-streaming path, which screens the input, runs the inner
            // agent once, and screens the complete output — all before any update is delivered,
            // so a streaming consumer cannot receive text that output screening would block.
            var buffered = await RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in buffered.ToAgentResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            yield break;
        }

        var preScreenResult = await _evaluator.EvaluateAsync(messages, _inputExtractor, cancellationToken).ConfigureAwait(false);
        if (preScreenResult is not null)
        {
            if (_shouldBlock(preScreenResult))
            {
                var blocked = CreateBlockedResponse(preScreenResult, "input");
                foreach (var update in blocked.ToAgentResponseUpdates())
                {
                    yield return update;
                }

                yield break;
            }
        }

        // Input-only screening: stream the inner agent through.
        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }


    private AgentResponse CreateBlockedResponse(SystemOneResponse evaluation, string stage)
    {
        var blockChatMessage = new ChatMessage(ChatRole.Assistant, [
            new TextContent(_blockMessage),
            new TypeSafeEvaluationContent(evaluation)
        ])
        {
            AuthorName = Name ?? "TypeSafeGuardrail",
            MessageId = Guid.NewGuid().ToString("N"),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["typesafe.guardrail_blocked"] = true,
                ["typesafe.guardrail_stage"] = stage,
                ["typesafe.response"] = evaluation
            }
        };

        return new AgentResponse(blockChatMessage)
        {
            AgentId = Id,
            ResponseId = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.ContentFilter,
            RawRepresentation = evaluation,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["typesafe.guardrail_blocked"] = true,
                ["typesafe.guardrail_stage"] = stage
            }
        };
    }
}

/// <summary>Extension methods for attaching <see cref="TypeSafeGuardrail"/> via <see cref="AIAgentBuilder"/>.</summary>
public static class TypeSafeGuardrailExtensions
{
    /// <summary>Adds a TypeSafe guardrail screening step to the agent pipeline.</summary>
    public static AIAgentBuilder UseTypeSafeGuardrail(
        this AIAgentBuilder builder,
        ITypeSafeClient client,
        IReadOnlyDictionary<string, TypeSafeQuestion> screeningQuestions,
        Func<SystemOneResponse, bool> shouldBlock,
        string blockMessage = "Your request violates safety guidelines and cannot be processed.",
        string? model = null,
        bool screenOutput = false,
        ITypeSafeContentExtractor? contentExtractor = null,
        ITypeSafeContentExtractor? outputContentExtractor = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use((inner, _) => new TypeSafeGuardrail(
            inner,
            client,
            screeningQuestions,
            shouldBlock,
            blockMessage,
            model,
            screenOutput,
            contentExtractor,
            outputContentExtractor));
    }

    /// <summary>Adds a TypeSafe guardrail screening step to the agent pipeline using a question builder lambda.</summary>
    public static AIAgentBuilder UseTypeSafeGuardrail(
        this AIAgentBuilder builder,
        ITypeSafeClient client,
        Func<QuestionBuilder, QuestionBuilder> screeningQuestions,
        Func<SystemOneResponse, bool> shouldBlock,
        string blockMessage = "Your request violates safety guidelines and cannot be processed.",
        string? model = null,
        bool screenOutput = false,
        ITypeSafeContentExtractor? contentExtractor = null,
        ITypeSafeContentExtractor? outputContentExtractor = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use((inner, _) => new TypeSafeGuardrail(
            inner,
            client,
            screeningQuestions,
            shouldBlock,
            blockMessage,
            model,
            screenOutput,
            contentExtractor,
            outputContentExtractor));
    }
}

