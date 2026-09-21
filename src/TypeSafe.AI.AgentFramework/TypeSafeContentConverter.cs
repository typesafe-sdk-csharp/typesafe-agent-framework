using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>Defines an extraction strategy to convert incoming chat messages into TypeSafeContent state.</summary>
public interface ITypeSafeContentExtractor
{
    /// <summary>Extracts evaluation content state from the conversation messages.</summary>
    TypeSafeContent Extract(IEnumerable<ChatMessage> messages);
}

/// <summary>
/// Provides conversion utilities and extraction strategies between Microsoft Agent Framework
/// <see cref="ChatMessage"/>s and TypeSafe <see cref="TypeSafeContent"/>.
/// </summary>
public static class TypeSafeContentConverter
{
    /// <summary>Default extractor: inspects the latest external message (or latest message) for structured JSON or text.</summary>
    public static ITypeSafeContentExtractor DefaultExtractor { get; } = new LastMessageContentExtractor();

    /// <summary>Transcript extractor: formats multi-turn dialogue into a structured transcript.</summary>
    public static ITypeSafeContentExtractor ChatTranscriptExtractor { get; } = new ChatTranscriptContentExtractor();

    /// <summary>Screens every text and JSON part, preserving message roles and boundaries. Other content requires a custom extractor.</summary>
    public static ITypeSafeContentExtractor ScreeningExtractor { get; } = CreateScreeningExtractor();

