using System.ClientModel;
using System.Net.Http.Headers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using OpenAI;
using TypeSafe.AI;
using TypeSafe.AI.AgentFramework;
using TypeSafe.Sample.ToolSelection;
using TypeSafe.Samples;

var sample = SelectionScenario.ParseArguments(args);
Console.WriteLine(sample.Live
    ? "Live GitHub MCP catalog, TypeSafe proactive tool selection, and OpenAI pull-request review."
    : "Offline deterministic GitHub MCP tool-selection fixture.");

using var githubHttpClient = sample.Live ? CreateGitHubHttpClient() : null;
await using var mcpClient = sample.Live
    ? await McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = SelectionScenario.GitHubMcpEndpoint,
        Name = "GitHub MCP full catalog",
        TransportMode = HttpTransportMode.StreamableHttp
    }, githubHttpClient!))
    : null;

var offlineCatalog = sample.Live ? null : new OfflineGitHubCatalog();
IList<AITool> catalog = sample.Live
    ? [.. (await mcpClient!.ListToolsAsync()).Cast<AITool>()]
    : [.. offlineCatalog!.Tools];

if (catalog.Count == 0)
    throw new InvalidOperationException("GitHub MCP returned no tools.");

Console.WriteLine($"Catalog tools eligible for proactive selection: {catalog.Count}");

var services = new ServiceCollection();
if (sample.Live)
{
    services.AddTypeSafeClient(options => options.ApiKey = SampleSupport.Required("TYPESAFE_API_KEY"));
    services.AddScoped<IChatClient>(_ => CreateLiveClient().AsBuilder().UseFunctionInvocation().Build());
}
else
{
    services.AddScoped<ITypeSafeClient>(_ => SampleSupport.RelevanceClient(new Dictionary<string, double>
    {
        ["pull_request_read"] = 0.99,
        ["issue_read"] = 0.40,
        ["repository_search"] = 0.30,
        ["pull_request_create"] = 0.05,
        ["pull_request_merge"] = 0.01
    }));
    services.AddScoped<IChatClient>(_ => new DemoModelClient(OfflineRespond).AsBuilder().UseFunctionInvocation().Build());
}

services.AddScoped<IList<AITool>>(_ => catalog);
services.AddTypeSafeToolSelectionProvider(SelectionScenario.CreateOptions(), lifetime: ServiceLifetime.Scoped);
services.AddScoped<AIAgent>(sp => new ChatClientAgent(sp.GetRequiredService<IChatClient>(), new ChatClientAgentOptions
{
    Name = "GitHubPullRequestReviewer",
    UseProvidedChatClientAsIs = true,
    ChatOptions = new()
    {
        Instructions = "You review GitHub pull requests using the single read-only tool selected before this turn. " +
            "Do not create, update, comment on, merge, close, or otherwise modify any GitHub resource.",
        Tools = sp.GetRequiredService<IList<AITool>>()
    },
    AIContextProviders = [sp.GetRequiredService<TypeSafeToolSelectionProvider>()]
}));

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});
await using var scope = provider.CreateAsyncScope();
var agent = scope.ServiceProvider.GetRequiredService<AIAgent>();
var response = await agent.RunAsync(SelectionScenario.CreateReviewPrompt(sample.Target));
Console.WriteLine(response.Text);

if (!sample.Live)
{
    SampleSupport.Require(catalog.Count == offlineCatalog!.Tools.Count,
        "Proactive selection mutated the original GitHub tool catalog.");
    SampleSupport.Require(offlineCatalog.InvocationCount("pull_request_read") == 1,
        "The fixture did not invoke the selected pull-request read tool exactly once.");
    SampleSupport.Require(offlineCatalog.InvocationCount("pull_request_create") == 0 &&
                          offlineCatalog.InvocationCount("pull_request_merge") == 0,
        "A mutating GitHub tool was invoked by the fixture.");
}

static HttpClient CreateGitHubHttpClient()
{
    var client = new HttpClient(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true
    });
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Bearer", SampleSupport.Required("GITHUB_PAT_TOKEN"));
    return client;
}

static IChatClient CreateLiveClient()
{
    var options = new OpenAIClientOptions();
    if (Environment.GetEnvironmentVariable("OPENAI_BASE_URL") is { Length: > 0 } endpoint)
        options.Endpoint = new Uri(endpoint);

    return new OpenAIClient(new ApiKeyCredential(SampleSupport.Required("OPENAI_API_KEY")), options)
        .GetChatClient(SampleSupport.Required("OPENAI_MODEL"))
        .AsIChatClient();
}

static ChatResponse OfflineRespond(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
{
    var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
    if (result is null)
    {
        SampleSupport.Require(options!.Tools!.Select(tool => tool.Name).SequenceEqual(["pull_request_read"]),
            "TypeSafe did not proactively select exactly the pull-request read tool.");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("read-pr", "pull_request_read", new Dictionary<string, object?>
            {
                ["owner"] = "octo-org", ["repo"] = "agent-framework", ["pullNumber"] = 123
            })
        ]));
    }

    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
        $"Offline review: {result.Result}"));
}
