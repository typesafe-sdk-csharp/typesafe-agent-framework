using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Sample.ChatWithSql;

public static class SqlServiceCollectionExtensions
{
    // The caller registers IChatClient, ITypeSafeClient and IQueryDatabase, then opens a scope per conversation.
    public static IServiceCollection AddVerifiedSqlAgent(this IServiceCollection services)
    {
        services.AddTypeSafeAgent(q => q
            .Noul("read_only", "Is candidate_sql strictly read-only, without mutations or procedure calls?")
            .Noul("authorized", "Does candidate_sql access only the authorized customers(id, name) table?")
            .Noul("aligned", "Does candidate_sql answer user_inquiry without unrelated actions?"),
            lifetime: ServiceLifetime.Scoped);
        services.AddScoped<JevSqlVerifier>();
        services.AddScoped<VerifiedSqlExecutor>();
        services.AddScoped<SqlExecutionTool>();
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}
            """);
        services.AddTypeSafeFunction<SqlExecutionTool>(
            "execute_sql", "Execute a verified read-only customer query.", schema.RootElement,
            static (tool, arguments, cancellationToken) => tool.InvokeAsync(arguments, cancellationToken),
            lifetime: ServiceLifetime.Scoped);
        services.AddScoped<SqlSchemaContextProvider>();
        services.AddScoped<SqlAgentFactory>();
        services.AddScoped<AIAgent>(sp => sp.GetRequiredService<SqlAgentFactory>().CreateVerifiedAgent());
        return services;
    }
}
