using System.Diagnostics;

namespace TypeSafe.AI.AgentFramework;

internal static class RelevancePolicy
{
    internal static readonly ActivitySource Diagnostics = new("TypeSafe.AI.AgentFramework.Selection");

    internal static async Task<IList<T>> SelectAsync<T>(ITypeSafeClient client, IList<T> candidates,
        TypeSafeContent state, TypeSafeToolSelectionOptions options, Func<T, string> name,
        Func<T, string> description, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name(candidate));
            if (!names.Add(name(candidate))) throw new ArgumentException("Duplicate candidate names are not supported.");
        }
        if (candidates.Count == 0) return [];
        using var activity = Diagnostics.StartActivity("select", ActivityKind.Internal);
        activity?.SetTag("candidate.count", candidates.Count);
        var timer = Stopwatch.StartNew();
        try
        {
            if (TypeSafeContentConverter.IsBlankState(state))
            {
                activity?.SetTag("fallback.reason", "missing_context");
                if (options.FallbackMode == TypeSafeRelevanceFallbackMode.Throw)
                    throw new InvalidOperationException("Relevance evaluation context is unavailable.");
                activity?.SetTag("outcome", "fallback");
                var fallback = candidates.Where(x => options.FallbackMode == TypeSafeRelevanceFallbackMode.IncludeAll ||
                    options.AlwaysIncludeToolNames.Contains(name(x))).ToList();
                activity?.SetTag("selected.count", fallback.Count);
                return fallback;
            }
            var instructions = options.Instructions?.ToJsonNode().ToString();
            var questions = new Dictionary<string, TypeSafeQuestion>(StringComparer.Ordinal);
            for (var i = 0; i < candidates.Count; i++)
                questions.Add($"r{i:D8}", new Noul { Instructions = TypeSafeContent.FromString(
                    $"Is this capability relevant to fulfilling the request? Evaluate independently of other capabilities.\n{instructions}\nName: {name(candidates[i])}\nDescription: {description(candidates[i])}") });
            var response = await client.SystemOneAsync(state, questions, options.Model, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            activity?.SetTag("model", response.Model);
            activity?.SetTag("request.id", response.RequestId);
            var ranked = candidates.Select((candidate, i) =>
            {
                if (!response.Answers.TryGetValue($"r{i:D8}", out var answer) || answer is not NoulAnswer noul ||
                    !double.IsFinite(noul.Noul) || noul.Noul is < 0 or > 1)
                    throw new TypeSafeProtocolException($"Invalid relevance answer r{i:D8}.");
                return (Candidate: candidate, Probability: noul.Noul);
            }).OrderByDescending(x => x.Probability).ToList();
            var cutoff = ranked[0].Probability * (options.DropOffRatio ?? 0);
            var selected = ranked.Where(x => !options.AlwaysIncludeToolNames.Contains(name(x.Candidate)) &&
                    x.Probability >= (options.MinimumRelevanceProbability ?? 0) && x.Probability >= cutoff)
                .Take(options.TopK).Select(x => x.Candidate).ToList();
            selected.AddRange(candidates.Where(x => options.AlwaysIncludeToolNames.Contains(name(x))));
            activity?.SetTag("outcome", "selected");
            activity?.SetTag("selected.count", selected.Count);
            return selected;
        }
        catch (Exception ex) when (ex is TypeSafeProtocolException or TypeSafeConnectionException or
            TypeSafeTimeoutException or TypeSafeRateLimitException or TypeSafeOverloadedException or HttpRequestException ||
            ex is TypeSafeException { StatusCode: { } status } && (int)status is >= 500 and <= 599)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.SetTag("outcome", options.FallbackMode == TypeSafeRelevanceFallbackMode.Throw ? "failed" : "fallback");
            activity?.SetTag("fallback.reason", ex.GetType().Name);
            if (options.FallbackMode == TypeSafeRelevanceFallbackMode.Throw) throw;
            var selected = candidates.Where(x => options.FallbackMode == TypeSafeRelevanceFallbackMode.IncludeAll ||
                options.AlwaysIncludeToolNames.Contains(name(x))).ToList();
            activity?.SetTag("selected.count", selected.Count);
            return selected;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("outcome", "canceled");
            throw;
        }
        catch (Exception)
        {
            activity?.SetTag("outcome", "failed");
            throw;
        }
        finally { activity?.SetTag("duration.ms", timer.Elapsed.TotalMilliseconds); }
    }
}
