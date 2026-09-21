using Microsoft.Extensions.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Configuration options for dynamic tool shortlisting and selection.
/// </summary>
public class TypeSafeToolSelectionOptions
{
    private int _topK = 5;
    private double? _minimumRelevanceProbability = 0.50;
    private double? _dropOffRatio;

    /// <summary>
    /// Gets or sets the maximum number of ranked nonmandatory tools. Mandatory tools are appended.
    /// Default is 5. Must be greater than 0.
    /// </summary>
    public int TopK
    {
        get => _topK;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "TopK must be greater than 0.");
            }

            _topK = value;
        }
    }

    /// <summary>
    /// Gets or sets an minimum independent relevance probability (default 0.50) required for a tool to be included.
    /// Values must be between 0.0 and 1.0. Tools below this threshold are omitted unless included in <see cref="AlwaysIncludeToolNames"/>.
    /// </summary>
    public double? MinimumRelevanceProbability
    {
        get => _minimumRelevanceProbability;
        set
        {
            if (value.HasValue && (!double.IsFinite(value.Value) || value is < 0.0 or > 1.0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MinimumRelevanceProbability must be between 0.0 and 1.0.");
            }

            _minimumRelevanceProbability = value;
        }
    }

    /// <summary>
    /// Gets or sets an optional relative score drop-off ratio (between 0.0 and 1.0).
    /// When set, candidates whose probability falls below (topCandidateProbability * DropOffRatio)
    /// are omitted, preventing low-relevance tools from being shortlisted when a dominant match exists.
    /// </summary>
    public double? DropOffRatio
    {
        get => _dropOffRatio;
        set
        {
            if (value.HasValue && (!double.IsFinite(value.Value) || value is < 0.0 or > 1.0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DropOffRatio must be between 0.0 and 1.0.");
            }

            _dropOffRatio = value;
        }
    }

    /// <summary>
    /// Gets or sets whether to parse parameter metadata (names, types, parameter descriptions) from
    /// <see cref="AIFunction.JsonSchema"/> into the semantic criteria passed to TypeSafe System One.
    /// Default is true.
    /// </summary>
    public bool IncludeStructuredMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets the fallback behavior when semantic evaluation fails or no turn context is present.
    /// Default is <see cref="TypeSafeRelevanceFallbackMode.Throw"/>.
    /// </summary>
    public TypeSafeRelevanceFallbackMode FallbackMode { get; set; } = TypeSafeRelevanceFallbackMode.Throw;

    /// <summary>
    /// Gets or sets the optional model override to use for decision evaluation.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets optional custom instructions for the tool classification question.
    /// Defaults to "Select the most relevant tool(s) for the user's request."
    /// </summary>
    public TypeSafeContent? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the content extractor used to synthesize state from request messages.
    /// Defaults to <see cref="TypeSafeContentConverter.DefaultExtractor"/>.
    /// </summary>
    public ITypeSafeContentExtractor? ContentExtractor { get; set; }

    /// <summary>
    /// Gets a set of tool names that must always be included in the shortlisted tools,
    /// bypassing probability ranking and thresholds (e.g. handoff tools, memory tools, or critical safety tools).
    /// </summary>
    public ISet<string> AlwaysIncludeToolNames { get; } = new HashSet<string>(StringComparer.Ordinal);
    internal TypeSafeToolSelectionOptions Snapshot()
    {
        if (!Enum.IsDefined(FallbackMode)) throw new ArgumentOutOfRangeException(nameof(FallbackMode));
        var copy = new TypeSafeToolSelectionOptions { TopK = TopK, MinimumRelevanceProbability = MinimumRelevanceProbability,
            DropOffRatio = DropOffRatio, IncludeStructuredMetadata = IncludeStructuredMetadata, FallbackMode = FallbackMode,
            Model = Model, Instructions = Instructions, ContentExtractor = ContentExtractor };
        copy.AlwaysIncludeToolNames.UnionWith(AlwaysIncludeToolNames);
        return copy;
    }
}
