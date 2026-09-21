using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Sample.ChatWithSql;

public sealed class SqlAgentFactory(
    IChatClient model,
    TypeSafeFunction execute,
    SqlSchemaContextProvider context)
{
    public AIAgent CreateVerifiedAgent() => new ChatClientAgent(model, new ChatClientAgentOptions
    {
        Name = "VerifiedSql",
        ChatOptions = new()
        {
            Instructions = "Answer customer-name queries using execute_sql, then summarize the result. Explain refusals.",
            Tools = [execute]
        },
        AIContextProviders = [context]
    });
}
