using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeToolSelectionTests
{
    private sealed class ContextAgent : AIAgent
    {
        public override string Name => "ContextAgent";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new TypeSafeAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(default(JsonElement));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentSession>(new TypeSafeAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "OK")));

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "OK");
            await Task.CompletedTask;
        }
    }

    private static AIFunction CreateDummyFunction(string name, string description)
    {
        return AIFunctionFactory.Create(() => "result", name: name, description: description);
    }

    [Fact]
    public async Task ShortlistToolsAsync_WhenToolsCountBelowTopK_StillEvaluates()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("query_db", "Queries the database."),
            CreateDummyFunction("send_email", "Sends email.")
        };

        var fakeClient = new FakeTypeSafeClient((_, q, _) => FakeTypeSafeClient.CreateSampleResponse(answers: q.Keys.ToDictionary(k => k, _ => (TypeSafeAnswer)new NoulAnswer { Noul = 0.9 })));
        var shortlisted = await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("Some user query"),
            new TypeSafeToolSelectionOptions { TopK = 5 });

        Assert.Equal(2, shortlisted.Count);
        Assert.Single(fakeClient.Invocations);
    }

    [Fact]
    public async Task ShortlistToolsAsync_RanksToolsByProbabilityAndReturnsTopK()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("query_sql", "Executes SQL query."),
            CreateDummyFunction("generate_invoice", "Generates PDF invoice."),
            CreateDummyFunction("send_email", "Sends an email."),
            CreateDummyFunction("restart_server", "Restarts the production server.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.80,
                ["send_email"] = 0.75,
                ["query_sql"] = 0.04,
                ["restart_server"] = 0.01
            };

        var response = FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer));

        var fakeClient = new FakeTypeSafeClient(response);
        var shortlisted = await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("Please create invoice for customer #123 and email it"),
            new TypeSafeToolSelectionOptions { TopK = 2 });

        Assert.Equal(2, shortlisted.Count);
        Assert.Equal("generate_invoice", ((AIFunction)shortlisted[0]).Name);
        Assert.Equal("send_email", ((AIFunction)shortlisted[1]).Name);
        Assert.Single(fakeClient.Invocations);
    }

    [Fact]
    public async Task ShortlistToolsAsync_WithMinimumConfidence_FiltersOutLowRelevance()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("generate_invoice", "Generates PDF invoice."),
            CreateDummyFunction("send_email", "Sends an email."),
            CreateDummyFunction("query_sql", "Executes SQL query.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.85,
                ["send_email"] = 0.10,
                ["query_sql"] = 0.05
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var shortlisted = await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("Create an invoice"),
            new TypeSafeToolSelectionOptions
            {
                TopK = 5,
                MinimumRelevanceProbability = 0.50
            });

        Assert.Single(shortlisted);
        Assert.Equal("generate_invoice", ((AIFunction)shortlisted[0]).Name);
    }

    [Fact]
    public async Task ShortlistToolsAsync_AlwaysIncludeTools_PreservesSpecifiedTools()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("generate_invoice", "Generates PDF invoice."),
            CreateDummyFunction("send_email", "Sends an email."),
            CreateDummyFunction("audit_log", "Writes an audit log.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.95,
                ["send_email"] = 0.04,
                ["audit_log"] = 0.01
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var options = new TypeSafeToolSelectionOptions
        {
            TopK = 1,
            MinimumRelevanceProbability = 0.50
        };
        options.AlwaysIncludeToolNames.Add("audit_log");

        var shortlisted = await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("Create invoice"),
            options);

        Assert.Equal(2, shortlisted.Count);
        Assert.Contains(shortlisted, t => ((AIFunction)t).Name == "generate_invoice");
        Assert.Contains(shortlisted, t => ((AIFunction)t).Name == "audit_log");
    }

    [Fact]
    public async Task ShortlistToolsAsync_WhenClientThrows_AppliesFallbackModes()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("tool1", "Tool 1 description."),
            CreateDummyFunction("tool2", "Tool 2 description.")
        };

        var failingClient = new FakeTypeSafeClient((_, _, _) => throw new HttpRequestException("Network failure"));
        var state = TypeSafeContent.FromString("Some query");

        // IncludeAll (default)
        var allResult = await TypeSafeTools.ShortlistToolsAsync(
            failingClient,
            tools,
            state,
            new TypeSafeToolSelectionOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.IncludeAll });
        Assert.Equal(2, allResult.Count);

        // IncludeNone
        var noneResult = await TypeSafeTools.ShortlistToolsAsync(
            failingClient,
            tools,
            state,
            new TypeSafeToolSelectionOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.IncludeNone });
        Assert.Empty(noneResult);

        // Throw
        await Assert.ThrowsAsync<HttpRequestException>(() => TypeSafeTools.ShortlistToolsAsync(
            failingClient,
            tools,
            state,
            new TypeSafeToolSelectionOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.Throw }));
    }

    [Fact]
    public async Task InvokingCoreAsync_InProvider_ReplacesContextToolsWithShortlistedSubset()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("query_sql", "Executes SQL query."),
            CreateDummyFunction("generate_invoice", "Generates PDF invoice."),
            CreateDummyFunction("send_email", "Sends an email.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.90,
                ["send_email"] = 0.08,
                ["query_sql"] = 0.02
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var provider = new TypeSafeToolSelectionProvider(fakeClient, new TypeSafeToolSelectionOptions
        {
            TopK = 1
        });

        var agent = new ContextAgent();
        var initialContext = new AIContext
        {
            Tools = tools,
            Messages = new List<ChatMessage> { new(ChatRole.User, "Create an invoice for order 99") }
        };

        var invokingContext = new AIContextProvider.InvokingContext(agent, null, initialContext);

        var invokingCoreMethod = typeof(TypeSafeToolSelectionProvider).GetMethod(
            "InvokingCoreAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (ValueTask<AIContext>)invokingCoreMethod!.Invoke(provider, [invokingContext, CancellationToken.None])!;
        var resultContext = await task;

        var resultTools = resultContext.Tools?.ToList();
        Assert.NotNull(resultTools);
        Assert.Single(resultTools);
        Assert.Equal("generate_invoice", ((AIFunction)resultTools[0]).Name);
    }

    [Fact]
    public async Task CreateToolSelectionTool_WhenInvokedByAgent_ReturnsRelevanceRanking()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("query_sql", "Executes SQL query."),
            CreateDummyFunction("generate_invoice", "Generates PDF invoice.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.95,
                ["query_sql"] = 0.05
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var selectionTool = TypeSafeTools.CreateToolSelectionTool(
            fakeClient,
            tools,
            name: "recommend_tools",
            topK: 1);

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = "I need an invoice generated"
        });

        var result = await selectionTool.InvokeAsync(args);
        var recommended = Assert.IsAssignableFrom<string[]>(result);
        Assert.Single(recommended);
        Assert.Equal("generate_invoice", recommended[0]);
    }

    [Fact]
    public async Task ShortlistToolsAsync_WithStructuredMetadata_IncludesParameterSchemaInCriteria()
    {
        var weatherTool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("Target city name")] string city, [System.ComponentModel.Description("Forecast days")] int days) => "weather",
            name: "get_weather",
            description: "Fetches current weather and forecast.");

        var tools = new List<AITool> { weatherTool, CreateDummyFunction("ping", "Pings the server.") };

        var fakeClient = new FakeTypeSafeClient((_, q, _) => FakeTypeSafeClient.CreateSampleResponse(answers: q.Keys.ToDictionary(k => k, _ => (TypeSafeAnswer)new NoulAnswer { Noul = 0.9 })));
        await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("What is the forecast?"),
            new TypeSafeToolSelectionOptions { TopK = 1, IncludeStructuredMetadata = true });

        Assert.Single(fakeClient.Invocations);
        var questions = fakeClient.Invocations[0].Questions;
        var question = Assert.IsType<Noul>(questions["r00000000"]);
        var weatherCriteria = question.Instructions;
        Assert.NotNull(weatherCriteria);
        var text = weatherCriteria.ToJsonNode().GetValue<string>();
        Assert.NotNull(text);
        Assert.Contains("Parameters:", text);
        Assert.Contains("city (string): Target city name", text);
        Assert.Contains("days (integer): Forecast days", text);
    }

    [Fact]
    public async Task ShortlistToolsAsync_WithStructuredMetadataDisabled_OmitsParameterSchema()
    {
        var weatherTool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("Target city name")] string city) => "weather",
            name: "get_weather",
            description: "Fetches current weather.");

        var tools = new List<AITool> { weatherTool, CreateDummyFunction("ping", "Pings the server.") };

        var fakeClient = new FakeTypeSafeClient((_, q, _) => FakeTypeSafeClient.CreateSampleResponse(answers: q.Keys.ToDictionary(k => k, _ => (TypeSafeAnswer)new NoulAnswer { Noul = 0.9 })));
        await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("What is the weather?"),
            new TypeSafeToolSelectionOptions { TopK = 1, IncludeStructuredMetadata = false });

        Assert.Single(fakeClient.Invocations);
        var questions = fakeClient.Invocations[0].Questions;
        var question = Assert.IsType<Noul>(questions["r00000000"]);
        var weatherCriteria = question.Instructions;
        Assert.NotNull(weatherCriteria);
        var text = weatherCriteria.ToJsonNode().GetValue<string>();
        Assert.NotNull(text);
        Assert.DoesNotContain("Parameters:", text);
        Assert.Contains("Name: get_weather", text);
        Assert.Contains("Fetches current weather.", text);
    }

    [Fact]
    public async Task ShortlistToolsAsync_WithDropOffRatio_FiltersOutCandidatesBelowRelativeThreshold()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("tool_a", "Primary match."),
            CreateDummyFunction("tool_b", "Close secondary match."),
            CreateDummyFunction("tool_c", "Distant match."),
            CreateDummyFunction("tool_d", "Irrelevant match.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["tool_a"] = 0.80,
                ["tool_b"] = 0.75,
                ["tool_c"] = 0.50,
                ["tool_d"] = 0.10
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        // TopK = 4, but DropOffRatio = 0.90 -> cutoff = 0.80 * 0.90 = 0.72
        // tool_a (0.80 >= 0.72) and tool_b (0.75 >= 0.72) pass
        // tool_c (0.50 < 0.72) and tool_d (0.10 < 0.72) are dropped
        var shortlisted = await TypeSafeTools.ShortlistToolsAsync(
            fakeClient,
            tools,
            TypeSafeContent.FromString("Some query"),
            new TypeSafeToolSelectionOptions { TopK = 4, DropOffRatio = 0.90 });

        Assert.Equal(2, shortlisted.Count);
        Assert.Equal("tool_a", ((AIFunction)shortlisted[0]).Name);
        Assert.Equal("tool_b", ((AIFunction)shortlisted[1]).Name);
    }

    [Fact]
    public async Task CreateDynamicToolExpansionTool_WhenAmbientContextNull_RejectsUnmanagedInvocation()
    {
        var tools = new List<AITool>
        {
            CreateDummyFunction("query_sql", "Executes SQL query."),
            CreateDummyFunction("generate_invoice", "Generates PDF invoice.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.95,
                ["query_sql"] = 0.05
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: tools.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var expansionTool = TypeSafeTools.CreateDynamicToolExpansionTool(
            fakeClient,
            tools,
            name: "request_tools",
            topK: 1);

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = "I need an invoice generated"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await expansionTool.InvokeAsync(args));
    }

    private sealed class MockDynamicExpansionChatClient : IChatClient
    {
        private int _callCount;

        public ChatClientMetadata Metadata => new("MockDynamicExpansionChatClient");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                // First turn: LLM calls request_tools
                var callContent = new FunctionCallContent("call_1", "request_tools", new Dictionary<string, object?>
                {
                    ["state"] = "Need to generate an invoice"
                });
                var msg = new ChatMessage(ChatRole.Assistant, [callContent]);
                return Task.FromResult(new ChatResponse(msg));
            }

            // Second turn: LLM sees the newly added tools in options.Tools
            var toolNames = options?.Tools?.OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];
            var text = $"Tools: {string.Join(",", toolNames)}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task CreateDynamicToolExpansionTool_WithManagedIntegration_PreservesCallerOptions()
    {
        var catalog = new List<AITool>
        {
            CreateDummyFunction("query_sql", "Executes SQL query."),
            CreateDummyFunction("generate_invoice", "Generates PDF invoice.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["generate_invoice"] = 0.95,
                ["query_sql"] = 0.05
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: catalog.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var requestTools = TypeSafeTools.CreateDynamicToolExpansionTool(
            fakeClient,
            catalog,
            name: "request_tools",
            topK: 1);

        using var innerClient = new MockDynamicExpansionChatClient();
        using var invokingClient = new ChatClientBuilder(innerClient).UseTypeSafeToolExpansion().Build();

        var options = new ChatOptions
        {
            Tools = [requestTools]
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Can you create an invoice for order 402?")
        };

        var response = await invokingClient.GetResponseAsync(messages, options);

        Assert.NotNull(response);
        Assert.Contains("generate_invoice", response.Text);
        // Verify that options.Tools now contains both request_tools and generate_invoice!
        Assert.Single(options.Tools);
        Assert.DoesNotContain(options.Tools, t => t.Name == "generate_invoice");
    }
}
