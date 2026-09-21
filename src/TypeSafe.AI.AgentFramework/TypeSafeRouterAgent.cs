using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>The versioned session envelope persisted by <see cref="TypeSafeRouterAgent"/>.</summary>
/// <remarks>Each entry maps a stable route label to that agent's own serialized session, so every
/// downstream agent keeps an isolated conversation state instead of inheriting another agent's session.</remarks>
internal sealed record RouterSessionEnvelope
{
    public const string Kind = "typesafe-router";
    public const int CurrentVersion = 2;

    [JsonPropertyName("kind")] public required string KindValue { get; init; }

    [JsonPropertyName("version")] public required int Version { get; init; }

    [JsonPropertyName("targets")] public IReadOnlyDictionary<string, JsonElement>? Targets { get; init; }

    [JsonPropertyName("fallback")] public JsonElement? Fallback { get; init; }
    [JsonPropertyName("state_bag")] public JsonElement StateBag { get; init; }

    [JsonPropertyName("last_decision")] public string? LastDecision { get; init; }
}

/// <summary>
/// An <see cref="AIAgent"/> that classifies incoming messages with TypeSafe System One
/// and dispatches to appropriate downstream target agents based on intent and confidence.
/// </summary>
/// <remarks>
/// Sessions are isolated per route label even when labels share an agent instance. The fallback
/// has a separate session. Version 2 envelopes survive reconstruction of equivalent agents.
/// Legacy envelopes require resolvable runtime IDs; incompatible state raises a migration error.
/// </remarks>
public class TypeSafeRouterAgent : AIAgent
{
    private const string RoutingDecisionKey = "typesafe.routing_decision";

    private readonly ITypeSafeClient _client;
    private readonly string _questionId;
    private readonly IReadOnlyDictionary<string, TypeSafeQuestion> _questions;
    private readonly IReadOnlyDictionary<string, AIAgent> _routeTargets;
    private readonly AIAgent _fallbackAgent;
    private readonly double _minimumConfidence;
    private readonly string? _model;
    private readonly ITypeSafeContentExtractor _contentExtractor;

