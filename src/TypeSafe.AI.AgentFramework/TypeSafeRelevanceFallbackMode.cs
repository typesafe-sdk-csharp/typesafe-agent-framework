namespace TypeSafe.AI.AgentFramework;

/// <summary>Behavior when tool or skill relevance evaluation is unavailable or invalid.</summary>
public enum TypeSafeRelevanceFallbackMode
{
    /// <summary>Return all authorized candidates.</summary>
    IncludeAll = 0,
    /// <summary>Return no ranked candidates. Explicitly mandatory tools are still included.</summary>
    IncludeNone = 1,
    /// <summary>Propagate the evaluation failure. This is the default.</summary>
    Throw = 2
}
