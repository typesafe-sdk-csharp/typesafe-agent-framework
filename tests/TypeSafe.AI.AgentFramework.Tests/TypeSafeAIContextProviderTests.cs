using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeAIContextProviderTests
{
    [Fact]
    public async Task InvokingAsync_InstructionsMode_InjectsEvaluationSummaryIntoInstructions()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var provider = new TypeSafeAIContextProvider(
            fakeClient,
            q => q.Noul("is_urgent", "Is the ticket urgent?"),
            mode: TypeSafeContextInjectionMode.Instructions);

        var session = new TypeSafeAgentSession();
        var incomingMessages = new[]
        {
            new ChatMessage(ChatRole.User, "Critical production outage!")
                .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "WebChat")
        };

        var initialContext = new AIContext
        {
            Instructions = "Base assistant instructions.",
            Messages = incomingMessages
        };

        var testAgent = new TypeSafeAgent(fakeClient, q => q.Noul("test", "test"));
        var invokingContext = new AIContextProvider.InvokingContext(
            agent: testAgent,
            session: session,
            aiContext: initialContext);

        var enrichedContext = await provider.InvokingAsync(invokingContext);

        Assert.NotNull(enrichedContext.Instructions);
        Assert.Contains("Base assistant instructions.", enrichedContext.Instructions);
        Assert.Contains("[TypeSafe Evaluation Context]", enrichedContext.Instructions);
        Assert.Contains("is_urgent: p=0.950 (yes)", enrichedContext.Instructions);

        // Verify session state caching
        Assert.True(session.StateBag.TryGetValue<SystemOneResponse>("typesafe.evaluation_context", out var cached));
        Assert.Same(sampleResponse, cached);
    }

    [Fact]
    public async Task InvokingAsync_MessagesMode_InjectsEvaluationMessage()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var provider = new TypeSafeAIContextProvider(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"),
            mode: TypeSafeContextInjectionMode.Messages);

        var incomingMessages = new[]
        {
            new ChatMessage(ChatRole.User, "Help needed")
                .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "Slack")
        };

        var testAgent = new TypeSafeAgent(fakeClient, q => q.Noul("test", "test"));
        var initialContext = new AIContext { Messages = incomingMessages };
        var invokingContext = new AIContextProvider.InvokingContext(agent: testAgent, session: null, aiContext: initialContext);

        var enrichedContext = await provider.InvokingAsync(invokingContext);

        Assert.NotNull(enrichedContext.Messages);
        var messageList = enrichedContext.Messages.ToList();
        Assert.Equal(2, messageList.Count);

        var injectedMessage = messageList[^1];
        Assert.Equal(ChatRole.Assistant, injectedMessage.Role);
        var evalContent = Assert.Single(injectedMessage.Contents.OfType<TypeSafeEvaluationContent>());
        Assert.Same(sampleResponse, evalContent.Response);
    }

    [Fact]
    public async Task InvokingAsync_ToolsMode_InjectsSelectedTools()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var mockTool = TypeSafeTools.CreateNoulTool(fakeClient, "sub_check", "Sub check");

        var provider = new TypeSafeAIContextProvider(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"),
            mode: TypeSafeContextInjectionMode.Tools,
            toolSelector: resp => resp.Nouls["is_urgent"].Noul > 0.8 ? [mockTool] : null);

        var incomingMessages = new[]
        {
            new ChatMessage(ChatRole.User, "Emergency!")
                .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "Portal")
        };

        var testAgent = new TypeSafeAgent(fakeClient, q => q.Noul("test", "test"));
        var initialContext = new AIContext { Messages = incomingMessages };
        var invokingContext = new AIContextProvider.InvokingContext(agent: testAgent, session: null, aiContext: initialContext);

        var enrichedContext = await provider.InvokingAsync(invokingContext);

        Assert.NotNull(enrichedContext.Tools);
        var injectedTool = Assert.Single(enrichedContext.Tools);
        Assert.Same(mockTool, injectedTool);
    }

    [Fact]
    public async Task InvokingAsync_EmptyMessages_DoesNotInvokeClient()
    {
        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        var provider = new TypeSafeAIContextProvider(fakeClient, q => q.Noul("q", "question"));

        var testAgent = new TypeSafeAgent(fakeClient, q => q.Noul("test", "test"));
        var initialContext = new AIContext { Messages = [] };
        var invokingContext = new AIContextProvider.InvokingContext(agent: testAgent, session: null, aiContext: initialContext);

        var enrichedContext = await provider.InvokingAsync(invokingContext);

        Assert.Empty(fakeClient.Invocations);
    }

    [Fact]
    public async Task InvokingAsync_WhitespaceOnlyMessage_DoesNotInvokeClient()
    {
        var explodingClient = new FakeTypeSafeClient((_, _, _) =>
            throw new InvalidOperationException("The client must not be called for blank input."));
        var provider = new TypeSafeAIContextProvider(explodingClient, q => q.Noul("q", "question"));

        var testAgent = new TypeSafeAgent(explodingClient, q => q.Noul("test", "test"));
        var initialContext = new AIContext
        {
            Messages =
            [
                new ChatMessage(ChatRole.User, "   ")
                    .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "WebChat")
            ]
        };
        var invokingContext = new AIContextProvider.InvokingContext(agent: testAgent, session: null, aiContext: initialContext);

        var enrichedContext = await provider.InvokingAsync(invokingContext);

        Assert.Empty(explodingClient.Invocations);
    }
}
