using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Controls how TypeSafe evaluation results are injected into the agent's <see cref="AIContext"/>.
/// </summary>
[Flags]
public enum TypeSafeContextInjectionMode
{
    /// <summary>Do not inject into context properties (session caching only).</summary>
    None = 0,

    /// <summary>Inject evaluated facts/rubric summaries into <see cref="AIContext.Instructions"/>.</summary>
    Instructions = 1,

    /// <summary>Inject an evaluation <see cref="ChatMessage"/> with <see cref="TypeSafeEvaluationContent"/> into <see cref="AIContext.Messages"/>.</summary>
    Messages = 2,

    /// <summary>Conditionally inject tools selected by <see cref="TypeSafeAIContextProvider.ToolSelector"/> into <see cref="AIContext.Tools"/>.</summary>
    Tools = 4,

    /// <summary>Inject into all context properties: instructions, messages, and tools.</summary>
    All = Instructions | Messages | Tools
}

/// <summary>
/// An <see cref="AIContextProvider"/> that pre-evaluates incoming request messages against TypeSafe questions
/// before downstream LLM invocation. Results can be injected as instructions, contextual messages, or dynamic tools,
/// and are cached in <see cref="AgentSession.StateBag"/>.
/// </summary>
public class TypeSafeAIContextProvider : AIContextProvider
{
    private readonly ITypeSafeClient _client;
    private readonly IReadOnlyDictionary<string, TypeSafeQuestion> _questions;
    private readonly string? _model;
    private readonly TypeSafeContextInjectionMode _mode;
    private readonly ITypeSafeContentExtractor _contentExtractor;
    private readonly Func<SystemOneResponse, string>? _instructionFormatter;
    private readonly Func<SystemOneResponse, IEnumerable<AITool>?>? _toolSelector;

    /// <summary>Initializes a new instance of <see cref="TypeSafeAIContextProvider"/>.</summary>
    public TypeSafeAIContextProvider(
        ITypeSafeClient client,
        IReadOnlyDictionary<string, TypeSafeQuestion> questions,
        TypeSafeContextInjectionMode mode = TypeSafeContextInjectionMode.Instructions,
        string? model = null,
        ITypeSafeContentExtractor? contentExtractor = null,
        Func<SystemOneResponse, string>? instructionFormatter = null,
        Func<SystemOneResponse, IEnumerable<AITool>?>? toolSelector = null,
        string stateKey = "typesafe.evaluation_context")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
        {
            throw new ArgumentException("At least one question is required for context provider evaluation.", nameof(questions));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stateKey);
        StateKey = stateKey;
        _client = client;
        _questions = EvaluationSupport.Snapshot(questions);
        _mode = mode;
        _model = model;
        _contentExtractor = contentExtractor ?? TypeSafeContentConverter.DefaultExtractor;
        _instructionFormatter = instructionFormatter;
        _toolSelector = toolSelector;
    }

    /// <summary>Initializes a new instance of <see cref="TypeSafeAIContextProvider"/> using a question builder lambda.</summary>
    public TypeSafeAIContextProvider(
        ITypeSafeClient client,
        Func<QuestionBuilder, QuestionBuilder> questions,
        TypeSafeContextInjectionMode mode = TypeSafeContextInjectionMode.Instructions,
        string? model = null,
        ITypeSafeContentExtractor? contentExtractor = null,
        Func<SystemOneResponse, string>? instructionFormatter = null,
        Func<SystemOneResponse, IEnumerable<AITool>?>? toolSelector = null,
        string stateKey = "typesafe.evaluation_context")
        : this(client, Questions.Build(questions), mode, model, contentExtractor, instructionFormatter, toolSelector, stateKey)
    {
    }

    /// <summary>Session key for this provider evaluation.</summary>
    public string StateKey { get; }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => [StateKey];

    /// <summary>Gets the context injection mode.</summary>
    public TypeSafeContextInjectionMode Mode => _mode;

    /// <summary>Gets the tool selector delegate, if configured.</summary>
    public Func<SystemOneResponse, IEnumerable<AITool>?>? ToolSelector => _toolSelector;

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var messages = context.AIContext.Messages;
        if (messages is null)
        {
            return new AIContext();
        }

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (messageList.Count == 0)
        {
            return new AIContext();
        }

        var state = _contentExtractor.Extract(messageList);

        // Blank input carries nothing to evaluate; skip the upstream call and inject nothing.
        if (TypeSafeContentConverter.IsBlankState(state))
        {
            return new AIContext();
        }

        var response = await _client.SystemOneAsync(state, _questions, _model, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Cache evaluation into session state
        context.Session?.StateBag.SetValue(StateKey, response, AgentFrameworkJsonContext.Default.Options);

        string? instructions = null;
        if (_mode.HasFlag(TypeSafeContextInjectionMode.Instructions))
        {
            var summary = _instructionFormatter?.Invoke(response) ?? TypeSafeEvaluationContent.FormatSummary(response);
            instructions = $"[TypeSafe Evaluation Context]\n{summary}";
        }

        IEnumerable<ChatMessage>? injectedMessages = null;
        if (_mode.HasFlag(TypeSafeContextInjectionMode.Messages))
        {
            var msg = TypeSafeContentConverter.ToChatMessage(response, "TypeSafeEvaluator");
            injectedMessages = [msg];
        }

        IEnumerable<AITool>? tools = null;
        if (_mode.HasFlag(TypeSafeContextInjectionMode.Tools) && _toolSelector is not null)
        {
            tools = _toolSelector(response);
        }

        return new AIContext
        {
            Instructions = instructions,
            Messages = injectedMessages,
            Tools = tools
        };
    }
}
