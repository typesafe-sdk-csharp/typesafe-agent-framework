using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeToolsTests
{
    [Fact]
    public async Task EvaluationTool_InvokesClientWithExtractedStateAndReturnsResponse()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var tool = TypeSafeTools.CreateEvaluationTool(
            fakeClient,
            q => q.Noul("is_urgent", "Is the request urgent?"),
            name: "evaluate_urgency",
            description: "Evaluates whether an incoming support request is urgent.");

        Assert.Equal("evaluate_urgency", tool.Name);
        Assert.Equal("Evaluates whether an incoming support request is urgent.", tool.Description);
        Assert.Equal(JsonValueKind.Object, tool.JsonSchema.ValueKind);
        Assert.True(tool.JsonSchema.TryGetProperty("required", out var requiredProp));
        Assert.Contains("state", requiredProp.EnumerateArray().Select(e => e.GetString()));

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = "System crash on database node 4"
        });

        var result = await tool.InvokeAsync(args);
        var response = Assert.IsType<SystemOneResponse>(result);
        Assert.Same(sampleResponse, response);

        Assert.Single(fakeClient.Invocations);
        var invocation = fakeClient.Invocations[0];
        Assert.Equal("\"System crash on database node 4\"", JsonSerializer.Serialize(invocation.State, AgentFrameworkJsonContext.Default.TypeSafeContent));
        Assert.True(invocation.Questions.ContainsKey("is_urgent"));
    }

    [Fact]
    public async Task EvaluationTool_AcceptsStructuredJsonState()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var tool = TypeSafeTools.CreateEvaluationTool(
            fakeClient,
            q => q.Noul("is_urgent", "Is urgent?"));

        var jsonObject = new JsonObject
        {
            ["ticket_id"] = 991,
            ["service"] = "auth-gateway",
            ["status_code"] = 504
        };

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = jsonObject
        });

        await tool.InvokeAsync(args);

        Assert.Single(fakeClient.Invocations);
        var serialized = JsonSerializer.Serialize(fakeClient.Invocations[0].State, AgentFrameworkJsonContext.Default.TypeSafeContent);
        Assert.Contains("\"ticket_id\":991", serialized);
        Assert.Contains("\"service\":\"auth-gateway\"", serialized);
    }

    [Fact]
    public async Task NoulTool_BuildsYesNoQuestionAndEvaluates()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var tool = TypeSafeTools.CreateNoulTool(
            fakeClient,
            id: "phishing_alert",
            instructions: "Is this email phishing?",
            name: "detect_phishing",
            whenTrue: "Email contains phishing indicators",
            whenFalse: "Email is legitimate");

        Assert.Equal("detect_phishing", tool.Name);

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = "Click this link to avoid account suspension immediately"
        });

        var result = await tool.InvokeAsync(args);
        Assert.IsType<SystemOneResponse>(result);

        Assert.Single(fakeClient.Invocations);
        var q = Assert.Single(fakeClient.Invocations[0].Questions);
        Assert.Equal("phishing_alert", q.Key);
        var noul = Assert.IsType<Noul>(q.Value);
        Assert.NotNull(noul.Criteria);
    }

    [Fact]
    public async Task ChoiceTool_BuildsMultipleChoiceQuestionAndEvaluates()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var criteria = new Dictionary<string, TypeSafeContent?>
        {
            ["support"] = "Customer needs help",
            ["sales"] = "Customer wants to buy",
            ["other"] = null
        };

        var tool = TypeSafeTools.CreateChoiceTool(
            fakeClient,
            id: "intent",
            instructions: "Classify user intent",
            criteria: criteria,
            name: "classify_user_intent");

        Assert.Equal("classify_user_intent", tool.Name);

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = "I would like pricing details for 50 licenses"
        });

        await tool.InvokeAsync(args);

        Assert.Single(fakeClient.Invocations);
        var q = Assert.Single(fakeClient.Invocations[0].Questions);
        Assert.Equal("intent", q.Key);
        var choice = Assert.IsType<Choice>(q.Value);
        Assert.Equal(3, choice.Criteria.Count);
    }

    [Fact]
    public async Task ScoreTool_BuildsRubricQuestionAndEvaluates()
    {
        var sampleResponse = FakeTypeSafeClient.CreateSampleResponse();
        var fakeClient = new FakeTypeSafeClient(sampleResponse);

        var levels = new TypeSafeContent[]
        {
            "Low complexity",
            "Medium complexity",
            "High complexity"
        };

        var tool = TypeSafeTools.CreateScoreTool(
            fakeClient,
            id: "pr_complexity",
            instructions: "Score pull request complexity",
            criteria: levels,
            name: "score_complexity");

        Assert.Equal("score_complexity", tool.Name);

        var args = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["state"] = "+2000 lines modified across 45 files"
        });

        await tool.InvokeAsync(args);

        Assert.Single(fakeClient.Invocations);
        var q = Assert.Single(fakeClient.Invocations[0].Questions);
        Assert.Equal("pr_complexity", q.Key);
        var score = Assert.IsType<Score>(q.Value);
        Assert.Equal(3, score.Criteria.Count);
    }

    [Fact]
    public async Task EvaluationTool_ThrowsOnMissingStateArgument()
    {
        var fakeClient = new FakeTypeSafeClient(FakeTypeSafeClient.CreateSampleResponse());
        var tool = TypeSafeTools.CreateEvaluationTool(fakeClient, q => q.Noul("q", "question"));

        var emptyArgs = new AIFunctionArguments(new Dictionary<string, object?>());
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(emptyArgs));
    }
}
