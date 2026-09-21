using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// Factory for creating Microsoft Agent Framework tools (<see cref="AIFunction"/> / <see cref="AITool"/>)
/// backed by TypeSafe System One evaluations.
/// </summary>
public static class TypeSafeTools
{
    private static readonly JsonElement StateEvaluationSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "state": {
                    "type": ["string", "object", "array"],
                    "description": "The natural-language text or structured JSON data to evaluate."
                }
            },
            "required": ["state"]
        }
        """).RootElement.Clone();

    /// <summary>
    /// Creates an <see cref="AIFunction"/> tool that evaluates content against a pre-configured set of questions.
    /// </summary>
    public static TypeSafeFunction CreateEvaluationTool(
        ITypeSafeClient client,
        IReadOnlyDictionary<string, TypeSafeQuestion> questions,
        string name = "typesafe_evaluate",
        string description = "Evaluates content against pre-configured verification questions.",
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (questions.Count == 0)
        {
            throw new ArgumentException("At least one question is required for the evaluation tool.", nameof(questions));
        }

        // Snapshot questions
        var questionSnapshot = EvaluationSupport.Snapshot(questions);

        return new TypeSafeFunction(
            name,
            description,
            StateEvaluationSchema,
            async (args, ct) =>
            {
                var state = ExtractState(args);
                var response = await client.SystemOneAsync(state, questionSnapshot, model, ct).ConfigureAwait(false);
                return response;
            });
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> tool using a question builder lambda.
    /// </summary>
    public static TypeSafeFunction CreateEvaluationTool(
        ITypeSafeClient client,
        Func<QuestionBuilder, QuestionBuilder> questionsBuilder,
        string name = "typesafe_evaluate",
        string description = "Evaluates content against pre-configured verification questions.",
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(questionsBuilder);
        return CreateEvaluationTool(client, Questions.Build(questionsBuilder), name, description, model);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> tool for answering a single yes/no verification question.
    /// </summary>
    public static TypeSafeFunction CreateNoulTool(
        ITypeSafeClient client,
        string id,
        TypeSafeContent? instructions,
        string name = "verify_condition",
        string? description = null,
        TypeSafeContent? whenTrue = null,
        TypeSafeContent? whenFalse = null,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        NoulCriteria? criteria = whenTrue is null && whenFalse is null
            ? null
            : new NoulCriteria { WhenTrue = whenTrue, WhenFalse = whenFalse };

        var questions = new ReadOnlyDictionary<string, TypeSafeQuestion>(new Dictionary<string, TypeSafeQuestion>
        {
            [id] = new Noul { Instructions = instructions ?? id, Criteria = criteria }
        });

        var effectiveDescription = description ?? $"Evaluates yes/no condition '{id}' on the provided state.";
        return CreateEvaluationTool(client, questions, name, effectiveDescription, model);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> tool for a multiple-choice categorization question.
    /// </summary>
    public static TypeSafeFunction CreateChoiceTool(
        ITypeSafeClient client,
        string id,
        TypeSafeContent? instructions,
        IReadOnlyDictionary<string, TypeSafeContent?> criteria,
        string name = "classify_intent",
        string? description = null,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(criteria);

        var questions = new ReadOnlyDictionary<string, TypeSafeQuestion>(new Dictionary<string, TypeSafeQuestion>
        {
            [id] = new Choice { Instructions = instructions ?? id, Criteria = criteria }
        });

        var effectiveDescription = description ?? $"Classifies the provided state among '{string.Join(", ", criteria.Keys)}'.";
        return CreateEvaluationTool(client, questions, name, effectiveDescription, model);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> tool for a rubric scoring question.
    /// </summary>
    public static TypeSafeFunction CreateScoreTool(
        ITypeSafeClient client,
        string id,
        TypeSafeContent? instructions,
        IReadOnlyList<TypeSafeContent> criteria,
        string name = "score_rubric",
        string? description = null,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(criteria);

        var questions = new ReadOnlyDictionary<string, TypeSafeQuestion>(new Dictionary<string, TypeSafeQuestion>
        {
            [id] = new Score { Instructions = instructions ?? id, Criteria = criteria }
        });

        var effectiveDescription = description ?? $"Evaluates rubric score '{id}' on the provided state.";
        return CreateEvaluationTool(client, questions, name, effectiveDescription, model);
    }

    /// <summary>
    /// Evaluates a candidate collection of <see cref="AITool"/> instances against the specified state
    /// and returns the most relevant tools ranked by probability.
    /// </summary>
    public static async Task<IList<AITool>> ShortlistToolsAsync(
        ITypeSafeClient client,
        IEnumerable<AITool> candidateTools,
        TypeSafeContent state,
        TypeSafeToolSelectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(candidateTools);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        var opt = (options ?? new TypeSafeToolSelectionOptions()).Snapshot();
        return await RelevancePolicy.SelectAsync(client, candidateTools.ToList(), state, opt,
            GetToolName, t => GetToolSemanticDescription(t, opt.IncludeStructuredMetadata), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates a candidate collection of <see cref="AITool"/> instances against request messages
    /// and returns the most relevant tools ranked by probability.
    /// </summary>
    public static Task<IList<AITool>> ShortlistToolsAsync(
        ITypeSafeClient client,
        IEnumerable<AITool> candidateTools,
        IEnumerable<ChatMessage> messages,
        TypeSafeToolSelectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = (options ?? new TypeSafeToolSelectionOptions()).Snapshot();
        var extractor = snapshot.ContentExtractor ?? TypeSafeContentConverter.DefaultExtractor;
        var materialized = messages.ToArray();
        var state = materialized.Length == 0 ? TypeSafeContent.FromString("") : extractor.Extract(materialized);
        return ShortlistToolsAsync(client, candidateTools, state, snapshot, cancellationToken);
    }

    /// <summary>
    /// Creates an <see cref="AIContextProvider"/> that dynamically shortlists the agent's available tools
    /// before every LLM turn.
    /// </summary>
    public static TypeSafeToolSelectionProvider CreateSelectionProvider(
        ITypeSafeClient client,
        TypeSafeToolSelectionOptions? options = null,
        ILoggerFactory? loggerFactory = null)
        => new(client, options, loggerFactory);

    /// <summary>
    /// Creates an <see cref="AIContextProvider"/> that dynamically shortlists the agent's available tools
    /// to the specified top-K before every LLM turn.
    /// </summary>
    public static TypeSafeToolSelectionProvider CreateSelectionProvider(
        ITypeSafeClient client,
        int topK,
        double? minimumRelevanceProbability = 0.50,
        TypeSafeRelevanceFallbackMode fallbackMode = TypeSafeRelevanceFallbackMode.Throw,
        ILoggerFactory? loggerFactory = null)
        => new(client, new TypeSafeToolSelectionOptions
        {
            TopK = topK,
            MinimumRelevanceProbability = minimumRelevanceProbability,
            FallbackMode = fallbackMode
        }, loggerFactory);

    /// <summary>
    /// Creates an <see cref="AIFunction"/> meta-tool allowing a supervisor or planner agent
    /// to dynamically query TypeSafe System One for tool relevance recommendations mid-run.
    /// </summary>
    public static TypeSafeFunction CreateToolSelectionTool(
        ITypeSafeClient client,
        IEnumerable<AITool> availableTools,
        string name = "recommend_tools",
        string? description = null,
        string? model = null,
        int topK = 5)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(availableTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var toolSnapshot = availableTools.ToList();
        var effectiveDesc = description ?? "Recommends the most relevant tools for a given task or user state.";

        return new TypeSafeFunction(
            name,
            effectiveDesc,
            StateEvaluationSchema,
            async (args, ct) =>
            {
                var state = ExtractState(args);
                var shortlisted = await ShortlistToolsAsync(
                    client,
                    toolSnapshot,
                    state,
                    new TypeSafeToolSelectionOptions { TopK = topK, Model = model },
                    ct).ConfigureAwait(false);

                return shortlisted.Select(GetToolName).ToArray();
            });
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> meta-tool (e.g. 'request_tools' or 'search_tools') that dynamically
    /// shortlists tools from a catalog using TypeSafe System One and adds them to the managed per-run function-calling loop
    /// (<see cref="FunctionInvokingChatClient.CurrentContext"/>).
    /// </summary>
    public static TypeSafeFunction CreateDynamicToolExpansionTool(
        ITypeSafeClient client,
        IEnumerable<AITool> toolCatalog,
        string name = "request_tools",
        string? description = null,
        TypeSafeToolSelectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(toolCatalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var toolSnapshot = toolCatalog.ToList();
        var effectiveDesc = description ?? "Requests additional specialized tools to be loaded dynamically based on a description of the capability needed.";
        var effectiveOptions = (options ?? new TypeSafeToolSelectionOptions()).Snapshot();

        return new TypeSafeFunction(
            name,
            effectiveDesc,
            StateEvaluationSchema,
            async (args, ct) =>
            {
                TypeSafeExpansionChatClient.RequireScope();
                var state = ExtractState(args);
                var shortlisted = await ShortlistToolsAsync(
                    client,
                    toolSnapshot,
                    state,
                    effectiveOptions,
                    ct).ConfigureAwait(false);

                var addedNames = TypeSafeExpansionChatClient.Merge(shortlisted, ct);

                return addedNames.Count > 0
                    ? $"Successfully loaded {addedNames.Count} tool(s): {string.Join(", ", addedNames)}."
                    : "No new tools were loaded for the specified requirement.";
            });
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> meta-tool with explicit top-K and thresholding parameters.
    /// </summary>
    public static TypeSafeFunction CreateDynamicToolExpansionTool(
        ITypeSafeClient client,
        IEnumerable<AITool> toolCatalog,
        int topK,
        double? minimumRelevanceProbability = 0.50,
        double? dropOffRatio = null,
        string name = "request_tools",
        string? description = null)
    {
        return CreateDynamicToolExpansionTool(
            client,
            toolCatalog,
            name,
            description,
            new TypeSafeToolSelectionOptions
            {
                TopK = topK,
                MinimumRelevanceProbability = minimumRelevanceProbability,
                DropOffRatio = dropOffRatio
            });
    }

    internal static string GetToolName(AITool tool) => tool.Name;

    internal static string GetToolSemanticDescription(AITool tool, bool includeStructuredMetadata = true)
    {
        if (tool is not AIFunction fn)
        {
            return tool.Description ?? tool.Name;
        }

        var baseDesc = !string.IsNullOrWhiteSpace(fn.Description) ? fn.Description : fn.Name;
        if (!includeStructuredMetadata)
        {
            return baseDesc;
        }

        try
        {
            var schema = fn.JsonSchema;
            if (schema.ValueKind == JsonValueKind.Object &&
                schema.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object)
            {
                var paramList = new List<string>();
                foreach (var prop in props.EnumerateObject())
                {
                    var pName = prop.Name;
                    var pType = prop.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? "object"
                        : "object";
                    var pDesc = prop.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;

                    paramList.Add(string.IsNullOrWhiteSpace(pDesc) ? $"{pName} ({pType})" : $"{pName} ({pType}): {pDesc}");
                }

                if (paramList.Count > 0)
                {
                    return $"{baseDesc}. Parameters: {string.Join(", ", paramList)}";
                }
            }
        }
        catch (Exception)
        {
            // Non-fatal: if schema format is non-standard, fall back cleanly to base description
        }

        return baseDesc;
    }

    internal static string GetToolDescription(AITool tool) => GetToolSemanticDescription(tool, false);

    private static TypeSafeContent ExtractState(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetValue("state", out var stateObj) || stateObj is null)
        {
            throw new ArgumentException("Missing required 'state' argument.", nameof(arguments));
        }

        if (stateObj is TypeSafeContent tsContent)
        {
            return tsContent;
        }

        if (stateObj is string text)
        {
            var trimmed = text.Trim();
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            {
                try
                {
                    var parsedNode = JsonNode.Parse(trimmed);
                    if (parsedNode is JsonObject or JsonArray)
                    {
                        return TypeSafeContent.FromJson(parsedNode);
                    }
                }
                catch (JsonException)
                {
                    // Fall back to string
                }
            }

            return TypeSafeContent.FromString(text);
        }

        if (stateObj is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => TypeSafeContent.FromString(element.GetString()!),
                JsonValueKind.Object or JsonValueKind.Array => TypeSafeContent.FromJson(JsonNode.Parse(element.GetRawText())!),
                _ => throw new ArgumentException($"Invalid state element kind: {element.ValueKind}.")
            };
        }

        if (stateObj is JsonNode node && node is JsonObject or JsonArray)
        {
            return TypeSafeContent.FromJson(node);
        }

        return TypeSafeContent.FromString(stateObj.ToString() ?? string.Empty);
    }
}
