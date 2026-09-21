using System.Text.Json;
using System.Text.Json.Serialization;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Source-generated JSON context for the AgentFramework adapter types, envelopes, and responses.
/// Ensures complete NativeAOT compatibility and trimming safety with zero reflection.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string[]))]

[JsonSerializable(typeof(TypeSafeAgentSessionEnvelope))]
[JsonSerializable(typeof(RouterSessionEnvelope))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(SystemOneResponse))]
[JsonSerializable(typeof(SystemOneRequest))]
[JsonSerializable(typeof(TypeSafeContent))]
[JsonSerializable(typeof(TypeSafeUsage))]
[JsonSerializable(typeof(TypeSafeAnswer))]
[JsonSerializable(typeof(NoulAnswer))]
[JsonSerializable(typeof(ChoiceAnswer))]
[JsonSerializable(typeof(ScoreAnswer))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, TypeSafeQuestion>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, double>))]
internal partial class AgentFrameworkJsonContext : JsonSerializerContext;

