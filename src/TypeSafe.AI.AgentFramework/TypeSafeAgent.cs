using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>The session envelope persisted by <see cref="TypeSafeAgent"/>.</summary>
internal sealed record TypeSafeAgentSessionEnvelope
{
    public const string Kind = "typesafe-agent";
    public const int CurrentVersion = 1;
    [JsonPropertyName("state_bag")] public JsonElement StateBag { get; init; }

    [JsonPropertyName("kind")] public required string KindValue { get; init; }
    [JsonPropertyName("version")] public required int Version { get; init; }
    [JsonPropertyName("last_response")] public SystemOneResponse? LastResponse { get; init; }
}

/// <summary>
/// A conversation session for <see cref="TypeSafeAgent"/> that tracks the most recent evaluation.
/// </summary>
public class TypeSafeAgentSession : AgentSession
{
    public TypeSafeAgentSession() { }
    internal TypeSafeAgentSession(JsonElement state) : base(AgentSessionStateBag.Deserialize(state)) { }
    /// <summary>The latest evaluation response from TypeSafe System One.</summary>
    public SystemOneResponse? LastResponse { get; set; }
}

/// <summary>
/// A generic, full-featured Microsoft Agent Framework <see cref="AIAgent"/> backed by TypeSafe System One.
/// Supports static or dynamic questions, structured outputs, streaming, and session persistence.
/// </summary>
public class TypeSafeAgent : AIAgent
{
    private readonly ITypeSafeClient _client;

    /// <summary>Initializes a new instance of <see cref="TypeSafeAgent"/>.</summary>
    public TypeSafeAgent(
        ITypeSafeClient client,
        IReadOnlyDictionary<string, TypeSafeQuestion>? defaultQuestions = null,
        string name = "TypeSafeAgent",
        string? defaultModel = null,
        ITypeSafeContentExtractor? contentExtractor = null,
        Func<SystemOneResponse, string>? resultFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (defaultModel is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        }

        _client = client;
        Name = name;
        DefaultModel = defaultModel;
        ContentExtractor = contentExtractor ?? TypeSafeContentConverter.DefaultExtractor;
        ResultFormatter = resultFormatter;

        if (defaultQuestions is not null)
        {
            if (defaultQuestions.Count == 0)
            {
                throw new ArgumentException("The default questions dictionary cannot be empty.", nameof(defaultQuestions));
            }

            // Snapshot questions for immutability
            DefaultQuestions = EvaluationSupport.Snapshot(defaultQuestions);
        }
    }

