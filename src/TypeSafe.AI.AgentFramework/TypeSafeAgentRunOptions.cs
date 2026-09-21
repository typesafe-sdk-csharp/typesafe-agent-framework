using Microsoft.Agents.AI;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Execution options for configuring or overriding <see cref="TypeSafeAgent"/> invocations.
/// </summary>
public class TypeSafeAgentRunOptions : AgentRunOptions
{
    /// <summary>Initializes a new instance of <see cref="TypeSafeAgentRunOptions"/>.</summary>
    public TypeSafeAgentRunOptions()
    {
    }

    /// <summary>Initializes a new instance of <see cref="TypeSafeAgentRunOptions"/> by copying from existing options.</summary>
    public TypeSafeAgentRunOptions(TypeSafeAgentRunOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Questions = options.Questions;
        Model = options.Model;
        ContentExtractor = options.ContentExtractor;
        ResultFormatter = options.ResultFormatter;
    }

    /// <inheritdoc />
    public override AgentRunOptions Clone() => new TypeSafeAgentRunOptions(this);

    /// <summary>Dynamic questions to evaluate for this run. If null, the agent's default questions are used.</summary>
    public IReadOnlyDictionary<string, TypeSafeQuestion>? Questions { get; set; }

    /// <summary>Model override for this run. If null, the agent's default model is used.</summary>
    public string? Model { get; set; }

    /// <summary>Custom extractor to extract TypeSafeContent state from incoming messages. If null, agent's extractor is used.</summary>
    public ITypeSafeContentExtractor? ContentExtractor { get; set; }

    /// <summary>Custom string formatter for the response text. If null, agent's formatter is used.</summary>
    public Func<SystemOneResponse, string>? ResultFormatter { get; set; }
}