    /// <summary>Initializes a new instance of <see cref="TypeSafeRouterAgent"/>.</summary>
    public TypeSafeRouterAgent(
        ITypeSafeClient client,
        string questionId,
        Choice routingQuestion,
        IReadOnlyDictionary<string, AIAgent> routeTargets,
        AIAgent fallbackAgent,
        double minimumConfidence = 0.70,
        string? model = null,
        ITypeSafeContentExtractor? contentExtractor = null,
        string name = "TypeSafeRouter")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentNullException.ThrowIfNull(routingQuestion);
        ArgumentNullException.ThrowIfNull(routeTargets);
        ArgumentNullException.ThrowIfNull(fallbackAgent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (routeTargets.Count == 0)
        {
            throw new ArgumentException("At least one route target agent must be configured.", nameof(routeTargets));
        }

        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence), "Minimum confidence must be between 0.0 and 1.0.");
        }

        foreach (var target in routeTargets.Values) ArgumentNullException.ThrowIfNull(target);
        _client = client;
        _questionId = questionId;
        _questions = EvaluationSupport.Snapshot(new Dictionary<string, TypeSafeQuestion> { [questionId] = routingQuestion });
        _routeTargets = new ReadOnlyDictionary<string, AIAgent>(new Dictionary<string, AIAgent>(routeTargets));
        _fallbackAgent = fallbackAgent;
        _minimumConfidence = minimumConfidence;
        _model = model;
        _contentExtractor = contentExtractor ?? TypeSafeContentConverter.DefaultExtractor;
        Name = name;
    }

    /// <summary>Initializes a new instance of <see cref="TypeSafeRouterAgent"/> using a choice options builder.</summary>
    public TypeSafeRouterAgent(
        ITypeSafeClient client,
        string questionId,
        string instructions,
        Func<ChoiceOptions, ChoiceOptions> options,
        IReadOnlyDictionary<string, AIAgent> routeTargets,
        AIAgent fallbackAgent,
        double minimumConfidence = 0.70,
        string? model = null,
        ITypeSafeContentExtractor? contentExtractor = null,
        string name = "TypeSafeRouter")
        : this(
            client,
            questionId,
            (Choice)Questions.Build(q => q.Choice(questionId, instructions, options))[questionId],
            routeTargets,
            fallbackAgent,
            minimumConfidence,
            model,
            contentExtractor,
            name)
    {
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The route targets mapped by choice option label.</summary>
    public IReadOnlyDictionary<string, AIAgent> RouteTargets => _routeTargets;

    /// <summary>The fallback agent invoked when confidence is below threshold or label unmapped.</summary>
    public AIAgent FallbackAgent => _fallbackAgent;

    /// <summary>The minimum confidence required to route to a target agent.</summary>
    public double MinimumConfidence => _minimumConfidence;

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AgentSession>(new RouterSession());
    }

    /// <inheritdoc />
    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSession(session);
        using var operation = ((RouterSession)session).Enter();

        var targetSessions = GetTargetSessionMap(session);
        var targets = new Dictionary<string, JsonElement>(targetSessions.Count, StringComparer.Ordinal);
        foreach (var (route, targetSession) in targetSessions)
        {
            if (!_routeTargets.TryGetValue(route, out var agent)) throw new InvalidOperationException($"Unknown saved route: {route}.");
            targets[route] = await agent.SerializeSessionAsync(targetSession, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        var fallback = ((RouterSession)session).Fallback;
        JsonElement? fallbackState = fallback is null ? null : await _fallbackAgent.SerializeSessionAsync(fallback, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);

        session.StateBag.TryGetValue<string>(RoutingDecisionKey, out var lastDecision);
        return JsonSerializer.SerializeToElement(new RouterSessionEnvelope
        {
            KindValue = RouterSessionEnvelope.Kind,
            Version = RouterSessionEnvelope.CurrentVersion,
            Targets = targets,
            Fallback = fallbackState,
            StateBag = session.StateBag.Serialize(),
            LastDecision = lastDecision
        }, AgentFrameworkJsonContext.Default.RouterSessionEnvelope);
    }

    /// <inheritdoc />
    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RouterSessionEnvelope? envelope = null;
        try
        {
            envelope = serializedState.Deserialize(AgentFrameworkJsonContext.Default.RouterSessionEnvelope);
        }
        catch (JsonException)
        {
            // Fall through to the rejection below.
        }

        if (envelope is null || envelope.KindValue != RouterSessionEnvelope.Kind || envelope.Version is not (1 or RouterSessionEnvelope.CurrentVersion))
            throw new ArgumentException("Unsupported TypeSafe router session envelope.", nameof(serializedState));

        var routerSession = new RouterSession(envelope.StateBag);
        if (envelope.Targets is not null)
        {
            var targetSessions = GetTargetSessionMap(routerSession);
            foreach (var (key, serializedTargetSession) in envelope.Targets)
            {
                var routes = envelope.Version == 1
                    ? _routeTargets.Where(x => x.Value.Id == key).ToList()
                    : _routeTargets.Where(x => x.Key == key).ToList();
                if (routes.Count == 0 && envelope.Version == 1 && _fallbackAgent.Id == key)
                    routerSession.Fallback = await _fallbackAgent.DeserializeSessionAsync(serializedTargetSession, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
                else if (routes.Count == 0)
                    throw new InvalidOperationException($"Cannot migrate saved router target '{key}'; supply the original route mapping.");
                foreach (var route in routes)
                    targetSessions[route.Key] = await route.Value.DeserializeSessionAsync(serializedTargetSession, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        if (envelope.Fallback is JsonElement fallback)
            routerSession.Fallback = await _fallbackAgent.DeserializeSessionAsync(fallback, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);

        if (envelope.LastDecision is not null)
        {
            routerSession.StateBag.SetValue(RoutingDecisionKey, envelope.LastDecision);
        }

        return routerSession;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(messages);
        if (session is not null) ValidateSession(session);

        using var operation = (session as RouterSession)?.Enter();
        var materializedMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var (targetAgent, routingDecision, routeKey) = await ResolveTargetAsync(materializedMessages, cancellationToken).ConfigureAwait(false);
        var targetSession = await GetTargetSessionAsync(targetAgent, routeKey, session, cancellationToken).ConfigureAwait(false);

        var agentResponse = await targetAgent.RunAsync(materializedMessages, targetSession, options, cancellationToken).ConfigureAwait(false);

        if (session is not null)
        {
            session.StateBag.SetValue(RoutingDecisionKey, routingDecision);
        }

        // Attach routing attribution metadata
        agentResponse.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        agentResponse.AdditionalProperties["typesafe.routed_to"] = targetAgent.Name ?? targetAgent.Id;
        agentResponse.AdditionalProperties["typesafe.routing_decision"] = routingDecision;

        return agentResponse;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session is not null) ValidateSession(session);
        using var operation = (session as RouterSession)?.Enter();
        var materializedMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var (targetAgent, routingDecision, routeKey) = await ResolveTargetAsync(materializedMessages, cancellationToken).ConfigureAwait(false);
        var targetSession = await GetTargetSessionAsync(targetAgent, routeKey, session, cancellationToken).ConfigureAwait(false);

        if (session is not null)
        {
            session.StateBag.SetValue(RoutingDecisionKey, routingDecision);
        }

        await foreach (var update in targetAgent.RunStreamingAsync(materializedMessages, targetSession, options, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            update.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            update.AdditionalProperties["typesafe.routed_to"] = targetAgent.Name ?? targetAgent.Id;
            update.AdditionalProperties["typesafe.routing_decision"] = routingDecision;
            yield return update;
        }
    }

    private async Task<(AIAgent TargetAgent, string RoutingDecision, string? RouteKey)> ResolveTargetAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        // No evaluable input: fall back without an upstream call rather than fabricating a routing decision.
        if (messages.Count == 0)
        {
            return (_fallbackAgent, "fallback (no evaluable input)", null);
        }

        var state = _contentExtractor.Extract(messages);
        if (TypeSafeContentConverter.IsBlankState(state))
        {
            return (_fallbackAgent, "fallback (no evaluable input)", null);
        }

        var response = await _client.SystemOneAsync(state, _questions, _model, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (!response.Answers.TryGetValue(_questionId, out var answer))
            return (_fallbackAgent, "fallback (missing answer)", null);
        if (!ScreeningAnswers.Valid(_questions[_questionId], answer))
            return (_fallbackAgent, "fallback (invalid answer)", null);
        if (answer is ChoiceAnswer choiceAnswer)
        {
            if (choiceAnswer.Confidence >= _minimumConfidence &&
                _routeTargets.TryGetValue(choiceAnswer.Choice, out var targetAgent))
            {
                return (targetAgent, choiceAnswer.Choice, choiceAnswer.Choice);
            }

            if (!_routeTargets.ContainsKey(choiceAnswer.Choice)) return (_fallbackAgent, "fallback (unmapped route)", null);
            return (_fallbackAgent, $"fallback (choice '{choiceAnswer.Choice}' confidence {choiceAnswer.Confidence:F2} < {_minimumConfidence:F2})", null);
        }

        return (_fallbackAgent, "fallback (unrecognized answer kind)", null);
    }

    private async ValueTask<AgentSession> GetTargetSessionAsync(AIAgent targetAgent, string? routeKey, AgentSession? routerSession, CancellationToken cancellationToken)
    {
        if (routerSession is null)
        {
            return await targetAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var typed = (RouterSession)routerSession;
        if (routeKey is null) return typed.Fallback ??= await targetAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var targetSessions = GetTargetSessionMap(routerSession);
        if (targetSessions.TryGetValue(routeKey, out var existing))
        {
            return existing;
        }

        var created = await targetAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        targetSessions[routeKey] = created;
        return created;
    }

    private static Dictionary<string, AgentSession> GetTargetSessionMap(AgentSession session) => ((RouterSession)session).Targets;

    private static void ValidateSession(AgentSession session)
    {
        if (session is not RouterSession)
            throw new ArgumentException("The session is not a TypeSafe router session.", nameof(session));
    }

    private sealed class RouterSession : AgentSession
    {
        private int _active;
        public IDisposable Enter()
        {
            if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
                throw new InvalidOperationException("Overlapping operations on the same router session are unsupported. Use separate sessions for parallel runs.");
            return new Operation(this);
        }
        private sealed class Operation(RouterSession session) : IDisposable
        {
            private RouterSession? _session = session;
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _session, null);
                if (owner is not null) Volatile.Write(ref owner._active, 0);
            }
        }
        public RouterSession() { }
        public RouterSession(JsonElement state) : base(AgentSessionStateBag.Deserialize(state)) { }
        public Dictionary<string, AgentSession> Targets { get; } = new(StringComparer.Ordinal);
        public AgentSession? Fallback { get; set; }
    }
}

