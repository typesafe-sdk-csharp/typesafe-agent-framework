namespace TypeSafe.AI.AgentFramework;

internal static class ScreeningAnswers
{
    internal static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    internal static bool Valid(TypeSafeQuestion question, TypeSafeAnswer answer) => question switch
    {
        Noul => answer is NoulAnswer n && Probability(n.Noul),
        Choice q => answer is ChoiceAnswer c && c.Choice is not null && q.Criteria.ContainsKey(c.Choice) &&
            Probability(c.Confidence) && c.Probabilities is not null &&
            c.Probabilities.All(p => q.Criteria.ContainsKey(p.Key) && Probability(p.Value)),
        Score q => answer is ScoreAnswer s && double.IsFinite(s.Score) && s.Score >= 0 && s.Score <= q.Criteria.Count - 1 &&
            Probability(s.Confidence) && s.Probabilities is not null && s.Probabilities.All(p =>
                int.TryParse(p.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level) &&
                level >= 0 && level < q.Criteria.Count && Probability(p.Value)),
        _ => false
    };

    internal static void Validate(SystemOneResponse response, IReadOnlyDictionary<string, TypeSafeQuestion> questions)
    {
        foreach (var (id, question) in questions)
            if (!response.Answers.TryGetValue(id, out var answer) || !Valid(question, answer))
                throw new TypeSafeProtocolException($"Missing or invalid screening answer: {id}.");
    }
}
