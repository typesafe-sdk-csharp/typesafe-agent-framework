using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class ReleaseContractTests
{
    private static readonly IReadOnlyDictionary<string, TypeSafeQuestion> Questions = TypeSafe.AI.Questions.Build(q => q.Noul("unsafe", "Unsafe?"));
    private static FakeTypeSafeClient Screener() => new((state, _, _) => FakeTypeSafeClient.CreateSampleResponse(answers: new()
    {
        ["unsafe"] = new NoulAnswer { Noul = state.ToJsonNode().ToJsonString().Contains("REJECTED", StringComparison.Ordinal) ? 1 : 0 }
    }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedHistory_NeverCommitsBlockedOutput(bool streaming)
    {
        var model = new RecordingModel();
        var history = new RecordingHistory();
        var agent = TypeSafeGuardrails.CreateChatClientAgent(model, Screener(), Questions,
            r => r.Nouls["unsafe"].Noul > .5, new() { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        if (streaming)
        {
            await foreach (var update in agent.RunStreamingAsync("first", session))
                Assert.DoesNotContain("REJECTED", update.Text);
        }
        else Assert.DoesNotContain("REJECTED", (await agent.RunAsync("first", session)).Text);
        Assert.DoesNotContain("REJECTED", (await agent.SerializeSessionAsync(session)).GetRawText());
        Assert.All(history.SuccessMessages, m => Assert.DoesNotContain("REJECTED", m.Text));
        await agent.RunAsync("second", session);
        Assert.All(model.Requests[1], m => Assert.DoesNotContain("REJECTED", m.Text));
        Assert.Contains(model.Requests[1], m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task ProtectedHistory_RejectsProviderStateAndPersistenceModes()
    {
        Assert.Throws<NotSupportedException>(() => TypeSafeGuardrails.CreateChatClientAgent(new RecordingModel(), Screener(), Questions,
            _ => false, new() { RequirePerServiceCallChatHistoryPersistence = true }));
        foreach (var options in new[] { new ChatOptions { ConversationId = "remote" }, new ChatOptions { AllowBackgroundResponses = true },
            new ChatOptions { ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1 }) } })
            Assert.Throws<NotSupportedException>(() => TypeSafeGuardrails.CreateChatClientAgent(new RecordingModel(), Screener(), Questions,
                _ => false, new() { ChatOptions = options }));
        var model = new RecordingModel { ConversationId = "remote" };
        var agent = TypeSafeGuardrails.CreateChatClientAgent(model, Screener(), Questions, _ => false);
        var session = await agent.CreateSessionAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.RunAsync("first", session));
        Assert.DoesNotContain("REJECTED", (await agent.SerializeSessionAsync(session)).GetRawText());
    }

    [Fact]
    public void Screening_CoversEvaluationAndToolPayloadsAndRejectsOpaqueContent()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse();
        var messages = new[]
        {
            TypeSafeContentConverter.ToChatMessage(response),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "search", new Dictionary<string, object?> { ["query"] = "sensitive-argument" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", JsonDocument.Parse("{\"value\":\"sensitive-result\"}").RootElement.Clone())])
        };
        var json = TypeSafeContentConverter.ScreeningExtractor.Extract(messages).ToJsonNode().ToJsonString();
        Assert.Contains("sensitive-argument", json);
        Assert.Contains("sensitive-result", json);
        Assert.Contains(response.RequestId!, json);
        Assert.Throws<NotSupportedException>(() => TypeSafeContentConverter.ScreeningExtractor.Extract(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", new object())])]));
        Assert.Throws<NotSupportedException>(() => TypeSafeContentConverter.ScreeningExtractor.Extract(
            [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"model\":null,\"answers\":{},\"usage\":{}}")]
    [InlineData("{\"model\":\"m\",\"answers\":[],\"usage\":{}}")]
    [InlineData("{\"model\":\"m\",\"answers\":{\"q\":{}},\"usage\":{}}")]
    [InlineData("{\"model\":\"m\",\"answers\":{},\"usage\":{},\"request_id\":1}")]
    public void MalformedResponses_AlwaysRaiseJsonException(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, TypeSafeJson.ResponseTypeInfo));

    [Fact]
    public void PublicResponseMetadata_PreservesRequestIdAndUnknownAnswers()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse(answers: new()
        {
            ["q"] = new UnknownAnswer { Type = "future", Raw = JsonDocument.Parse("{\"type\":\"future\",\"data\":[1,2]}").RootElement.Clone() }
        });
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(response, TypeSafeJson.ResponseTypeInfo), TypeSafeJson.ResponseTypeInfo)!;
        Assert.Equal(response.RequestId, restored.RequestId);
        Assert.Equal("[1,2]", Assert.IsType<UnknownAnswer>(restored.Answers["q"]).Raw.GetProperty("data").GetRawText());
    }

    [Theory]
    [InlineData(TypeSafeRelevanceFallbackMode.Throw)]
    [InlineData(TypeSafeRelevanceFallbackMode.IncludeAll)]
    [InlineData(TypeSafeRelevanceFallbackMode.IncludeNone)]
    public async Task Selection_EmptyMessagesAndServerFailuresHonorPolicy(TypeSafeRelevanceFallbackMode mode)
    {
        var catalog = new List<AITool> { AIFunctionFactory.Create(() => "ok", name: "mandatory") };
        var options = new TypeSafeToolSelectionOptions { FallbackMode = mode };
        options.AlwaysIncludeToolNames.Add("mandatory");
        var client = Screener();
        if (mode == TypeSafeRelevanceFallbackMode.Throw)
            await Assert.ThrowsAsync<InvalidOperationException>(() => TypeSafeTools.ShortlistToolsAsync(client, catalog, Array.Empty<ChatMessage>(), options));
        else Assert.Single(await TypeSafeTools.ShortlistToolsAsync(client, catalog, Array.Empty<ChatMessage>(), options));
        Assert.Empty(client.Invocations);
        var unavailable = new FakeTypeSafeClient((_, _, _) => throw new TypeSafeException("unavailable", System.Net.HttpStatusCode.ServiceUnavailable));
        if (mode == TypeSafeRelevanceFallbackMode.Throw)
            await Assert.ThrowsAsync<TypeSafeException>(() => TypeSafeTools.ShortlistToolsAsync(unavailable, catalog, "request", options));
        else Assert.Single(await TypeSafeTools.ShortlistToolsAsync(unavailable, catalog, "request", options));
    }

    [Fact]
    public async Task Router_RejectsOverlapAndReleasesGateOnStreamDisposal()
    {
        var target = new TypeSafeAgent(Screener(), Questions);
        var router = new TypeSafeRouterAgent(new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: new()
        {
            ["route"] = new ChoiceAnswer { Choice = "main", Confidence = 1, Probabilities = new Dictionary<string, double> { ["main"] = 1 } }
        })), "route", "Route", q => q.Option("main", "Main"), new Dictionary<string, AIAgent> { ["main"] = target }, target);
        var session = await router.CreateSessionAsync();
        var stream = router.RunStreamingAsync("request", session).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.RunAsync("overlap", session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.SerializeSessionAsync(session).AsTask());
        await router.RunAsync("independent", await router.CreateSessionAsync());
        await stream.DisposeAsync();
        await router.RunAsync("after disposal", session);
        await router.SerializeSessionAsync(session);
    }

    private sealed class RecordingHistory : ChatHistoryProvider
    {
        private readonly InMemoryChatHistoryProvider _inner = new();
        public override IReadOnlyList<string> StateKeys => _inner.StateKeys;
        protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default) => _inner.InvokingAsync(context, cancellationToken);
        public List<ChatMessage> SuccessMessages { get; } = [];
        protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            SuccessMessages.AddRange(context.ResponseMessages!);
            await _inner.InvokedAsync(context, cancellationToken);
        }
    }

    private sealed class RecordingModel : IChatClient
    {
        public string? ConversationId { get; init; }
        public List<ChatMessage[]> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Requests.Count == 1 ? "REJECTED" : "safe"))
            { ConversationId = ConversationId, RawRepresentation = "REJECTED" });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

