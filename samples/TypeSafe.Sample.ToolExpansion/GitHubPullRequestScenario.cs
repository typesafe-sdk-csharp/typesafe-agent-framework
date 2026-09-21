using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Sample.ToolExpansion;

public sealed record GitHubPullRequestTarget(string Repository, int Number)
{
    public string Owner => Repository[..Repository.IndexOf('/')];
    public string Name => Repository[(Repository.IndexOf('/') + 1)..];
}

public sealed record GitHubPullRequestSampleOptions(bool Live, GitHubPullRequestTarget Target);

public static partial class GitHubPullRequestScenario
{
    public static Uri GitHubMcpEndpoint { get; } = new("https://api.githubcopilot.com/mcp/x/all");

    public static GitHubPullRequestSampleOptions ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args is ["--offline"])
            return new(false, new GitHubPullRequestTarget("octo-org/agent-framework", 123));

        if (args.Length != 5 || args[0] != "--live")
            throw new ArgumentException("Usage: --offline (default) or --live --repo owner/repo --pr 123.");

        string? repository = null;
        string? pullRequest = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--repo" when repository is null:
                    repository = args[i + 1];
                    break;
                case "--pr" when pullRequest is null:
                    pullRequest = args[i + 1];
                    break;
                default:
                    throw new ArgumentException("Usage: --offline (default) or --live --repo owner/repo --pr 123.");
            }
        }

        if (repository is null || !RepositoryPattern().IsMatch(repository))
            throw new ArgumentException("--repo must be in owner/repo format.");
        if (!int.TryParse(pullRequest, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException("--pr must be a positive integer.");

        return new(true, new GitHubPullRequestTarget(repository, number));
    }

    public static TypeSafeToolSelectionOptions CreateExpansionOptions() => new()
    {
        TopK = 1,
        MinimumRelevanceProbability = null,
        IncludeStructuredMetadata = true,
        FallbackMode = TypeSafeRelevanceFallbackMode.Throw
    };

    public static string CreateReviewPrompt(GitHubPullRequestTarget target) =>
        $"Review pull request #{target.Number} in {target.Repository}. First request exactly one GitHub MCP tool " +
        "that can read the pull request. Then use the selected tool and provide a concise read-only review of the " +
        "title, status, changed areas, and notable risks. Do not modify GitHub resources.";

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
}

public sealed class OfflineGitHubCatalog
{
    private readonly Dictionary<string, int> _invocations = new(StringComparer.Ordinal);

    public OfflineGitHubCatalog()
    {
        Tools =
        [
            Create("pull_request_read", "Read a GitHub pull request's metadata, files, diff summary, and review state.",
                "PR #123 is open. It adds TypeSafe GitHub MCP tool selection and has no blocking review findings."),
            Create("issue_read", "Read a GitHub issue, its comments, labels, and status.", "Issue details returned."),
            Create("repository_search", "Search GitHub repositories for matching names and metadata.", "Repository search returned."),
            Create("pull_request_create", "Create a new GitHub pull request. This changes GitHub state.", "Pull request created."),
            Create("pull_request_merge", "Merge an existing GitHub pull request. This changes GitHub state.", "Pull request merged.")
        ];
    }

    public IReadOnlyList<AITool> Tools { get; }

    public int InvocationCount(string toolName) => _invocations.GetValueOrDefault(toolName);

    private AITool Create(string name, string description, string result)
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "owner": { "type": "string", "description": "Repository owner." },
                "repo": { "type": "string", "description": "Repository name." },
                "pullNumber": { "type": "integer", "description": "Pull request number." }
              }
            }
            """).RootElement.Clone();
        return new TypeSafeFunction(name, description, schema, (_, _) =>
        {
            _invocations[name] = InvocationCount(name) + 1;
            return ValueTask.FromResult<object?>(result);
        });
    }
}
