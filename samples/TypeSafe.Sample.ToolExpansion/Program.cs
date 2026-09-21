using System.ClientModel;
using System.Net.Http.Headers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using OpenAI;
using TypeSafe.AI;
using TypeSafe.AI.AgentFramework;
using TypeSafe.Sample.ToolExpansion;
using TypeSafe.Samples;

var sample = GitHubPullRequestScenario.ParseArguments(args);

Console.WriteLine(sample.Live
    ? "Live GitHub MCP catalog, TypeSafe tool expansion, and OpenAI pull-request review."
    : "Offline deterministic GitHub MCP tool-expansion fixture.");

using var githubHttpClient = sample.Live ? CreateGitHubHttpClient() : null;
await using var mcpClient = sample.Live
    ? await McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = GitHubPullRequestScenario.GitHubMcpEndpoint,
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

Console.WriteLine($"Catalog tools eligible for selection: {catalog.Count}");

var services = new ServiceCollection();
if (sample.Live)
{
    services.AddTypeSafeClient(options => options.ApiKey = SampleSupport.Required("TYPESAFE_API_KEY"));
    services.AddScoped<IChatClient>(_ => CreateLiveClient());
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
    services.AddScoped<IChatClient>(_ => new DemoModelClient(OfflineRespond));
}

services.AddScoped<IList<AITool>>(_ => catalog);
services.AddScoped<AIAgent>(sp =>
{
    var requestTools = TypeSafeTools.CreateDynamicToolExpansionTool(
        sp.GetRequiredService<ITypeSafeClient>(),
        sp.GetRequiredService<IList<AITool>>(),
        name: "request_tools",
        description: "Load exactly one GitHub MCP tool needed to complete the current pull-request review step.",
        options: GitHubPullRequestScenario.CreateExpansionOptions());

    var loop = sp.GetRequiredService<IChatClient>().AsBuilder().UseTypeSafeToolExpansion().Build();
    return new ChatClientAgent(loop, new ChatClientAgentOptions
    {
        Name = "GitHubPullRequestReviewer",
        UseProvidedChatClientAsIs = true,
        ChatOptions = new()
        {
            Instructions = "You review GitHub pull requests. Start by using request_tools to load exactly one " +
                "tool that can read the requested pull request. Use only that loaded tool. Do not create, update, " +
                "comment on, merge, close, or otherwise modify any GitHub resource.",
            Tools = [requestTools]
        }
    });
});

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

await using var scope = provider.CreateAsyncScope();
var agent = scope.ServiceProvider.GetRequiredService<AIAgent>();
var prompt = GitHubPullRequestScenario.CreateReviewPrompt(sample.Target);
var answer = await agent.RunAsync(prompt);
Console.WriteLine(answer.Text);

if (!sample.Live)
{
    SampleSupport.Require(catalog.Count == offlineCatalog!.Tools.Count,
        "Expansion leaked into the original GitHub tool catalog.");
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
    var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();
    if (results.Count == 0)
    {
        SampleSupport.Require(options!.Tools!.Select(tool => tool.Name).SequenceEqual(["request_tools"]),
            "The offline model received GitHub tools before requesting one.");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("discover-pr", "request_tools", new Dictionary<string, object?>
            {
                ["state"] = "Read the requested GitHub pull request and summarize its title, status, changes, and review risks."
            })
        ]));
    }

    if (results.Count == 1)
    {
        SampleSupport.Require(options!.Tools!.Select(tool => tool.Name)
            .SequenceEqual(["request_tools", "pull_request_read"]),
            "TypeSafe did not inject exactly the pull-request read tool.");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("read-pr", "pull_request_read", new Dictionary<string, object?>
            {
                ["owner"] = "octo-org", ["repo"] = "agent-framework", ["pullNumber"] = 123
            })
        ]));
    }

    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
        $"Offline review: {results[^1].Result}"));
}
