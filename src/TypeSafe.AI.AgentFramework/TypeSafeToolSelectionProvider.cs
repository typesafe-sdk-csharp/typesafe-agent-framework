using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// An <see cref="AIContextProvider"/> that dynamically shortlists the agent's candidate tools
/// (<see cref="AIContext.Tools"/>) using TypeSafe AI probabilistic inference before
/// downstream LLM invocation.
/// </summary>
public class TypeSafeToolSelectionProvider : AIContextProvider
{
    private readonly ITypeSafeClient _client;
    private readonly TypeSafeToolSelectionOptions _options;
    private readonly ILogger<TypeSafeToolSelectionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeToolSelectionProvider"/> class.
    /// </summary>
    /// <param name="client">The TypeSafe client used for inference.</param>
    /// <param name="options">Configuration options controlling top-K, threshold, and fallback mode.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="provideInputMessageFilter">Optional selection input filter; defaults to external messages.</param>
    public TypeSafeToolSelectionProvider(
        ITypeSafeClient client,
        TypeSafeToolSelectionOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? provideInputMessageFilter = null)
        : base(provideInputMessageFilter: provideInputMessageFilter)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = (options ?? new TypeSafeToolSelectionOptions()).Snapshot();
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TypeSafeToolSelectionProvider>();
    }

    /// <summary>
    /// Gets the configuration options for tool shortlisting.
    /// </summary>
    public TypeSafeToolSelectionOptions Options => _options.Snapshot();

    /// <inheritdoc />
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var inputContext = context.AIContext;
        var candidateTools = inputContext.Tools?.ToList();

        // If there are no candidate tools, passthrough
        if (candidateTools is not { Count: > 0 })
        {
            return await base.InvokingCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // Extract messages for the current turn
        var messages = inputContext.Messages;
        messages ??= AIAgent.CurrentRunContext?.RequestMessages;
        messages ??= [new ChatMessage(ChatRole.User, "")];
        messages = ProvideInputMessageFilter(messages);

        var shortlistedTools = await TypeSafeTools.ShortlistToolsAsync(_client, candidateTools, messages, _options, cancellationToken).ConfigureAwait(false);
        return new AIContext { Instructions = inputContext.Instructions, Messages = inputContext.Messages, Tools = shortlistedTools };
    }
}


