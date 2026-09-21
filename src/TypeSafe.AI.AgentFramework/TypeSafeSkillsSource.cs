using System.Collections.ObjectModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// A skill source decorator (<see cref="DelegatingAgentSkillsSource"/>) that shortlists and prunes
/// discovered <see cref="AgentSkill"/> catalogs using TypeSafe AI probabilistic classification
/// before progressive disclosure instructions are generated for the agent.
/// </summary>
public class TypeSafeSkillsSource : DelegatingAgentSkillsSource
{
    private readonly bool _ownsInnerSource;
    private int _disposed;
    private readonly ITypeSafeClient _client;
    private readonly TypeSafeSkillsOptions _options;
    private readonly ITypeSafeContentExtractor _contentExtractor;
    private readonly ILogger<TypeSafeSkillsSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSafeSkillsSource"/> class.
    /// </summary>
    /// <param name="innerSource">The underlying skill source providing raw candidate skills.</param>
    /// <param name="client">The TypeSafe client used for semantic inference.</param>
    /// <param name="options">Configuration options controlling top-K, threshold, and fallback mode.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public TypeSafeSkillsSource(
        AgentSkillsSource innerSource,
        ITypeSafeClient client,
        TypeSafeSkillsOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        bool ownsInnerSource = true)
        : base(innerSource)
    {
        ArgumentNullException.ThrowIfNull(innerSource);
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _ownsInnerSource = ownsInnerSource;
        var supplied = options ?? new TypeSafeSkillsOptions();
        if (!Enum.IsDefined(supplied.FallbackMode)) throw new ArgumentOutOfRangeException(nameof(options));
        _options = new TypeSafeSkillsOptions { TopK = supplied.TopK, MinimumRelevanceProbability = supplied.MinimumRelevanceProbability,
            FallbackMode = supplied.FallbackMode, Model = supplied.Model, Instructions = supplied.Instructions, ContentExtractor = supplied.ContentExtractor };
        _contentExtractor = _options.ContentExtractor ?? TypeSafeContentConverter.DefaultExtractor;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TypeSafeSkillsSource>();
    }

    /// <summary>
    /// Gets the configuration options for this source.
    /// </summary>
    public TypeSafeSkillsOptions Options => new() { TopK = _options.TopK, MinimumRelevanceProbability = _options.MinimumRelevanceProbability,
        FallbackMode = _options.FallbackMode, Model = _options.Model, Instructions = _options.Instructions, ContentExtractor = _options.ContentExtractor };

    /// <inheritdoc />
    public override async Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var allSkills = await InnerSource.GetSkillsAsync(context, cancellationToken).ConfigureAwait(false);
        if (allSkills is not { Count: > 0 })
        {
            return allSkills ?? [];
        }

        var messages = AIAgent.CurrentRunContext?.RequestMessages;
        var state = messages is { Count: > 0 } ? _contentExtractor.Extract(messages) : TypeSafeContent.FromString("");
        return await RelevancePolicy.SelectAsync(_client, allSkills, state,
            new TypeSafeToolSelectionOptions { TopK = _options.TopK, MinimumRelevanceProbability = _options.MinimumRelevanceProbability,
                FallbackMode = _options.FallbackMode, Model = _options.Model, Instructions = _options.Instructions },
            s => s.Frontmatter.Name, s => s.Frontmatter.Description, cancellationToken).ConfigureAwait(false);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsInnerSource) InnerSource.Dispose();
    }
}
