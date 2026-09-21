using Microsoft.Extensions.AI;
using TypeSafe.AI;
using Xunit;

namespace TypeSafe.AI.AgentFramework.Tests;

public class TypeSafeEvaluationContentTests
{
    [Fact]
    public void Constructor_SetsAllTypedPropertiesAndRawRepresentation()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse();
        var content = new TypeSafeEvaluationContent(response);

        Assert.Same(response, content.Response);
        Assert.Same(response, content.RawRepresentation);
        Assert.Equal("jev-latest", content.Model);
        Assert.Equal(42, content.Usage.InputTokens);
        Assert.Equal(12, content.Usage.OutputTokens);
        Assert.Equal("req-test-123", content.RequestId);

        Assert.Single(content.Nouls);
        Assert.Equal(0.95, content.Nouls["is_urgent"].Noul);

        Assert.Single(content.Choices);
        Assert.Equal("billing", content.Choices["category"].Choice);
        Assert.Equal(0.98, content.Choices["category"].Confidence);

        Assert.Single(content.Scores);
        Assert.Equal(2.0, content.Scores["severity"].Score);
        Assert.Equal(0.91, content.Scores["severity"].Confidence);

        Assert.Equal(3, content.Answers.Count);
    }

    [Fact]
    public void FormattedText_IncludesSummaryOfAllAnswers()
    {
        var response = FakeTypeSafeClient.CreateSampleResponse();
        var content = new TypeSafeEvaluationContent(response);

        var summary = content.FormattedText;
        Assert.Contains("is_urgent: p=0.950 (yes)", summary);
        Assert.Contains("category: billing (conf=0.980)", summary);
        Assert.Contains("severity: score=2.00 (conf=0.910)", summary);
        Assert.Equal(summary, content.ToString());
    }

    [Fact]
    public void Constructor_ThrowsOnNullResponse()
    {
        Assert.Throws<ArgumentNullException>(() => new TypeSafeEvaluationContent(null!));
    }
}
