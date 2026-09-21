using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Sample.ChatWithSql;

public sealed class JevSqlVerifier(TypeSafeAgent evaluator)
{
    public async Task<bool> VerifyQueryAsync(string sql, string userPrompt, CancellationToken cancellationToken = default)
    {
        var state = new JsonObject { ["candidate_sql"] = sql, ["user_inquiry"] = userPrompt };
        var response = await evaluator.RunEvaluationAsync(
            [new ChatMessage(ChatRole.User, state.ToJsonString())], cancellationToken: cancellationToken);
        foreach (var id in new[] { "read_only", "authorized", "aligned" })
        {
            if (!response.Answers.TryGetValue(id, out var answer) || answer is not NoulAnswer noul ||
                !double.IsFinite(noul.Noul) || noul.Noul is < 0 or > 1)
                throw new TypeSafeProtocolException($"Missing or invalid SQL verification answer: {id}.");
            if (noul.Noul < 0.80) return false;
        }
        return true;
    }
}

// The database still enforces access even if probabilistic screening approves an unsafe query.
public sealed class VerifiedSqlExecutor(JevSqlVerifier verifier, IQueryDatabase database)
{
    public async Task<string> ExecuteAsync(string sql, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!await verifier.VerifyQueryAsync(sql, userPrompt, cancellationToken))
            return "DENIED: candidate SQL failed verification.";
        cancellationToken.ThrowIfCancellationRequested();
        return await database.ExecuteAsync(sql, cancellationToken);
    }
}
