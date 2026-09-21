using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenAI;
using TypeSafe.AI;
using TypeSafe.Sample.ChatWithSql;
using TypeSafe.Samples;

var live = SampleSupport.LiveMode(args);

Console.WriteLine(
    live ? "Live model, TypeSafe and PostgreSQL; errors propagate." : "Offline deterministic SQL fixture.");
var services = new ServiceCollection();

if (live)
{
    services.AddTypeSafeClient(options => options.ApiKey = SampleSupport.Required("TYPESAFE_API_KEY"));
    services.AddScoped<NpgsqlDataSource>(_ =>
        new NpgsqlSlimDataSourceBuilder(SampleSupport.Required("SQL_CONNECTION_STRING")).Build());
    services.AddScoped<IQueryDatabase, PostgresQueryDatabase>();
    services.AddScoped<IChatClient>(_ => CreateLiveClient());
}
else
{
    services.AddScoped<ITypeSafeClient>(_ => new DemoTypeSafeClient((state, questions) =>
        questions.ToDictionary(q => q.Key, _ =>
        {
            var text = state.ToJsonNode().ToString();
            return (TypeSafeAnswer)new NoulAnswer
                { Noul = text.Contains("DELETE", StringComparison.OrdinalIgnoreCase) ? 0.01 : 0.99 };
        })));
    services.AddScoped<IQueryDatabase, FixtureDatabase>();
    services.AddScoped<IChatClient>(_ => new DemoModelClient((messages, _) =>
    {
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        return result is null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("query", "execute_sql",
                    new Dictionary<string, object?> { ["query"] = FixtureDatabase.AllowedQuery })
            ]))
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Result?.ToString()));
    }));
}

services.AddVerifiedSqlAgent();

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true, ValidateOnBuild = true
});

await using var scope = provider.CreateAsyncScope();
var agent = scope.ServiceProvider.GetRequiredService<AIAgent>();

var answer = await agent.RunAsync("List the first five customer names.");

Console.WriteLine(answer.Text);

if (!live)
{
    SampleSupport.Require(answer.Text.Contains("Acme", StringComparison.Ordinal), "SQL fixture did not execute.");
    var denied = await scope.ServiceProvider.GetRequiredService<VerifiedSqlExecutor>()
        .ExecuteAsync("DELETE FROM customers", "List customer names.");
    SampleSupport.Require(denied.StartsWith("DENIED", StringComparison.Ordinal) &&
                          ((FixtureDatabase)scope.ServiceProvider.GetRequiredService<IQueryDatabase>())
                          .ExecutionCount == 1,
        "Denied query reached the database.");
    Console.WriteLine(denied);
}


static IChatClient CreateLiveClient()
{
    var options = new OpenAIClientOptions();
    if (Environment.GetEnvironmentVariable("OPENAI_BASE_URL") is { Length: > 0 } endpoint)
        options.Endpoint = new Uri(endpoint);
    return new OpenAIClient(new ApiKeyCredential(SampleSupport.Required("OPENAI_API_KEY")), options)
        .GetChatClient(SampleSupport.Required("OPENAI_MODEL")).AsIChatClient();
}