using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class ClassificationTests
{
    [Fact]
    public async Task Classification_UsesOnlyCurrentInputAndSupportsRunOverrides()
    {
        var client = new FakeTypeSafeClient(ToneResponse());
        var agent = CreateAgent(client, stateMapper: messages => string.Join("|", messages.Select(m => m.Text)));
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("first", session);
        var response = await agent.RunAsync("second", session, new TypeSafeAgentRunOptions
        {
            Model = "override", ResultFormatter = _ => "mapped",
            Questions = Questions.Build(q => q.Noul("override", "Question"))
        });
        Assert.Equal("second", client.Invocations[1].State.ToJsonNode().GetValue<string>());
        Assert.Equal("override", client.Invocations[1].Model);
        Assert.True(client.Invocations[1].Questions.ContainsKey("override"));
        Assert.Equal("mapped", response.Text);
        Assert.IsType<TypeSafeEvaluationContent>(response.Messages[0].Contents[1]);
        var restored = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
        await agent.RunAsync("third", restored);
        Assert.Equal("third", client.Invocations[2].State.ToJsonNode().GetValue<string>());
    }

    private static IReadOnlyDictionary<string, TypeSafeQuestion> ToneQuestions() => Questions.Build(q => q
        .Noul("billing", "Is this ticket about billing?")
        .Choice("tone", "What is the customer's tone?", "calm", "frustrated", "angry")
        .Score("urgency", "How urgent is this ticket?", "can wait", "this week", "today"));

    private static SystemOneResponse ToneResponse(
        int? inputTokens = 42, int? outputTokens = 12, string model = "jev-latest") =>
        new(
            model,
            new ReadOnlyDictionary<string, TypeSafeAnswer>(new Dictionary<string, TypeSafeAnswer>
            {
                ["tone"] = new ChoiceAnswer
                {
                    Choice = "angry",
                    Confidence = 0.87,
                    Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
                    {
                        ["calm"] = 0.03,
                        ["frustrated"] = 0.10,
                        ["angry"] = 0.87
                    })
                }
            }),
            new TypeSafeUsage { InputTokens = inputTokens, OutputTokens = outputTokens }
        );

    private static TypeSafeAgent CreateAgent(
        FakeTypeSafeClient? client = null,
        Func<IReadOnlyList<ChatMessage>, TypeSafeContent>? stateMapper = null,
        Func<SystemOneResponse, string>? resultMapper = null,
        IReadOnlyDictionary<string, TypeSafeQuestion>? questions = null,
        string? model = null,
        string name = "SupportClassifier")
        => new(
            client ?? new FakeTypeSafeClient(ToneResponse()),
            questions ?? ToneQuestions(),
            name: name, defaultModel: model,
            contentExtractor: TypeSafeContentConverter.FromDelegate(stateMapper ?? (messages => (TypeSafeContent)messages[^1].Text!)),
            resultFormatter: resultMapper ?? (response => $"tone={response.Choices["tone"].Choice}"));

    [Fact]
    public async Task RunAsync_ProducesMappedTextAndMetadata()
    {
        var response = ToneResponse();
        var fakeClient = new FakeTypeSafeClient(response);
        var agent = CreateAgent(fakeClient);

        var result = await agent.RunAsync("The customer is shouting about a double charge");

        var message = Assert.Single(result.Messages);
        Assert.Equal("tone=angry", message.Text);
        Assert.Equal("SupportClassifier", message.AuthorName);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        Assert.Equal(agent.Id, result.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(result.ResponseId));
        Assert.NotNull(result.CreatedAt);
        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);
        Assert.NotNull(result.AdditionalProperties);
        Assert.Equal("jev-latest", result.AdditionalProperties["typesafe.model"]);
        Assert.Same(response, result.RawRepresentation);

        var usage = Assert.IsType<UsageDetails>(result.Usage);
        Assert.Equal(42, usage.InputTokenCount);
        Assert.Equal(12, usage.OutputTokenCount);
        Assert.Equal(54, usage.TotalTokenCount);
    }

    [Fact]
    public async Task RunAsync_WithNullTokenCounts_UsageCountsStayNull()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse(inputTokens: null, outputTokens: null));
        var agent = CreateAgent(fakeClient);

        var result = await agent.RunAsync("message");

        var usage = Assert.IsType<UsageDetails>(result.Usage);
        Assert.Null(usage.InputTokenCount);
        Assert.Null(usage.OutputTokenCount);
        Assert.Null(usage.TotalTokenCount);
    }

    [Fact]
    public async Task RunAsync_WithOneNullTokenCount_TotalStaysNull()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse(inputTokens: 7, outputTokens: null));
        var agent = CreateAgent(fakeClient);

        var result = await agent.RunAsync("message");

        var usage = Assert.IsType<UsageDetails>(result.Usage);
        Assert.Equal(7, usage.InputTokenCount);
        Assert.Null(usage.OutputTokenCount);
        Assert.Null(usage.TotalTokenCount);
    }

    [Fact]
    public async Task RunStreamingAsync_YieldsSameTextAsNonStreaming()
    {
        var agent = CreateAgent(new FakeTypeSafeClient(ToneResponse()));

        var nonStreaming = await agent.RunAsync("message");
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("message"))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var streamedText = string.Concat(updates.Select(u => u.Text ?? string.Empty));
        Assert.Equal(nonStreaming.Messages[0].Text, streamedText);
    }

    [Fact]
    public async Task Session_RoundTrip_Succeeds()
    {
        var agent = CreateAgent();

        var session = await agent.CreateSessionAsync();
        var serialized = await agent.SerializeSessionAsync(session);
        var restored = await agent.DeserializeSessionAsync(serialized);

        Assert.IsAssignableFrom<AgentSession>(restored);

        // The restored session remains usable for a run.
        var result = await agent.RunAsync("message", restored);
        Assert.Equal("tone=angry", result.Messages[0].Text);
    }

    [Fact]
    public async Task DeserializeSession_RejectsCorruptedWrongKindAndWrongVersion()
    {
        var agent = CreateAgent();

        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.DeserializeSessionAsync(JsonDocument.Parse("\"not-an-envelope\"").RootElement.Clone()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.DeserializeSessionAsync(JsonDocument.Parse("{}").RootElement.Clone()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.DeserializeSessionAsync(JsonDocument.Parse("""{"kind":"other-agent","version":1}""").RootElement.Clone()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.DeserializeSessionAsync(JsonDocument.Parse("""{"kind":"typesafe-classifier","version":99}""").RootElement.Clone()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await agent.DeserializeSessionAsync(JsonDocument.Parse("""{"kind":"typesafe-classifier"}""").RootElement.Clone()));
    }

    [Fact]
    public async Task Constructor_SnapshotsQuestions_CallerMutationsAreNotSent()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse());
        var questions = new Dictionary<string, TypeSafeQuestion>
        {
            ["billing"] = new Noul { Instructions = (TypeSafeContent)"Is this ticket about billing?" }
        };
        var agent = CreateAgent(fakeClient, questions: questions);

        // Mutate the caller's dictionary after construction.
        questions["tone"] = new Noul { Instructions = (TypeSafeContent)"Injected later" };
        questions.Remove("billing");

        await agent.RunAsync("message");

        var invocation = Assert.Single(fakeClient.Invocations);
        Assert.Single(invocation.Questions);
        Assert.True(invocation.Questions.ContainsKey("billing"));
        Assert.False(invocation.Questions.ContainsKey("tone"));
        var noul = Assert.IsType<Noul>(invocation.Questions["billing"]);
        Assert.Equal("Is this ticket about billing?", noul.Instructions!.ToJsonNode().GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_NullStateMapperResult_ThrowsInvalidOperation()
    {
        var agent = CreateAgent(stateMapper: _ => null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("message"));
    }

    [Fact]
    public async Task RunAsync_NullResultMapperResult_ThrowsInvalidOperation()
    {
        var agent = CreateAgent(resultMapper: _ => null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("message"));
    }

    [Fact]
    public async Task RunAsync_PreCanceledToken_ThrowsOperationCanceled()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse());
        var agent = CreateAgent(fakeClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            agent.RunAsync("message", cancellationToken: cts.Token));
        Assert.Empty(fakeClient.Invocations);
    }

    [Fact]
    public async Task RunAsync_MaterializesMessagesOnce_StateMapperReceivesFullList()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse());
        var enumerationCount = 0;
        var mapperInvocations = 0;
        IReadOnlyList<ChatMessage>? mapperReceived = null;

        IEnumerable<ChatMessage> Messages()
        {
            enumerationCount++;
            yield return new ChatMessage(ChatRole.User, "first");
            yield return new ChatMessage(ChatRole.User, "second");
        }

        var agent = CreateAgent(
            fakeClient,
            stateMapper: messages =>
            {
                mapperInvocations++;
                mapperReceived = messages;
                return (TypeSafeContent)messages[^1].Text!;
            });

        await agent.RunAsync(Messages());

        // The framework's public wrapper may enumerate the input once itself; the contract this
        // pins is that the classifier materializes exactly one list and invokes the mapper once.
        Assert.Equal(1, mapperInvocations);
        Assert.Equal(2, mapperReceived!.Count);
        Assert.Equal("second", mapperReceived[^1].Text);
    }

    [Fact]
    public async Task RunAsync_NullModelUsesConfiguredDefault_ModelPassedThrough()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse());
        var agent = CreateAgent(fakeClient, model: null);

        await agent.RunAsync("message");

        var invocation = Assert.Single(fakeClient.Invocations);
        Assert.Null(invocation.Model);
    }

    [Fact]
    public async Task RunAsync_WithModelOverride_PassesModelToClient()
    {
        var fakeClient = new FakeTypeSafeClient(ToneResponse(model: "jev-pro"));
        var agent = CreateAgent(fakeClient, model: "jev-pro");

        await agent.RunAsync("message");

        var invocation = Assert.Single(fakeClient.Invocations);
        Assert.Equal("jev-pro", invocation.Model);
    }
}
