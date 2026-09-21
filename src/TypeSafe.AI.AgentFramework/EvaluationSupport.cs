using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TypeSafe.AI.AgentFramework;

internal static class EvaluationSupport
{
    internal static IReadOnlyDictionary<string, TypeSafeQuestion> Snapshot(
        IReadOnlyDictionary<string, TypeSafeQuestion> questions) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(questions,
            AgentFrameworkJsonContext.Default.IReadOnlyDictionaryStringTypeSafeQuestion),
            AgentFrameworkJsonContext.Default.IReadOnlyDictionaryStringTypeSafeQuestion)!;

    internal static AdditionalPropertiesDictionary Metadata(SystemOneResponse response) => new()
    {
        ["typesafe.model"] = response.Model,
        ["typesafe.response"] = response
    };

    internal static UsageDetails Usage(SystemOneResponse response) => new()
    {
        InputTokenCount = response.Usage.InputTokens,
        OutputTokenCount = response.Usage.OutputTokens,
        TotalTokenCount = response.Usage.InputTokens is int input && response.Usage.OutputTokens is int output
            ? input + output : null
    };

    internal static string Format(SystemOneResponse response, Func<SystemOneResponse, string>? formatter) =>
        formatter is null ? TypeSafeEvaluationContent.FormatSummary(response)
            : formatter(response) ?? throw new InvalidOperationException("The result formatter returned null.");
}

/// <summary>Shared fail-closed evaluation for both guardrail attachment points.</summary>
internal sealed class ScreeningEvaluator(ITypeSafeClient client,
    IReadOnlyDictionary<string, TypeSafeQuestion> questions, string? model)
{
    internal async Task<SystemOneResponse?> EvaluateAsync(IEnumerable<ChatMessage> messages,
        ITypeSafeContentExtractor extractor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = extractor.Extract(messages);
        if (TypeSafeContentConverter.IsBlankState(state)) return null;
        var response = await client.SystemOneAsync(state, questions, model, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ScreeningAnswers.Validate(response, questions);
        return response;
    }
}
