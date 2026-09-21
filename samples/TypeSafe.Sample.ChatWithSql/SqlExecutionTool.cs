using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Sample.ChatWithSql;

public sealed class SqlExecutionTool(VerifiedSqlExecutor executor)
{
    public async ValueTask<object?> InvokeAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var query = arguments.TryGetValue("query", out var value) ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        } : null;
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A query is required.");
        var request = AIAgent.CurrentRunContext?.RequestMessages
            ?? throw new InvalidOperationException("SQL execution requires an agent run context.");
        var prompt = TypeSafeContentConverter.ChatTranscriptExtractor.Extract(request).ToJsonNode().ToString();
        return await executor.ExecuteAsync(query, prompt, cancellationToken);
    }
}
