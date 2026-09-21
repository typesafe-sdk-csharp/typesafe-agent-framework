using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeRouterAgentTests
{
    private sealed class EchoAgent : AIAgent
    {
        public EchoAgent(string name) => Name = name;
        public override string Name { get; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new TypeSafeAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(default(JsonElement));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new TypeSafeAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = $"{Name}: Handled message";
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, text) { AuthorName = Name }));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"{Name}: Streaming update");
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_HighConfidence_RoutesToMatchingTargetAgent()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse(
            answers: new Dictionary<string, TypeSafeAnswer>
            {
                ["intent"] = new ChoiceAnswer
                {
                    Choice = "billing",
                    Confidence = 0.94,
                    Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
                    {
                        ["billing"] = 0.94,
                        ["technical"] = 0.06
                    })
                }
            });

        var fakeClient = new FakeTypeSafeClient(response);
        var billingAgent = new EchoAgent("BillingAgent");
        var techAgent = new EchoAgent("TechAgent");
        var fallbackAgent = new EchoAgent("FallbackAgent");

        var router = new TypeSafeRouterAgent(
            fakeClient,
            questionId: "intent",
            instructions: "Determine customer intent",
            options: opts => opts.Option("billing", "Billing issues").Option("technical", "Technical issues"),
            routeTargets: new Dictionary<string, AIAgent>
            {
                ["billing"] = billingAgent,
                ["technical"] = techAgent
            },
            fallbackAgent: fallbackAgent,
            minimumConfidence: 0.75);

        var agentResponse = await router.RunAsync("I have a question about my invoice");

        Assert.Equal("BillingAgent: Handled message", agentResponse.Messages[0].Text);
        Assert.NotNull(agentResponse.AdditionalProperties);
        Assert.Equal("BillingAgent", agentResponse.AdditionalProperties["typesafe.routed_to"]);
        Assert.Equal("billing", agentResponse.AdditionalProperties["typesafe.routing_decision"]);
    }

    [Fact]
    public async Task RunAsync_LowConfidence_RoutesToFallbackAgent()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse(
            answers: new Dictionary<string, TypeSafeAnswer>
            {
                ["intent"] = new ChoiceAnswer
                {
                    Choice = "billing",
                    Confidence = 0.52, // Below 0.75 threshold
                    Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
                    {
                        ["billing"] = 0.52,
                        ["technical"] = 0.48
                    })
                }
            });

        var fakeClient = new FakeTypeSafeClient(response);
        var billingAgent = new EchoAgent("BillingAgent");
        var fallbackAgent = new EchoAgent("EscalationAgent");

        var router = new TypeSafeRouterAgent(
            fakeClient,
            questionId: "intent",
            instructions: "Determine customer intent",
            options: opts => opts.Option("billing", "Billing").Option("technical", "Technical"),
            routeTargets: new Dictionary<string, AIAgent> { ["billing"] = billingAgent },
            fallbackAgent: fallbackAgent,
            minimumConfidence: 0.75);

        var agentResponse = await router.RunAsync("Maybe billing maybe something else");

        Assert.Equal("EscalationAgent: Handled message", agentResponse.Messages[0].Text);
        Assert.NotNull(agentResponse.AdditionalProperties);
        Assert.Equal("EscalationAgent", agentResponse.AdditionalProperties["typesafe.routed_to"]);
    }

    [Fact]
    public async Task RunStreamingAsync_RoutesAndStreamsFromTargetAgent()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse(
            answers: new Dictionary<string, TypeSafeAnswer>
            {
                ["intent"] = new ChoiceAnswer
                {
                    Choice = "technical",
                    Confidence = 1.0,
                    Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
                    {
                        ["technical"] = 1.0
                    })
                }
            });

        var fakeClient = new FakeTypeSafeClient(response);
        var techAgent = new EchoAgent("TechAgent");
        var fallbackAgent = new EchoAgent("FallbackAgent");

        var router = new TypeSafeRouterAgent(
            fakeClient,
            questionId: "intent",
            instructions: "Determine customer intent",
            options: opts => opts.Option("technical", "Technical"),
            routeTargets: new Dictionary<string, AIAgent> { ["technical"] = techAgent },
            fallbackAgent: fallbackAgent);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in router.RunStreamingAsync("Database connection failed"))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("TechAgent: Streaming update", updates[0].Text);
        Assert.Equal("technical", updates[0].AdditionalProperties!["typesafe.routing_decision"]);
    }

    /// <summary>
    /// A stateful target agent: each session carries an owner and a counter, mutated per run.
    /// Used to prove that the router hands each target only its own session and that state
    /// survives a router session round-trip.
    /// </summary>
    private sealed class StatefulAgent : AIAgent
    {
        public sealed class StateSession : AgentSession
        {
            public string Owner { get; set; } = string.Empty;
            public int Counter { get; set; }
        }

        public List<AgentSession?> SessionsReceived { get; } = [];
        public int CreateCalls { get; private set; }
        public int DeserializeCalls { get; private set; }

        public override string Name { get; }

        public StatefulAgent(string name) => Name = name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return ValueTask.FromResult<AgentSession>(new StateSession { Owner = Name });
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            var state = Assert.IsType<StateSession>(session);
            return ValueTask.FromResult(JsonDocument.Parse(
                $"{{\"owner\":\"{state.Owner}\",\"counter\":{state.Counter}}}").RootElement.Clone());
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            DeserializeCalls++;
            return ValueTask.FromResult<AgentSession>(new StateSession
            {
                Owner = serializedState.GetProperty("owner").GetString()!,
                Counter = serializedState.GetProperty("counter").GetInt32()
            });
        }

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            SessionsReceived.Add(session);
            var counter = session is StateSession state ? ++state.Counter : 0;
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, $"{Name}#{counter}") { AuthorName = Name }));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SessionsReceived.Add(session);
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"{Name}: Streaming update");
            await Task.CompletedTask;
        }
    }

    private static (TypeSafeRouterAgent Router, StatefulAgent Billing, StatefulAgent Technical, StatefulAgent Fallback, FakeTypeSafeClient Client) CreateStatefulRouter()
    {
        // "invoice" queries route to billing; anything else routes to technical (high confidence either way).
        var fakeClient = new FakeTypeSafeClient((state, _, _) =>
        {
            var text = state.ToJsonNode().GetValue<string>() ?? string.Empty;
            var choice = text.Contains("invoice", StringComparison.OrdinalIgnoreCase) ? "billing" : "technical";
            return FakeTypeSafeClient.CreateSampleResponse(
                answers: new Dictionary<string, TypeSafeAnswer>
                {
                    ["intent"] = new ChoiceAnswer
                    {
                        Choice = choice,
                        Confidence = 0.95,
                        Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double> { [choice] = 0.95, [choice == "billing" ? "technical" : "billing"] = 0.05 })
                    }
                });
        });

        var billing = new StatefulAgent("Billing");
        var technical = new StatefulAgent("Technical");
        var fallback = new StatefulAgent("Escalation");

        var router = new TypeSafeRouterAgent(
            fakeClient,
            questionId: "intent",
            instructions: "Determine customer intent",
            options: opts => opts.Option("billing", "Billing").Option("technical", "Technical"),
            routeTargets: new Dictionary<string, AIAgent> { ["billing"] = billing, ["technical"] = technical },
            fallbackAgent: fallback,
            minimumConfidence: 0.75);

        return (router, billing, technical, fallback, fakeClient);
    }

    [Fact]
    public async Task RunAsync_WithSession_EachTargetReceivesOnlyItsOwnSession()
    {
        var (router, billing, technical, fallback, _) = CreateStatefulRouter();
        var routerSession = await router.CreateSessionAsync();

        await router.RunAsync("invoice question", routerSession);
        await router.RunAsync("database question", routerSession);

        var billingSession = Assert.Single(billing.SessionsReceived);
        var technicalSession = Assert.Single(technical.SessionsReceived);
        Assert.NotSame(billingSession, technicalSession);
        Assert.Equal("Billing", Assert.IsType<StatefulAgent.StateSession>(billingSession).Owner);
        Assert.Equal("Technical", Assert.IsType<StatefulAgent.StateSession>(technicalSession).Owner);
        Assert.Empty(fallback.SessionsReceived);
    }

    [Fact]
    public async Task RunAsync_PerTargetCounterAdvancesIndependently()
    {
        var (router, billing, _, _, _) = CreateStatefulRouter();
        var routerSession = await router.CreateSessionAsync();

        var first = await router.RunAsync("invoice question", routerSession);
        var second = await router.RunAsync("another invoice question", routerSession);

        Assert.Equal("Billing#1", first.Messages[0].Text);
        Assert.Equal("Billing#2", second.Messages[0].Text);
        Assert.Same(billing.SessionsReceived[0], billing.SessionsReceived[1]);
        Assert.Equal(1, billing.CreateCalls);
    }

    [Fact]
    public async Task Session_RoundTrip_PreservesPerTargetState()
    {
        var (router, billing, technical, _, _) = CreateStatefulRouter();
        var routerSession = await router.CreateSessionAsync();

        await router.RunAsync("invoice question", routerSession); // billing state: counter 1
        var serialized = await router.SerializeSessionAsync(routerSession);
        var restored = await router.DeserializeSessionAsync(serialized);

        var followUp = await router.RunAsync("one more invoice question", restored);

        Assert.Equal("Billing#2", followUp.Messages[0].Text); // billing counter survived the round-trip
        Assert.Equal(1, billing.CreateCalls);                 // no new session after restore
        Assert.Equal(1, billing.DeserializeCalls);            // state came from deserialization
        Assert.Empty(technical.SessionsReceived);
    }

    [Fact]
    public async Task RunAsync_WrongSessionType_ThrowsArgument()
    {
        var (router, _, _, _, _) = CreateStatefulRouter();

        await Assert.ThrowsAsync<ArgumentException>(() => router.RunAsync("message", new TypeSafeAgentSession()));
    }

    [Fact]
    public async Task SerializeSession_WrongSessionType_ThrowsArgument()
    {
        var (router, _, _, _, _) = CreateStatefulRouter();

        await Assert.ThrowsAsync<ArgumentException>(async () => await router.SerializeSessionAsync(new TypeSafeAgentSession()));
    }

    [Fact]
    public async Task DeserializeSession_RejectsWrongKindAndWrongVersion()
    {
        var (router, _, _, _, _) = CreateStatefulRouter();

        await Assert.ThrowsAsync<ArgumentException>(async () => await router.DeserializeSessionAsync(JsonDocument.Parse("{}").RootElement.Clone()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await router.DeserializeSessionAsync(JsonDocument.Parse("""{"kind":"other","version":1}""").RootElement.Clone()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await router.DeserializeSessionAsync(JsonDocument.Parse("""{"kind":"typesafe-router","version":42}""").RootElement.Clone()));
    }

    [Fact]
    public async Task RunAsync_BlankInput_FallsBackWithoutUpstreamCall()
    {
        var explodingClient = new FakeTypeSafeClient((_, _, _) =>
            throw new InvalidOperationException("The client must not be called for blank input."));
        var fallback = new EchoAgent("EscalationAgent");

        var router = new TypeSafeRouterAgent(
            explodingClient,
            questionId: "intent",
            instructions: "Determine customer intent",
            options: opts => opts.Option("billing", "Billing"),
            routeTargets: new Dictionary<string, AIAgent> { ["billing"] = new EchoAgent("BillingAgent") },
            fallbackAgent: fallback,
            minimumConfidence: 0.75);

        var response = await router.RunAsync([new ChatMessage(ChatRole.User, "   ")]);

        Assert.Equal("EscalationAgent", response.AdditionalProperties!["typesafe.routed_to"]);
        Assert.Equal("fallback (no evaluable input)", response.AdditionalProperties["typesafe.routing_decision"]);
    }

    [Fact]
    public async Task Session_RestoresIntoRecreatedAgentsIncludingFallbackAndApplicationState()
    {
        var original = CreateStatefulRouter().Router;
        var session = await original.CreateSessionAsync();
        session.StateBag.SetValue("application", "saved");
        await original.RunAsync("invoice", session);
        await original.RunAsync([new ChatMessage(ChatRole.User, " ")], session);
        var state = await original.SerializeSessionAsync(session);
        Assert.Equal(2, state.GetProperty("version").GetInt32());
        Assert.True(state.GetProperty("targets").TryGetProperty("billing", out _));
        var recreated = CreateStatefulRouter().Router;
        var restored = await recreated.DeserializeSessionAsync(state);
        Assert.Equal("saved", restored.StateBag.GetValue<string>("application"));
        Assert.Equal("Billing#2", (await recreated.RunAsync("invoice", restored)).Text);
        Assert.Equal("Escalation#2", (await recreated.RunAsync([new ChatMessage(ChatRole.User, " ")], restored)).Text);
    }

    [Fact]
    public async Task RoutesSharingAgentInstance_HaveIndependentHistories()
    {
        var setup = CreateStatefulRouter();
        var shared = new StatefulAgent("Shared");
        var router = new TypeSafeRouterAgent(setup.Client, "intent", "route",
            q => q.Option("billing", "billing").Option("technical", "technical"),
            new Dictionary<string, AIAgent> { ["billing"] = shared, ["technical"] = shared }, shared);
        var session = await router.CreateSessionAsync();
        Assert.Equal("Shared#1", (await router.RunAsync("invoice", session)).Text);
        Assert.Equal("Shared#1", (await router.RunAsync("technical", session)).Text);
        Assert.Equal("Shared#1", (await router.RunAsync([new ChatMessage(ChatRole.User, " ")], session)).Text);
        Assert.Equal("Shared#2", (await router.RunAsync("invoice", session)).Text);
        Assert.NotSame(shared.SessionsReceived[0], shared.SessionsReceived[1]);
        Assert.NotSame(shared.SessionsReceived[0], shared.SessionsReceived[2]);
    }

    [Fact]
    public async Task LegacyState_RequiresResolvableIdsAndStreamingValidatesSession()
    {
        var (router, billing, _, _, _) = CreateStatefulRouter();
        var legacy = JsonDocument.Parse("{\"kind\":\"typesafe-router\",\"version\":1,\"targets\":{\"" + billing.Id + "\":{\"owner\":\"Billing\",\"counter\":7}}}").RootElement;
        var restored = await router.DeserializeSessionAsync(legacy);
        Assert.Equal("Billing#8", (await router.RunAsync("invoice", restored)).Text);
        var recreated = CreateStatefulRouter().Router;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recreated.DeserializeSessionAsync(legacy));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in router.RunStreamingAsync("invoice", new TypeSafeAgentSession())) { }
        });
    }
}
