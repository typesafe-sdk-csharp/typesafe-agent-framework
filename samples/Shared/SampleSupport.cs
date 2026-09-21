using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Samples;

// Fixtures exercise plumbing, not model quality or latency.
internal sealed class DemoTypeSafeClient(
    Func<TypeSafeContent, IReadOnlyDictionary<string, TypeSafeQuestion>, Dictionary<string, TypeSafeAnswer>> evaluate) : ITypeSafeClient
{
    public Task<SystemOneResponse> SystemOneAsync(SystemOneRequest request, CancellationToken cancellationToken = default)
        => SystemOneAsync(request.State, request.Questions, request.Model, cancellationToken);
    public Task<SystemOneResponse> SystemOneAsync(TypeSafeContent state,
        IReadOnlyDictionary<string, TypeSafeQuestion> questions, string? model = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SystemOneResponse(model ?? "offline-fixture", evaluate(state, questions),
            new TypeSafeUsage(), "offline-request"));
    }
}

internal sealed class DemoModelClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> respond) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(messages.ToArray(), options));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

internal static class SampleSupport
{
    public static bool LiveMode(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--offline" or "--live")))
            throw new ArgumentException("Usage: --offline (default) or --live.");
        return args is ["--live"];
    }
    public static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} for live mode.");
    public static ITypeSafeClient RelevanceClient(IReadOnlyDictionary<string, double> scores) =>
        new DemoTypeSafeClient((_, questions) => questions.ToDictionary(p => p.Key, p =>
        {
            var description = ((Noul)p.Value).Instructions!.ToJsonNode().GetValue<string>();
            var name = description.Split('\n').Single(line => line.StartsWith("Name: ", StringComparison.Ordinal))[6..];
            return (TypeSafeAnswer)new NoulAnswer { Noul = scores.GetValueOrDefault(name, 0.01) };
        }));
    public static TypeSafeFunction Tool(string name, string description) => new(name, description,
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
        (_, _) => ValueTask.FromResult<object?>($"{name}: completed"));
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
