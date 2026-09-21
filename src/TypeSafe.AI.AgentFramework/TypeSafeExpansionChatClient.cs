using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>Installs the owned function invocation layer required for dynamic expansion.</summary>
public static class TypeSafeExpansionExtensions
{
    /// <summary>Clones options and tools per run and invokes discovered functions within that run.</summary>
    public static ChatClientBuilder UseTypeSafeToolExpansion(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new TypeSafeExpansionChatClient(inner));
    }
}

internal sealed class TypeSafeExpansionChatClient : DelegatingChatClient
{
    private static readonly AsyncLocal<RunScope?> Current = new();
    private sealed class RunScope { public object Gate { get; } = new(); }

    public TypeSafeExpansionChatClient(IChatClient inner) : base(new FunctionInvokingChatClient(inner)
    {
        AllowConcurrentInvocation = false
    }) { }

    private static ChatOptions CloneOptions(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        clone.Tools = options?.Tools?.ToList() ?? [];
        return clone;
    }

    internal static void RequireScope()
    {
        if (Current.Value is null || FunctionInvokingChatClient.CurrentContext?.Options is null)
            throw new InvalidOperationException("Dynamic expansion requires UseTypeSafeToolExpansion. Use CreateToolSelectionTool for recommendations only.");
    }

    internal static IList<string> Merge(IList<AITool> additions, CancellationToken cancellationToken)
    {
        RequireScope();
        var scope = Current.Value!;
        lock (scope.Gate)
        {
            var options = FunctionInvokingChatClient.CurrentContext!.Options!;
            var tools = options.Tools?.ToList() ?? [];
            var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);
            var added = new List<string>();
            foreach (var tool in additions)
                if (names.Add(tool.Name)) { tools.Add(tool); added.Add(tool.Name); }
            cancellationToken.ThrowIfCancellationRequested();
            options.Tools = tools;
            return added;
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Current.Value;
        Current.Value = new RunScope();
        try { return await base.GetResponseAsync(messages, CloneOptions(options), cancellationToken).ConfigureAwait(false); }
        finally { Current.Value = previous; }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = new RunScope();
        var previous = Current.Value;
        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        Current.Value = scope;
        try { enumerator = base.GetStreamingResponseAsync(messages, CloneOptions(options), cancellationToken).GetAsyncEnumerator(cancellationToken); }
        finally { Current.Value = previous; }
        try
        {
            while (await MoveNextAsync(enumerator, scope).ConfigureAwait(false))
                yield return enumerator.Current;
        }
        finally
        {
            previous = Current.Value;
            Current.Value = scope;
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            finally { Current.Value = previous; }
        }
    }

    private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator, RunScope scope)
    {
        var previous = Current.Value;
        Current.Value = scope;
        try { return await enumerator.MoveNextAsync().ConfigureAwait(false); }
        finally { Current.Value = previous; }
    }
}
