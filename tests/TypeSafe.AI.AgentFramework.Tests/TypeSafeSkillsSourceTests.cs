using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeSkillsSourceTests
{
    private sealed class ContextAgent : AIAgent
    {
        public override string Name => "ContextAgent";

        public static void SetContext(AgentRunContext? context) => CurrentRunContext = context;

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

    private sealed class TestSkill : AgentSkill
    {
        public TestSkill(string name, string description)
        {
            Frontmatter = new AgentSkillFrontmatter(name, description);
        }

        public override AgentSkillFrontmatter Frontmatter { get; }

        public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult($"# {Frontmatter.Name}\n{Frontmatter.Description}");
    }

    private static (ContextAgent Agent, AgentSkillsSourceContext Context) CreateContext()
    {
        var agent = new ContextAgent();
        var context = new AgentSkillsSourceContext(agent, session: null);
        return (agent, context);
    }

    [Fact]
    public async Task GetSkillsAsync_WhenSkillsCountBelowTopK_StillRequiresContext()
    {
        var (agent, context) = CreateContext();
        var skills = new List<AgentSkill>
        {
            new TestSkill("git-commit", "Creates a git commit."),
            new TestSkill("code-analyzer", "Analyzes code quality.")
        };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        var innerSource = new AgentInMemorySkillsSource(skills);

        var skillsSource = new TypeSafeSkillsSource(innerSource, fakeClient, new TypeSafeSkillsOptions
        {
            TopK = 5
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => skillsSource.GetSkillsAsync(context));
        Assert.Empty(fakeClient.Invocations);
    }

    [Fact]
    public async Task GetSkillsAsync_RanksSkillsByProbabilityAndReturnsTopK()
    {
        var (agent, context) = CreateContext();
        var skills = new List<AgentSkill>
        {
            new TestSkill("git-commit", "Creates a git commit."),
            new TestSkill("code-analyzer", "Analyzes code quality."),
            new TestSkill("database-query", "Queries SQL databases."),
            new TestSkill("email-sender", "Sends transactional emails.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["code-analyzer"] = 0.80,
                ["git-commit"] = 0.75,
                ["database-query"] = 0.04,
                ["email-sender"] = 0.01
            };

        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse(answers: skills.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Frontmatter.Name] })).ToDictionary(x => x.Id, x => x.Answer));

        var fakeClient = new FakeTypeSafeClient(sampleResponse);
        var innerSource = new AgentInMemorySkillsSource(skills);

        var skillsSource = innerSource.UseTypeSafeShortlisting(fakeClient, topK: 2);

        // Set ambient run context with messages
        var runMessages = new List<ChatMessage> { new(ChatRole.User, "Can you review my C# code for bugs?") };
        ContextAgent.SetContext(new AgentRunContext(agent, null, runMessages, null));

        try
        {
            var result = await skillsSource.GetSkillsAsync(context);

            Assert.Equal(2, result.Count);
            Assert.Equal("code-analyzer", result[0].Frontmatter.Name);
            Assert.Equal("git-commit", result[1].Frontmatter.Name);
            Assert.Single(fakeClient.Invocations);
        }
        finally
        {
            ContextAgent.SetContext(null);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_WithMinimumConfidence_FiltersOutLowProbabilitySkills()
    {
        var (agent, context) = CreateContext();
        var skills = new List<AgentSkill>
        {
            new TestSkill("code-analyzer", "Analyzes code quality."),
            new TestSkill("git-commit", "Creates a git commit."),
            new TestSkill("database-query", "Queries SQL databases.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["code-analyzer"] = 0.85,
                ["git-commit"] = 0.10,
                ["database-query"] = 0.05
            };

        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse(answers: skills.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Frontmatter.Name] })).ToDictionary(x => x.Id, x => x.Answer));

        var fakeClient = new FakeTypeSafeClient(sampleResponse);
        var innerSource = new AgentInMemorySkillsSource(skills);

        var skillsSource = innerSource.UseTypeSafeShortlisting(
            fakeClient,
            topK: 5,
            minimumRelevanceProbability: 0.50);

        var runMessages = new List<ChatMessage> { new(ChatRole.User, "Find syntax errors in my code.") };
        ContextAgent.SetContext(new AgentRunContext(agent, null, runMessages, null));

        try
        {
            var result = await skillsSource.GetSkillsAsync(context);

            Assert.Single(result);
            Assert.Equal("code-analyzer", result[0].Frontmatter.Name);
        }
        finally
        {
            ContextAgent.SetContext(null);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_WhenNoAmbientMessages_AppliesFallbackModes()
    {
        var (_, context) = CreateContext();
        var skills = new List<AgentSkill>
        {
            new TestSkill("skill-a", "Skill A description."),
            new TestSkill("skill-b", "Skill B description."),
            new TestSkill("skill-c", "Skill C description.")
        };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        ContextAgent.SetContext(null);

        // FallbackMode = IncludeAll (default)
        var sourceIncludeAll = new TypeSafeSkillsSource(
            new AgentInMemorySkillsSource(skills),
            fakeClient,
            new TypeSafeSkillsOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.IncludeAll });

        var allResult = await sourceIncludeAll.GetSkillsAsync(context);
        Assert.Equal(3, allResult.Count);

        // FallbackMode = IncludeNone
        var sourceIncludeNone = new TypeSafeSkillsSource(
            new AgentInMemorySkillsSource(skills),
            fakeClient,
            new TypeSafeSkillsOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.IncludeNone });

        var noneResult = await sourceIncludeNone.GetSkillsAsync(context);
        Assert.Empty(noneResult);

        // FallbackMode = Throw
        var sourceThrow = new TypeSafeSkillsSource(
            new AgentInMemorySkillsSource(skills),
            fakeClient,
            new TypeSafeSkillsOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.Throw });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sourceThrow.GetSkillsAsync(context));
    }

    [Fact]
    public async Task GetSkillsAsync_WhenClientThrows_AppliesFallbackModesCorrectly()
    {
        var (agent, context) = CreateContext();
        var skills = new List<AgentSkill>
        {
            new TestSkill("skill-a", "Skill A description."),
            new TestSkill("skill-b", "Skill B description.")
        };

        var failingClient = new FakeTypeSafeClient((_, _, _) => throw new HttpRequestException("Network failure"));
        var runMessages = new List<ChatMessage> { new(ChatRole.User, "User query") };
        ContextAgent.SetContext(new AgentRunContext(agent, null, runMessages, null));

        try
        {
            // IncludeAll returns original skills on error
            var sourceIncludeAll = new TypeSafeSkillsSource(
                new AgentInMemorySkillsSource(skills),
                failingClient,
                new TypeSafeSkillsOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.IncludeAll });

            var result = await sourceIncludeAll.GetSkillsAsync(context);
            Assert.Equal(2, result.Count);

            // Throw propagates exception
            var sourceThrow = new TypeSafeSkillsSource(
                new AgentInMemorySkillsSource(skills),
                failingClient,
                new TypeSafeSkillsOptions { TopK = 1, FallbackMode = TypeSafeRelevanceFallbackMode.Throw });

            await Assert.ThrowsAsync<HttpRequestException>(() => sourceThrow.GetSkillsAsync(context));
        }
        finally
        {
            ContextAgent.SetContext(null);
        }
    }

    [Fact]
    public async Task GetSkillsAsync_ComposesWithAgentSkillsProvider()
    {
        var (agent, context) = CreateContext();
        var skills = new List<AgentSkill>
        {
            new TestSkill("cloud-deploy", "Deploys microservices to Kubernetes."),
            new TestSkill("music-player", "Plays music playlists.")
        };

        var probabilities = new Dictionary<string, double>
        {
                ["cloud-deploy"] = 0.99,
                ["music-player"] = 0.01
            };

        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse(answers: skills.Select((t, i) => (Id: $"r{i:D8}", Answer: (TypeSafeAnswer)new NoulAnswer { Noul = probabilities[t.Frontmatter.Name] })).ToDictionary(x => x.Id, x => x.Answer)));

        var innerSource = new AgentInMemorySkillsSource(skills);
        var typeSafeSource = innerSource.UseTypeSafeShortlisting(fakeClient, topK: 1);

        using var skillsProvider = new AgentSkillsProvider(typeSafeSource);

        var runMessages = new List<ChatMessage> { new(ChatRole.User, "Deploy this image to the cluster") };
        ContextAgent.SetContext(new AgentRunContext(agent, null, runMessages, null));

        try
        {
            // InvokingContext for AIContextProvider
            var invokingContext = new AIContextProvider.InvokingContext(agent: agent, session: null, aiContext: new AIContext { Messages = runMessages });
            var aiContextMethod = typeof(AgentSkillsProvider).GetMethod(
                "ProvideAIContextAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var task = (ValueTask<AIContext>)aiContextMethod!.Invoke(skillsProvider, [invokingContext, CancellationToken.None])!;
            var aiContext = await task;

            Assert.NotNull(aiContext.Instructions);
            Assert.Contains("cloud-deploy", aiContext.Instructions);
            Assert.DoesNotContain("music-player", aiContext.Instructions);
        }
        finally
        {
            ContextAgent.SetContext(null);
        }
    }
}

