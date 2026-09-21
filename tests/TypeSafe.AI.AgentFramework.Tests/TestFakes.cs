using System.Collections.ObjectModel;
using TypeSafe.AI;

namespace TypeSafe.AI.AgentFramework.Tests;

public sealed class FakeTypeSafeClient : ITypeSafeClient
{
    private readonly Func<TypeSafeContent, IReadOnlyDictionary<string, TypeSafeQuestion>, string?, SystemOneResponse> _handler;

    public FakeTypeSafeClient(Func<TypeSafeContent, IReadOnlyDictionary<string, TypeSafeQuestion>, string?, SystemOneResponse> handler)
    {
        _handler = handler;
    }

    public FakeTypeSafeClient(SystemOneResponse defaultResponse)
        : this((_, _, _) => defaultResponse)
    {
    }

    public List<(TypeSafeContent State, IReadOnlyDictionary<string, TypeSafeQuestion> Questions, string? Model)> Invocations { get; } = [];

    public Task<SystemOneResponse> SystemOneAsync(SystemOneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SystemOneAsync(request.State, request.Questions, request.Model, cancellationToken);
    }

    public Task<SystemOneResponse> SystemOneAsync(TypeSafeContent state,
        IReadOnlyDictionary<string, TypeSafeQuestion> questions,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invocations.Add((state, questions, model));
        return Task.FromResult(_handler(state, questions, model));
    }

    public static SystemOneResponse CreateSampleResponse(
        string model = "jev-latest",
        Dictionary<string, TypeSafeAnswer>? answers = null,
        int inputTokens = 42,
        int outputTokens = 12,
        string? requestId = "req-test-123")
    {
        answers ??= new Dictionary<string, TypeSafeAnswer>
        {
            ["is_urgent"] = new NoulAnswer { Noul = 0.95 },
            ["category"] = new ChoiceAnswer
            {
                Choice = "billing",
                Confidence = 0.98,
                Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
                {
                    ["billing"] = 0.98,
                    ["technical"] = 0.02
                })
            },
            ["severity"] = new ScoreAnswer
            {
                Score = 2.0,
                Confidence = 0.91,
                Legend = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
                {
                    ["0"] = "Low",
                    ["1"] = "Medium",
                    ["2"] = "High"
                }),
                Probabilities = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>
                {
                    ["0"] = 0.03,
                    ["1"] = 0.06,
                    ["2"] = 0.91
                })
            }
        };

        return new SystemOneResponse(
            model,
            new ReadOnlyDictionary<string, TypeSafeAnswer>(answers),
            new TypeSafeUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
            requestId);
    }
}
