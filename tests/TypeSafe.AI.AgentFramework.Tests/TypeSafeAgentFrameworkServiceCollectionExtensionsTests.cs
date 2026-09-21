using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeAgentFrameworkServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Scoped)]
    public async Task AddTypeSafeFunction_UsesScopedImplementationAndPreservesInvocation(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddScoped<FunctionHandler>();
        using (var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}"""))
        {
            services.AddTypeSafeFunction<FunctionHandler>("query", "Run a query", schema.RootElement,
                static (handler, arguments, cancellationToken) => handler.InvokeAsync(arguments, cancellationToken),
                lifetime: lifetime);
        }
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true
        });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var handler = first.ServiceProvider.GetRequiredService<FunctionHandler>();
        var function = first.ServiceProvider.GetRequiredService<TypeSafeFunction>();
        var another = first.ServiceProvider.GetRequiredService<TypeSafeFunction>();
        Assert.Equal(lifetime == ServiceLifetime.Scoped, ReferenceEquals(function, another));
        Assert.Equal(JsonValueKind.Object, function.JsonSchema.GetProperty("properties").ValueKind);
        using var cancellation = new CancellationTokenSource();
        var arguments = new AIFunctionArguments { ["query"] = "SELECT name" };
        Assert.Same(handler, await function.InvokeAsync(arguments, cancellation.Token));
        Assert.Same(arguments, handler.Arguments);
        Assert.Equal(cancellation.Token, handler.CancellationToken);
        var otherHandler = await second.ServiceProvider.GetRequiredService<TypeSafeFunction>().InvokeAsync(arguments);
        Assert.NotSame(handler, otherHandler);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => function.InvokeAsync(arguments, cancellation.Token).AsTask());
        Assert.Equal(1, handler.Calls);
        first.Dispose();
        Assert.True(handler.Disposed);
        Assert.False(Assert.IsType<FunctionHandler>(otherHandler).Disposed);
    }

    [Fact]
    public void AddTypeSafeFunction_DefaultLifetimeIsTransient()
    {
        var services = new ServiceCollection();
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        services.AddScoped<FunctionHandler>();
        services.AddTypeSafeFunction<FunctionHandler>("query", "Run a query", schema.RootElement,
            static (handler, arguments, cancellationToken) => handler.InvokeAsync(arguments, cancellationToken));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.NotSame(scope.ServiceProvider.GetRequiredService<TypeSafeFunction>(),
            scope.ServiceProvider.GetRequiredService<TypeSafeFunction>());
    }

    private sealed class FunctionHandler : IDisposable
    {
        public AIFunctionArguments? Arguments { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask<object?> InvokeAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Arguments = arguments;
            CancellationToken = cancellationToken;
            Calls++;
            return ValueTask.FromResult<object?>(this);
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void AddTypeSafeAgent_RegistersAndResolvesAgent()
    {
        var services = new ServiceCollection();
        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        services.AddSingleton<ITypeSafeClient>(fakeClient);

        services.AddTypeSafeAgent(
            q => q.Noul("is_urgent", "Is urgent?"),
            name: "DIAgent",
            defaultModel: "jev-latest");

        using var provider = services.BuildServiceProvider();
        var agent = provider.GetService<TypeSafeAgent>();

        Assert.NotNull(agent);
        Assert.Equal("DIAgent", agent.Name);
        Assert.Equal("jev-latest", agent.DefaultModel);
        Assert.NotNull(agent.DefaultQuestions);
        Assert.True(agent.DefaultQuestions.ContainsKey("is_urgent"));
    }

    [Fact]
    public void AddKeyedTypeSafeAgent_RegistersAndResolvesKeyedAgent()
    {
        var services = new ServiceCollection();
        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        services.AddSingleton<ITypeSafeClient>(fakeClient);

        services.AddKeyedTypeSafeAgent("triage", q => q.Noul("q1", "Question 1"), name: "TriageAgent");

        using var provider = services.BuildServiceProvider();
        var agent = provider.GetKeyedService<TypeSafeAgent>("triage");

        Assert.NotNull(agent);
        Assert.Equal("TriageAgent", agent.Name);
    }

    [Fact]
    public void AddTypeSafeAIContextProvider_RegistersAndResolvesProvider()
    {
        var services = new ServiceCollection();
        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        services.AddSingleton<ITypeSafeClient>(fakeClient);

        services.AddTypeSafeAIContextProvider(
            q => q.Noul("is_urgent", "Is urgent?"),
            mode: TypeSafeContextInjectionMode.All,
            model: "jev-latest");

        using var provider = services.BuildServiceProvider();
        var contextProvider = provider.GetService<TypeSafeAIContextProvider>();

        Assert.NotNull(contextProvider);
        Assert.Equal(TypeSafeContextInjectionMode.All, contextProvider.Mode);
    }
}
