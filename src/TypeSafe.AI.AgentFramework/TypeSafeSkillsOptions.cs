using Microsoft.Agents.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Configuration options for <see cref="TypeSafeSkillsSource"/> semantic skill shortlisting.
/// </summary>
public class TypeSafeSkillsOptions
{
    private int _topK = 5;
    private double? _minimumRelevanceProbability = 0.50;

    /// <summary>
    /// Gets or sets the maximum number of skills to retain and advertise for the current turn.
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
    /// Gets or sets an minimum independent relevance probability (default 0.50) required for a skill to be included.
    /// Values must be between 0.0 and 1.0. Skills below this threshold are omitted.
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
    /// Gets or sets the fallback behavior when semantic evaluation fails or no turn context is present.
    /// Default is <see cref="TypeSafeRelevanceFallbackMode.Throw"/>.
    /// </summary>
    public TypeSafeRelevanceFallbackMode FallbackMode { get; set; } = TypeSafeRelevanceFallbackMode.Throw;

    /// <summary>
    /// Gets or sets the optional model override to use for decision evaluation.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets optional custom instructions for the classification question.
    /// Defaults to "Select the most relevant agent skills for the user's request."
    /// </summary>
    public TypeSafeContent? Instructions { get; set; }

    /// <summary>
    /// Gets or sets the content extractor used to synthesize state from request messages.
    /// Defaults to <see cref="TypeSafeContentConverter.DefaultExtractor"/>.
    /// </summary>
    public ITypeSafeContentExtractor? ContentExtractor { get; set; }
}