    /// <summary>Initializes a new instance of <see cref="TypeSafeAgent"/> using a question builder lambda.</summary>
    public TypeSafeAgent(
        ITypeSafeClient client,
        Func<QuestionBuilder, QuestionBuilder> questionsBuilder,
        string name = "TypeSafeAgent",
        string? defaultModel = null,
        ITypeSafeContentExtractor? contentExtractor = null,
        Func<SystemOneResponse, string>? resultFormatter = null)
        : this(client, Questions.Build(questionsBuilder), name, defaultModel, contentExtractor, resultFormatter)
    {
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Default questions evaluated when no run-specific questions are supplied.</summary>
    public IReadOnlyDictionary<string, TypeSafeQuestion>? DefaultQuestions { get; }

    /// <summary>Default model used when no run-specific model override is supplied.</summary>
    public string? DefaultModel { get; }

    /// <summary>The message content extractor used to convert chat messages to TypeSafe content.</summary>
    public ITypeSafeContentExtractor ContentExtractor { get; }

    /// <summary>Optional custom string formatter for evaluation responses.</summary>
    public Func<SystemOneResponse, string>? ResultFormatter { get; }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AgentSession>(new TypeSafeAgentSession());
    }

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);

        SystemOneResponse? lastResponse = null;
        if (session is TypeSafeAgentSession tsSession)
        {
            lastResponse = tsSession.LastResponse;
        }

        var envelope = new TypeSafeAgentSessionEnvelope
        {
            KindValue = TypeSafeAgentSessionEnvelope.Kind,
            Version = TypeSafeAgentSessionEnvelope.CurrentVersion,
            StateBag = session.StateBag.Serialize(),
            LastResponse = lastResponse
        };

        return ValueTask.FromResult(JsonSerializer.SerializeToElement(
            envelope,
            AgentFrameworkJsonContext.Default.TypeSafeAgentSessionEnvelope));
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TypeSafeAgentSessionEnvelope? envelope = null;
        try
        {
            envelope = serializedState.Deserialize(AgentFrameworkJsonContext.Default.TypeSafeAgentSessionEnvelope);
        }
        catch (JsonException)
        {
            // Fall through to error
        }

        if (envelope is null ||
            envelope.KindValue != TypeSafeAgentSessionEnvelope.Kind ||
            envelope.Version != TypeSafeAgentSessionEnvelope.CurrentVersion)
        {
            throw new ArgumentException("Unsupported TypeSafe agent session envelope.", nameof(serializedState));
        }

        var session = new TypeSafeAgentSession(envelope.StateBag) { LastResponse = envelope.LastResponse };
        if (envelope.LastResponse is not null)
        {
            session.StateBag.SetValue("typesafe.last_response", envelope.LastResponse, AgentFrameworkJsonContext.Default.Options);
        }

        return ValueTask.FromResult<AgentSession>(session);
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: not null })
            throw new NotSupportedException("Custom response schemas are unsupported. Use RunEvaluationAsync and map the response explicitly.");
        var tsOptions = options as TypeSafeAgentRunOptions;
        messages = ReferenceEquals(CurrentRunContext?.Agent, this) ? CurrentRunContext.RequestMessages : messages;
        var result = await RunEvaluationAsync(messages, session, tsOptions, cancellationToken).ConfigureAwait(false);
        // Format message text
        string text;
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            // Structured output was requested: serialize response as JSON for AgentResponse<T>.Result deserialization
            text = JsonSerializer.Serialize(result, TypeSafeJson.ResponseTypeInfo);
        }
        else
        {
            text = EvaluationSupport.Format(result, tsOptions?.ResultFormatter ?? ResultFormatter);
        }

        var responseMessage = TypeSafeContentConverter.ToChatMessage(result, Name, _ => text);
        return new AgentResponse(responseMessage)
        {
            AgentId = Id,
            ResponseId = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            Usage = EvaluationSupport.Usage(result),
            AdditionalProperties = EvaluationSupport.Metadata(result),
            RawRepresentation = result
        };
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToAgentResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    /// <summary>Evaluates questions and returns the complete response without application DTO mapping.</summary>
    public async Task<SystemOneResponse> RunEvaluationAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        TypeSafeAgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(messages);
        if (session is not null)
        {
            ValidateSession(session);
        }

        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: not null })
            throw new NotSupportedException("Custom response schemas are unsupported. Use RunEvaluationAsync without a schema and map the response explicitly.");
        var tsOptions = options;
        var effectiveQuestions = tsOptions?.Questions ?? DefaultQuestions
            ?? throw new InvalidOperationException("No TypeSafe questions were configured for this agent or invocation.");

        if (effectiveQuestions.Count == 0)
        {
            throw new InvalidOperationException("At least one question is required for TypeSafe evaluation.");
        }

        var effectiveModel = tsOptions?.Model ?? DefaultModel;
        var extractor = tsOptions?.ContentExtractor ?? ContentExtractor;
        var state = extractor.Extract(messages);

        var result = await _client.SystemOneAsync(state, effectiveQuestions, effectiveModel, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Update session state
        if (session is not null)
        {
            session.StateBag.SetValue("typesafe.last_response", result, AgentFrameworkJsonContext.Default.Options);
            if (session is TypeSafeAgentSession customSession)
            {
                customSession.LastResponse = result;
            }
        }

        return result;
    }

    private static void ValidateSession(AgentSession session)
    {
        if (session is not TypeSafeAgentSession)
            throw new ArgumentException("The session is not a TypeSafe agent session.", nameof(session));
    }
}


