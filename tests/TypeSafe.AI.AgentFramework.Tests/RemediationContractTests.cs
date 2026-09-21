using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class RemediationContractTests
{
    private static List<AITool> Catalog(int count) => Enumerable.Range(0, count)
        .Select(i => (AITool)AIFunctionFactory.Create(() => "ok", name: $"tool_{i}")).ToList();
    private static FakeTypeSafeClient Relevance(double probability) => new((_, questions, _) =>
        FakeTypeSafeClient.CreateSampleResponse(answers: questions.ToDictionary(q => q.Key,
            q => { Assert.IsType<Noul>(q.Value); return (TypeSafeAnswer)new NoulAnswer { Noul = probability }; })));

    [Theory]
    [InlineData(3)]
    [InlineData(300)]
    public async Task IndependentRelevance_DoesNotDiluteScores(int count)
    {
        var client = Relevance(0.9);
        var tools = Catalog(count);
        var selected = await TypeSafeTools.ShortlistToolsAsync(client, tools, "compound request");
        Assert.Equal(tools.Take(5), selected);
        Assert.Equal(count, Assert.Single(client.Invocations).Questions.Count);
        Assert.Empty(await TypeSafeTools.ShortlistToolsAsync(Relevance(0.1), tools, "unrelated"));
    }

    [Theory]
    [InlineData(TypeSafeRelevanceFallbackMode.IncludeAll)]
    [InlineData(TypeSafeRelevanceFallbackMode.IncludeNone)]
    [InlineData(TypeSafeRelevanceFallbackMode.Throw)]
    public async Task ProtocolFailuresAndBlankState_RespectFallback(TypeSafeRelevanceFallbackMode mode)
    {
        var tools = Catalog(2);
        foreach (var fault in new[] { "missing", "wrong", "nan", "infinity", "negative", "large", "blank", "network" })
        {
            var client = new FakeTypeSafeClient((_, questions, _) =>
            {
                if (fault == "network") throw new TypeSafeConnectionException("offline");
                var answers = new Dictionary<string, TypeSafeAnswer>();
                foreach (var key in questions.Keys)
                    if (fault != "missing") answers[key] = fault == "wrong"
                        ? new UnknownAnswer { Type = "future", Raw = JsonDocument.Parse("{}").RootElement.Clone() }
                        : new NoulAnswer { Noul = fault switch { "nan" => double.NaN, "infinity" => double.PositiveInfinity, "negative" => -0.1, "large" => 1.1, _ => 0.9 } };
                return FakeTypeSafeClient.CreateSampleResponse(answers: answers);
            });
            var options = new TypeSafeToolSelectionOptions { FallbackMode = mode };
            options.AlwaysIncludeToolNames.Add("tool_1");
            Task<IList<AITool>> Run() => TypeSafeTools.ShortlistToolsAsync(client, tools, fault == "blank" ? " " : "request", options);
            if (mode == TypeSafeRelevanceFallbackMode.Throw && fault == "blank") await Assert.ThrowsAsync<InvalidOperationException>(Run);
            else if (mode == TypeSafeRelevanceFallbackMode.Throw) await Assert.ThrowsAnyAsync<TypeSafeException>(Run);
            else Assert.Equal(mode == TypeSafeRelevanceFallbackMode.IncludeAll ? 2 : 1, (await Run()).Count);
        }
    }

    [Theory]
    [InlineData(TypeSafeRelevanceFallbackMode.IncludeAll)]
    [InlineData(TypeSafeRelevanceFallbackMode.IncludeNone)]
    [InlineData(TypeSafeRelevanceFallbackMode.Throw)]
    public async Task CancellationAndProgrammingErrors_NeverBecomeFallback(TypeSafeRelevanceFallbackMode mode)
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeTypeSafeClient((_, questions, _) =>
        {
            cts.Cancel();
            return FakeTypeSafeClient.CreateSampleResponse(answers: questions.Keys.ToDictionary(k => k, _ => (TypeSafeAnswer)new NoulAnswer { Noul = 0.9 }));
        });
        var options = new TypeSafeToolSelectionOptions { FallbackMode = mode };
        var task = TypeSafeTools.ShortlistToolsAsync(client, Catalog(2), "request", options, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TypeSafeTools.ShortlistToolsAsync(client, Catalog(2), "request", options, cts.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => TypeSafeTools.ShortlistToolsAsync(
            new FakeTypeSafeClient((_, _, _) => throw new ArgumentException("bad configuration")), Catalog(2), "request", options));
    }

    [Fact]
    public async Task Configuration_IsValidatedAndMandatoryToolsDoNotConsumeTopK()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypeSafeToolSelectionOptions { MinimumRelevanceProbability = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypeSafeToolSelectionOptions { DropOffRatio = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypeSafeSkillsOptions { MinimumRelevanceProbability = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypeSafeToolSelectionProvider(Relevance(1), new() { FallbackMode = (TypeSafeRelevanceFallbackMode)99 }));
        var tools = Catalog(3);
        await Assert.ThrowsAsync<ArgumentException>(() => TypeSafeTools.ShortlistToolsAsync(Relevance(1), [tools[0], tools[0]], "request", new() { FallbackMode = TypeSafeRelevanceFallbackMode.IncludeAll }));
        var options = new TypeSafeToolSelectionOptions { TopK = 1 };
        options.AlwaysIncludeToolNames.Add("tool_0");
        var provider = TypeSafeTools.CreateSelectionProvider(Relevance(1), options);
        options.TopK = 3;
        options.AlwaysIncludeToolNames.Clear();
        var selected = await TypeSafeTools.ShortlistToolsAsync(Relevance(1), tools, "request", provider.Options);
        Assert.Equal(new[] { "tool_1", "tool_0" }, selected.Select(t => t.Name));
        provider.Options.AlwaysIncludeToolNames.Clear();
        Assert.Contains("tool_0", provider.Options.AlwaysIncludeToolNames);
        _ = new TypeSafeAgent(Relevance(1)); // Consumer overload smoke checks.
        _ = TypeSafeTools.CreateSelectionProvider(Relevance(1));
    }

    [Fact]
    public async Task UnknownAnswersAndApplicationState_RoundTrip()
    {
        var raw = JsonDocument.Parse("""{"type":"future","nested":{"value":[1,true,"x"]}}""").RootElement.Clone();
        var client = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: new()
        {
            ["future"] = new UnknownAnswer { Type = "future", Raw = raw }
        }));
        var agent = new TypeSafeAgent(client, q => q.Noul("q", "question"));
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue("application", "retained");
        await agent.RunAsync("request", session);
        var restored = (TypeSafeAgentSession)await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
        Assert.Equal("retained", restored.StateBag.GetValue<string>("application"));
        Assert.Equal(raw.GetRawText(), Assert.IsType<UnknownAnswer>(restored.LastResponse!.Answers["future"]).Raw.GetRawText());
        var classifier = new TypeSafeAgent(client, Questions.Build(q => q.Noul("q", "question")),
            contentExtractor: TypeSafeContentConverter.FromDelegate(_ => "state"), resultFormatter: _ => "result");
        var classifierSession = await classifier.CreateSessionAsync();
        classifierSession.StateBag.SetValue("application", "retained");
        var classifierRestored = await classifier.DeserializeSessionAsync(await classifier.SerializeSessionAsync(classifierSession));
        Assert.Equal("retained", classifierRestored.StateBag.GetValue<string>("application"));
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void DependencyInjection_AliasesShareCanonicalInstance(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeSafeClient>(Relevance(1));
        services.AddTypeSafeToolSelectionProvider(lifetime: lifetime);
        services.AddSingleton<CountingSkills>();
        services.AddTypeSafeSkillsSource<CountingSkills>(lifetime: lifetime);
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<TypeSafeToolSelectionProvider>(), scope.ServiceProvider.GetRequiredService<AIContextProvider>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<TypeSafeSkillsSource>(), scope.ServiceProvider.GetRequiredService<AgentSkillsSource>());
        var inner = provider.GetRequiredService<CountingSkills>();
        scope.Dispose();
        provider.Dispose();
        Assert.Equal(1, inner.DisposeCount);
    }

    private sealed class CountingSkills : AgentSkillsSource
    {
        public int DisposeCount;
        public override Task<IList<AgentSkill>> GetSkillsAsync(AgentSkillsSourceContext context, CancellationToken cancellationToken = default) => Task.FromResult<IList<AgentSkill>>([]);
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; }
    }

    [Fact]
    public async Task Decisions_EmitSingleOutcomeWithoutMessageBodies()
    {
        var stopped = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "TypeSafe.AI.AgentFramework.Selection",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("diagnostic-test").Start();
        await TypeSafeTools.ShortlistToolsAsync(Relevance(0.9), Catalog(3), "secret message");
        var activity = Assert.Single(stopped, a => a.ParentId == parent.Id);
        Assert.Equal("selected", activity.GetTagItem("outcome"));
        Assert.Equal(3, activity.GetTagItem("selected.count"));
        Assert.DoesNotContain(activity.TagObjects, t => Equals(t.Value, "secret message"));
    }
}


