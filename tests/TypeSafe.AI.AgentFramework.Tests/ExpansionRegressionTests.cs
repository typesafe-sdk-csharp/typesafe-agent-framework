using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public sealed class ExpansionRegressionTests
{
    private static ChatOptions Options(string name, Func<CancellationToken, ValueTask<object?>>? invoke = null)
    {
        var client = new FakeTypeSafeClient((_, questions, _) => FakeTypeSafeClient.CreateSampleResponse(
            answers: questions.Keys.ToDictionary(k => k, _ => (TypeSafeAnswer)new NoulAnswer { Noul = 1 })));
        var tool = new TypeSafeFunction(name, name, JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone(),
            (_, ct) => invoke is null ? ValueTask.FromResult<object?>("executed") : invoke(ct));
        return new ChatOptions { Tools = Array.AsReadOnly<AITool>([TypeSafeTools.CreateDynamicToolExpansionTool(client, [tool])]) };
    }

    [Fact]
    public async Task RepeatedConcurrentStreaming_DiscoveriesStayInTheirOwnRun()
    {
        using var loop = new ChatClientBuilder(new ExpansionModel()).UseTypeSafeToolExpansion().Build();
        async Task Run(string name)
        {
            var options = Options(name);
            for (var i = 0; i < 3; i++)
            {
                var output = "";
                await foreach (var update in loop.GetStreamingResponseAsync([new(ChatRole.User, name)], options))
                {
                    await Task.Yield();
                    output += update.Text;
                }
                Assert.Contains("executed", output);
                Assert.Single(options.Tools!);
            }
        }
        await Task.WhenAll(Run("first"), Run("second"));
        Assert.Throws<InvalidOperationException>(TypeSafeExpansionChatClient.RequireScope);
    }

    [Fact]
    public async Task EarlyDisposalAndCancellation_AllowFreshRuns()
    {
        using var loop = new ChatClientBuilder(new ExpansionModel()).UseTypeSafeToolExpansion().Build();
        var options = Options("first");
        var enumerator = loop.GetStreamingResponseAsync([new(ChatRole.User, "first")], options).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
        using var cancel = new CancellationTokenSource();
        await using (var canceled = loop.GetStreamingResponseAsync([new(ChatRole.User, "first")], options, cancel.Token).GetAsyncEnumerator())
        {
            Assert.True(await canceled.MoveNextAsync());
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { while (await canceled.MoveNextAsync()) { } });
        }
        Assert.Contains("executed", (await loop.GetResponseAsync([new(ChatRole.User, "first")], options)).Text);
        Assert.Single(options.Tools!);
        Assert.Throws<InvalidOperationException>(TypeSafeExpansionChatClient.RequireScope);
    }

    [Fact]
    public async Task NestedInvocation_RestoresOuterScope()
    {
        using var loop = new ChatClientBuilder(new ExpansionModel()).UseTypeSafeToolExpansion().Build();
        var nested = Options("nested");
        var outer = Options("outer", async ct =>
        {
            var result = await loop.GetResponseAsync([new(ChatRole.User, "nested")], nested, ct);
            return result.Text;
        });
        Assert.Contains("executed", (await loop.GetResponseAsync([new(ChatRole.User, "outer")], outer)).Text);
        Assert.Single(outer.Tools!);
        Assert.Single(nested.Tools!);
    }

    private sealed class ExpansionModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            var name = snapshot.First(m => m.Role == ChatRole.User).Text;
            var results = snapshot.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            if (results.Length == 0)
            {
                Assert.Single(options!.Tools!);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("discover", "request_tools", new Dictionary<string, object?> { ["state"] = name })])));
            }
            if (results.Length == 1)
            {
                Assert.Equal(new[] { "request_tools", name }, options!.Tools!.Select(t => t.Name));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("execute", name, new Dictionary<string, object?>())])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, results[^1].Result?.ToString())));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "");
            await Task.Yield();
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

