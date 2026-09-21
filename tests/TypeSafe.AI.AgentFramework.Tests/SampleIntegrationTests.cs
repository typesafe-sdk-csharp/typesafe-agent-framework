using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TypeSafe.Sample.ChatWithSql;
using TypeSafe.Sample.ToolSelection;

namespace TypeSafe.AI.AgentFramework.Tests;

public class SampleIntegrationTests
{
    [Fact]
    public async Task Selection_UsesSampleConfigurationAndDoesNotMutateCatalog()
    {
        var client = new FakeTypeSafeClient((_, questions, _) => FakeTypeSafeClient.CreateSampleResponse(
            answers: questions.Keys.Select((key, i) => (key, score: new[] { .99, .40, .30, .05, .01 }[i]))
                .ToDictionary(p => p.key, p => (TypeSafeAnswer)new NoulAnswer { Noul = p.score })));
        var options = SelectionScenario.CreateOptions();
        var provider = new TypeSafeToolSelectionProvider(client, options);
        var catalog = SelectionScenario.CreateCatalog();
        var model = new RecordingModel();
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            ChatOptions = new() { Tools = catalog }, AIContextProviders = [provider]
        });
        await agent.RunAsync("Review pull request #123.");
        Assert.Equal(new[] { "pull_request_read" }, model.Tools);
        Assert.Equal(5, catalog.Count);
    }

    [Fact]
    public async Task Selection_SampleFailsClosedWhenEvaluationFails()
    {
        var client = new FakeTypeSafeClient((_, _, _) => throw new TypeSafeConnectionException("offline"));
        var agent = new ChatClientAgent(new RecordingModel(), new ChatClientAgentOptions
        {
            ChatOptions = new() { Tools = SelectionScenario.CreateCatalog() },
            AIContextProviders = [SelectionScenario.CreateProvider(client)]
        });
        await Assert.ThrowsAsync<TypeSafeConnectionException>(() => agent.RunAsync("Review PR"));
    }

    [Theory]
    [InlineData("read_only", 0.1)]
    [InlineData("authorized", 0.1)]
    [InlineData("aligned", 0.1)]
    public async Task Sql_AnyRejectedCriterionPreventsExecution(string id, double probability)
    {
        var answers = ApprovedAnswers();
        answers[id] = new NoulAnswer { Noul = probability };
        var database = new FixtureDatabase();
        using var provider = SqlServices(database, answers);
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<VerifiedSqlExecutor>();
        Assert.StartsWith("DENIED", await executor.ExecuteAsync(FixtureDatabase.AllowedQuery, "List customer names"));
        Assert.Equal(0, database.ExecutionCount);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task Sql_InvalidAnswersPreventExecution(double probability)
    {
        var answers = ApprovedAnswers();
        answers["read_only"] = new NoulAnswer { Noul = probability };
        var database = new FixtureDatabase();
        using var provider = SqlServices(database, answers);
        using var scope = provider.CreateScope();
        await Assert.ThrowsAsync<TypeSafeProtocolException>(() =>
            scope.ServiceProvider.GetRequiredService<VerifiedSqlExecutor>().ExecuteAsync(FixtureDatabase.AllowedQuery, "List customer names"));
        Assert.Equal(0, database.ExecutionCount);
    }

    [Fact]
    public async Task Sql_MissingAnswerAndEvaluationFailurePreventExecution()
    {
        var database = new FixtureDatabase();
        using var missingProvider = SqlServices(database, new());
        using var missingScope = missingProvider.CreateScope();
        await Assert.ThrowsAsync<TypeSafeProtocolException>(() =>
            missingScope.ServiceProvider.GetRequiredService<VerifiedSqlExecutor>().ExecuteAsync(FixtureDatabase.AllowedQuery, "List names"));
        var client = new FakeTypeSafeClient((_, _, _) => throw new HttpRequestException("service unavailable"));
        using var failedProvider = SqlServices(database, client: client);
        using var failedScope = failedProvider.CreateScope();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            failedScope.ServiceProvider.GetRequiredService<VerifiedSqlExecutor>().ExecuteAsync(FixtureDatabase.AllowedQuery, "List names"));
        Assert.Equal(0, database.ExecutionCount);
    }

    [Fact]
    public async Task Sql_ApprovedQueryExecutesOnce()
    {
        var database = new FixtureDatabase();
        using var provider = SqlServices(database, ApprovedAnswers());
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<VerifiedSqlExecutor>()
            .ExecuteAsync(FixtureDatabase.AllowedQuery, "List names");
        Assert.Contains("Acme", result);
        Assert.Equal(1, database.ExecutionCount);
    }

    [Fact]
    public async Task Sql_ModelFailureRemainsVisibleWithoutSimulation()
    {
        var client = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(
            answers: new() { ["customers_relevant"] = new NoulAnswer { Noul = .99 } }));
        var database = new FixtureDatabase();
        using var provider = SqlServices(database, client: client, model: new RecordingModel { Fail = true });
        using var scope = provider.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<AIAgent>();
        await Assert.ThrowsAsync<HttpRequestException>(() => agent.RunAsync("List names"));
        Assert.Equal(0, database.ExecutionCount);
    }

    private static Dictionary<string, TypeSafeAnswer> ApprovedAnswers() =>
        new[] { "read_only", "authorized", "aligned" }.ToDictionary(id => id, _ => (TypeSafeAnswer)new NoulAnswer { Noul = .99 });
    [Fact]
    public void Sql_ServicesAreSharedWithinConversationAndIsolatedBetweenScopes()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITypeSafeClient>(_ => new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse()));
        services.AddScoped<IQueryDatabase, FixtureDatabase>();
        services.AddScoped<IChatClient, RecordingModel>();
        services.AddVerifiedSqlAgent();
        Assert.All(services, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        foreach (var type in new[] { typeof(AIAgent), typeof(VerifiedSqlExecutor), typeof(JevSqlVerifier),
            typeof(TypeSafeAgent), typeof(TypeSafeFunction), typeof(SqlExecutionTool), typeof(SqlSchemaContextProvider),
            typeof(IQueryDatabase), typeof(IChatClient) })
        {
            var instance = first.ServiceProvider.GetRequiredService(type);
            Assert.Same(instance, first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(instance, second.ServiceProvider.GetRequiredService(type));
        }
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<AIAgent>());
    }

    private static ServiceProvider SqlServices(FixtureDatabase database,
        Dictionary<string, TypeSafeAnswer>? answers = null, ITypeSafeClient? client = null, IChatClient? model = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<ITypeSafeClient>(_ => client ?? new FakeTypeSafeClient(
            FakeTypeSafeClient.CreateSampleResponse(answers: answers ?? ApprovedAnswers())));
        services.AddScoped<IQueryDatabase>(_ => database);
        services.AddScoped<IChatClient>(_ => model ?? new RecordingModel());
        services.AddVerifiedSqlAgent();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private sealed class RecordingModel : IChatClient
    {
        public bool Fail { get; init; }
        public string[] Tools { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new HttpRequestException("live model unavailable");
            Tools = options!.Tools!.Select(t => t.Name).ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
