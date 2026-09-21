using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeContentConverterTests
{
    [Fact]
    public void ExtractContent_PlainText_ReturnsTextContent()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "Can you help reset my password?") };
        var content = TypeSafeContentConverter.ExtractContent(messages);

        var json = JsonSerializer.Serialize(content, AgentFrameworkJsonContext.Default.TypeSafeContent);
        Assert.Equal("\"Can you help reset my password?\"", json);
    }

    [Fact]
    public void ExtractContent_JsonStringInText_ParsesAsJsonObject()
    {
        var jsonText = "{\"ticket_id\":42,\"urgency\":\"critical\"}";
        var messages = new[] { new ChatMessage(ChatRole.User, jsonText) };
        var content = TypeSafeContentConverter.ExtractContent(messages);

        var serialized = JsonSerializer.Serialize(content, AgentFrameworkJsonContext.Default.TypeSafeContent);
        Assert.Contains("\"ticket_id\":42", serialized);
        Assert.Contains("\"urgency\":\"critical\"", serialized);
    }

    [Fact]
    public void ExtractContent_DataContentWithJson_ExtractsStructuredContent()
    {
        var payload = Encoding.UTF8.GetBytes("{\"device_id\":\"iot-99\",\"alert\":\"high_temp\"}");
        var dataContent = new DataContent(payload, "application/json");
        var message = new ChatMessage(ChatRole.User, [dataContent]);

        var content = TypeSafeContentConverter.ExtractContent([message]);
        var serialized = JsonSerializer.Serialize(content, AgentFrameworkJsonContext.Default.TypeSafeContent);

        Assert.Contains("\"device_id\":\"iot-99\"", serialized);
        Assert.Contains("\"alert\":\"high_temp\"", serialized);
    }

    [Fact]
    public void ExtractContent_PrefersLatestExternalMessage()
    {
        var internalMsg = new ChatMessage(ChatRole.Assistant, "Internal thinking")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "ThinkingProvider");
        var userMsg = new ChatMessage(ChatRole.User, "Actual user input")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "UserChannel");

        var content = TypeSafeContentConverter.ExtractContent([userMsg, internalMsg]);
        var serialized = JsonSerializer.Serialize(content, AgentFrameworkJsonContext.Default.TypeSafeContent);

        Assert.Equal("\"Actual user input\"", serialized);
    }

    [Fact]
    public void ChatTranscriptExtractor_FormatsMultiTurnHistory()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello") { AuthorName = "Alice" },
            new ChatMessage(ChatRole.Assistant, "Hi there! How can I help?") { AuthorName = "SupportBot" },
            new ChatMessage(ChatRole.User, "My screen is flickering") { AuthorName = "Alice" }
        };

        var content = TypeSafeContentConverter.ChatTranscriptExtractor.Extract(messages);
        var serialized = JsonSerializer.Serialize(content, AgentFrameworkJsonContext.Default.TypeSafeContent);

        Assert.Contains("User (Alice): Hello", serialized);
        Assert.Contains("Assistant (SupportBot): Hi there! How can I help?", serialized);
        Assert.Contains("User (Alice): My screen is flickering", serialized);
    }

    [Fact]
    public void FromDelegate_UsesCustomExtractorFunction()
    {
        var customExtractor = TypeSafeContentConverter.FromDelegate(msgs =>
            TypeSafeContent.FromString($"Custom count: {msgs.Count}"));

        var content = customExtractor.Extract([new ChatMessage(ChatRole.User, "A"), new ChatMessage(ChatRole.User, "B")]);
        var serialized = JsonSerializer.Serialize(content, AgentFrameworkJsonContext.Default.TypeSafeContent);

        Assert.Equal("\"Custom count: 2\"", serialized);
    }

    [Fact]
    public void ToChatMessage_CreatesChatMessageWithEvaluationContentAndProperties()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse();
        var message = TypeSafeContentConverter.ToChatMessage(response, "TestEvaluator");

        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("TestEvaluator", message.AuthorName);

        var evalContent = Assert.Single(message.Contents.OfType<TypeSafeEvaluationContent>());
        Assert.Same(response, evalContent.Response);

        Assert.NotNull(message.AdditionalProperties);
        Assert.Equal("jev-latest", message.AdditionalProperties["typesafe.model"]);
        Assert.Equal("req-test-123", message.AdditionalProperties["typesafe.requestId"]);
    }
}