    /// <summary>Creates a strict transcript extractor. Opaque tool results require explicitly supplied JSON metadata.</summary>
    public static ITypeSafeContentExtractor CreateScreeningExtractor(JsonSerializerOptions? serializerOptions = null) => FromDelegate(messages =>
    {
        var transcript = new JsonArray();
        foreach (var message in messages)
        {
            var parts = new JsonArray();
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    if (!string.IsNullOrWhiteSpace(text.Text)) parts.Add((JsonNode?)JsonValue.Create(text.Text));
                }
                else if (content is DataContent data && data.MediaType == "application/json")
                {
                    try { parts.Add(JsonNode.Parse(data.Data.Span)); }
                    catch (JsonException ex) { throw new NotSupportedException("Invalid JSON screening content.", ex); }
                }
                else if (content is TypeSafeEvaluationContent evaluation)
                    parts.Add(JsonSerializer.SerializeToNode(evaluation.Response, TypeSafeJson.ResponseTypeInfo));
                else if (content is FunctionCallContent call)
                    parts.Add((JsonNode)new JsonObject { ["function"] = call.Name, ["call_id"] = call.CallId,
                        ["arguments"] = ScreeningValue(call.Arguments, serializerOptions) });
                else if (content is FunctionResultContent result)
                    parts.Add((JsonNode)new JsonObject { ["call_id"] = result.CallId,
                        ["result"] = ScreeningValue(result.Result, serializerOptions) });
                else if (content is UsageContent) { }
                else throw new NotSupportedException($"Screening {content.GetType().Name} requires a custom content extractor.");
            }
            if (parts.Count > 0) transcript.Add((JsonNode)new JsonObject { ["role"] = message.Role.Value, ["content"] = parts });
        }
        return transcript.Count == 0 ? TypeSafeContent.FromString("") : TypeSafeContent.FromJson(transcript);
    });

    private static JsonNode? ScreeningValue(object? value, JsonSerializerOptions? options)
    {
        if (value is null) return null;
        if (value is SystemOneResponse evaluation) return JsonSerializer.SerializeToNode(evaluation, TypeSafeJson.ResponseTypeInfo);
        if (value is JsonNode node) return node.DeepClone();
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
        if (value is string text) return JsonValue.Create(text);
        if (value is bool boolean) return JsonValue.Create(boolean);
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
            return JsonValue.Create(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture));
        if (value is float or double)
        {
            var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(number)) throw new NotSupportedException("Non-finite tool values cannot be screened.");
            return JsonValue.Create(number);
        }
        if (value is IEnumerable<KeyValuePair<string, object?>> dictionary)
        {
            var result = new JsonObject();
            foreach (var pair in dictionary) result.Add(pair.Key, ScreeningValue(pair.Value, options));
            return result;
        }
        if (value is System.Collections.IEnumerable sequence)
        {
            var result = new JsonArray();
            foreach (var item in sequence) result.Add(ScreeningValue(item, options));
            return result;
        }
        if (options is not null) return JsonSerializer.SerializeToNode(value, options.GetTypeInfo(value.GetType()));
        throw new NotSupportedException($"Screening tool value {value.GetType().Name} requires configured serialization metadata or a custom extractor.");
    }

    /// <summary>Creates a custom content extractor from a delegate.</summary>
    public static ITypeSafeContentExtractor FromDelegate(Func<IReadOnlyList<ChatMessage>, TypeSafeContent> extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        return new DelegateContentExtractor(extractor);
    }

    /// <summary>Extracts evaluation content from messages using the default extraction strategy.</summary>
    public static TypeSafeContent ExtractContent(IEnumerable<ChatMessage> messages)
        => DefaultExtractor.Extract(messages);

    /// <summary>
    /// Determines whether the extracted state carries no evaluable input (a null, absent, or
    /// whitespace-only text root). Callers skip the upstream evaluation in that case instead of
    /// sending an empty state that the provider would reject.
    /// </summary>
    internal static bool IsBlankState(TypeSafeContent? content)
    {
        if (content is null)
        {
            return true;
        }

        var node = content.ToJsonNode();
        return node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Creates an assistant response ChatMessage populated with a TypeSafeEvaluationContent.</summary>
    public static ChatMessage ToChatMessage(SystemOneResponse response, string? authorName = null, Func<SystemOneResponse, string>? formatter = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = EvaluationSupport.Format(response, formatter);
        var message = new ChatMessage(ChatRole.Assistant, [
            new TextContent(text),
            new TypeSafeEvaluationContent(response)
        ])
        {
            AuthorName = authorName,
            MessageId = Guid.NewGuid().ToString("N"),
            AdditionalProperties = EvaluationSupport.Metadata(response)
        };

        message.AdditionalProperties["typesafe.usage"] = response.Usage;
        if (response.RequestId is not null)
        {
            message.AdditionalProperties["typesafe.requestId"] = response.RequestId;
        }

        return message;
    }

    private sealed class DelegateContentExtractor : ITypeSafeContentExtractor
    {
        private readonly Func<IReadOnlyList<ChatMessage>, TypeSafeContent> _extractor;

        public DelegateContentExtractor(Func<IReadOnlyList<ChatMessage>, TypeSafeContent> extractor)
        {
            _extractor = extractor;
        }

        public TypeSafeContent Extract(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
            return _extractor(list) ?? throw new InvalidOperationException("The content extractor returned null.");
        }
    }

    private sealed class LastMessageContentExtractor : ITypeSafeContentExtractor
    {
        public TypeSafeContent Extract(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
            if (messageList.Count == 0)
            {
                throw new ArgumentException("Cannot extract TypeSafeContent from an empty message collection.", nameof(messages));
            }

            // Prefer the latest external message if available; otherwise use the very last message.
            ChatMessage? target = null;
            for (int i = messageList.Count - 1; i >= 0; i--)
            {
                if (messageList[i].GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External)
                {
                    target = messageList[i];
                    break;
                }
            }
            target ??= messageList[^1];

            // 1. Check for DataContent with JSON payload
            foreach (var content in target.Contents)
            {
                if (content is DataContent dataContent && !dataContent.Data.IsEmpty)
                {
                    try
                    {
                        var jsonString = Encoding.UTF8.GetString(dataContent.Data.Span);
                        var node = JsonNode.Parse(jsonString);
                        if (node is JsonObject or JsonArray)
                        {
                            return TypeSafeContent.FromJson(node);
                        }
                    }
                    catch (JsonException)
                    {
                        // Not a valid JSON root, continue
                    }
                }
            }

            // 2. Check for TextContent or message.Text
            var text = target.Text;
            if (!string.IsNullOrWhiteSpace(text))
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
                        // Fall back to plain text
                    }
                }

                return TypeSafeContent.FromString(text);
            }

            // 3. Fallback: empty text content if no textual or data content was present
            return TypeSafeContent.FromString(string.Empty);
        }
    }

    private sealed class ChatTranscriptContentExtractor : ITypeSafeContentExtractor
    {
        public TypeSafeContent Extract(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            var sb = new StringBuilder();
            foreach (var message in messages)
            {
                var roleName = message.Role.Value switch
                {
                    "user" => "User",
                    "assistant" => "Assistant",
                    "system" => "System",
                    "tool" => "Tool",
                    _ => message.Role.Value
                };

                var author = !string.IsNullOrWhiteSpace(message.AuthorName) ? $" ({message.AuthorName})" : string.Empty;
                var text = message.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(roleName).Append(author).Append(": ").Append(text);
                }
            }

            return TypeSafeContent.FromString(sb.ToString());
        }
    }
}



