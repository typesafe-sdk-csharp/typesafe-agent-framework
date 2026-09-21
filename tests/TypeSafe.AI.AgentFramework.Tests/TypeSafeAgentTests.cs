using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeAgentTests
{
    [Fact]
    public async Task RunAsync_WithDefaultQuestions_EvaluatesAndReturnsAgentResponse()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var agent = new TypeSafeAgent(
            fakeClient,
            q => q.Noul("is_urgent", "Is the request urgent?"),
            name: "UrgencyClassifier");

        var response = await agent.RunAsync("My server is on fire!");

        Assert.NotNull(response);
        Assert.Single(response.Messages);
        Assert.Equal("UrgencyClassifier", response.Messages[0].AuthorName);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Same(sampleResponse, response.RawRepresentation);

        var evalContent = Assert.Single(response.Messages[0].Contents.OfType<TypeSafeEvaluationContent>());
        Assert.Same(sampleResponse, evalContent.Response);

        Assert.Single(fakeClient.Invocations);
        var invocation = fakeClient.Invocations[0];
        Assert.Equal("\"My server is on fire!\"", JsonSerializer.Serialize(invocation.State, AgentFrameworkJsonContext.Default.TypeSafeContent));
        Assert.True(invocation.Questions.ContainsKey("is_urgent"));
    }

    [Fact]
    public async Task RunAsync_WithDynamicQuestions_OverridesDefaultQuestions()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var agent = new TypeSafeAgent(
            fakeClient,
            q => q.Noul("default_q", "Default question"));

        var dynamicQuestions = Questions.Build(q => q.Choice("dynamic_choice", "Which intent?", "refund", "support"));
        var options = new TypeSafeAgentRunOptions
        {
            Questions = dynamicQuestions,
            Model = "jev-fast"
        };

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "Need help with billing")], null, options);

        Assert.NotNull(response);
        Assert.Single(fakeClient.Invocations);
        var invocation = fakeClient.Invocations[0];
        Assert.Equal("jev-fast", invocation.Model);
        Assert.True(invocation.Questions.ContainsKey("dynamic_choice"));
        Assert.False(invocation.Questions.ContainsKey("default_q"));
    }

    [Fact]
    public async Task RunAsync_StructuredOutput_ReturnsTypedSystemOneResponse()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var agent = new TypeSafeAgent(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"));

        await Assert.ThrowsAsync<NotSupportedException>(() => agent.RunAsync<SystemOneResponse>(
            new ChatMessage(ChatRole.User, "High priority ticket"), serializerOptions: TypeSafeJson.ResponseTypeInfo.Options));
        Assert.Empty(fakeClient.Invocations);
        var result = await agent.RunEvaluationAsync([new ChatMessage(ChatRole.User, "High priority ticket")]);
        Assert.Equal(sampleResponse.RequestId, result.RequestId);
        Assert.NotNull(result);
        Assert.Equal("jev-latest", result.Model);
        Assert.True(result.Nouls.ContainsKey("is_urgent"));
        Assert.Equal(0.95, result.Nouls["is_urgent"].Noul);
    }

    [Fact]
    public async Task RunStreamingAsync_YieldsAgentResponseUpdates()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var agent = new TypeSafeAgent(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"));

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("Stream this query"))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var textUpdates = updates.Where(u => !string.IsNullOrEmpty(u.Text)).ToList();
        Assert.NotEmpty(textUpdates);
    }

    [Fact]
    public async Task Session_TracksLastEvaluationAndSurvivesSerializationRoundtrip()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var agent = new TypeSafeAgent(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"));

        var session = await agent.CreateSessionAsync();
        Assert.IsType<TypeSafeAgentSession>(session);

        await agent.RunAsync("First message", session);

        var tsSession = (TypeSafeAgentSession)session;
        Assert.NotNull(tsSession.LastResponse);
        Assert.Equal("jev-latest", tsSession.LastResponse.Model);

        // Verify session serialization roundtrip
        var serialized = await agent.SerializeSessionAsync(session);
        var restored = await agent.DeserializeSessionAsync(serialized);

        var restoredTsSession = Assert.IsType<TypeSafeAgentSession>(restored);
        Assert.NotNull(restoredTsSession.LastResponse);
        Assert.Equal("jev-latest", restoredTsSession.LastResponse.Model);
    }

    [Fact]
    public async Task RunAsync_ThrowsIfNoQuestionsProvided()
    {
        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        var agent = new TypeSafeAgent(fakeClient, defaultQuestions: (IReadOnlyDictionary<string, TypeSafeQuestion>?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("Query without questions"));
    }

    [Fact]
    public async Task RunAsync_UsesCustomResultFormatterWhenProvided()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var agent = new TypeSafeAgent(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"),
            resultFormatter: resp => $"CustomUrgent={resp.Nouls["is_urgent"].Noul}");

        var response = await agent.RunAsync("Check custom formatting");
        Assert.Equal("CustomUrgent=0.95", response.Messages[0].Text);
    }
}

