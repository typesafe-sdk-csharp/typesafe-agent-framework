using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace TypeSafe.AI.AgentFramework;

/// <summary>
/// First-class <see cref="AIContent"/> carrying the complete typed <see cref="SystemOneResponse"/>
/// from a TypeSafe System One evaluation.
/// </summary>
public sealed class TypeSafeEvaluationContent : AIContent
{
    /// <summary>Initializes a new instance of <see cref="TypeSafeEvaluationContent"/> with the evaluation response.</summary>
    public TypeSafeEvaluationContent(SystemOneResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Response = response;
        RawRepresentation = response;
    }

    /// <summary>The complete typed System One response.</summary>
    public SystemOneResponse Response { get; }

    /// <summary>The model that answered the evaluation questions.</summary>
    public string Model => Response.Model;

    /// <summary>Token usage reported for the evaluation request.</summary>
    public TypeSafeUsage Usage => Response.Usage;

    /// <summary>The x-typesafe-request-id response header value, if attached by the client.</summary>
    public string? RequestId => Response.RequestId;

    /// <summary>Yes/no answers keyed by question id.</summary>
    public IReadOnlyDictionary<string, NoulAnswer> Nouls => Response.Nouls;

    /// <summary>Choice answers keyed by question id.</summary>
    public IReadOnlyDictionary<string, ChoiceAnswer> Choices => Response.Choices;

    /// <summary>Score answers keyed by question id.</summary>
    public IReadOnlyDictionary<string, ScoreAnswer> Scores => Response.Scores;

    /// <summary>All raw answers keyed by question id.</summary>
    public IReadOnlyDictionary<string, TypeSafeAnswer> Answers => Response.Answers;

    /// <summary>Produces a human-readable text summary of all evaluation answers.</summary>
    public string FormattedText => FormatSummary(Response);

    /// <inheritdoc />
    public override string ToString() => FormattedText;

    /// <summary>Formats a <see cref="SystemOneResponse"/> into a concise summary string.</summary>
    public static string FormatSummary(SystemOneResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var sb = new StringBuilder();
        foreach (var (id, answer) in response.Answers)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            switch (answer)
            {
                case NoulAnswer noul:
                    sb.Append(id).Append(": p=").Append(noul.Noul.ToString("F3", CultureInfo.InvariantCulture))
                      .Append(noul.Noul >= 0.5 ? " (yes)" : " (no)");
                    break;
                case ChoiceAnswer choice:
                    sb.Append(id).Append(": ").Append(choice.Choice)
                      .Append(" (conf=").Append(choice.Confidence.ToString("F3", CultureInfo.InvariantCulture)).Append(')');
                    break;
                case ScoreAnswer score:
                    sb.Append(id).Append(": score=").Append(score.Score.ToString("F2", CultureInfo.InvariantCulture))
                      .Append(" (conf=").Append(score.Confidence.ToString("F3", CultureInfo.InvariantCulture)).Append(')');
                    break;
                case UnknownAnswer unknown:
                    sb.Append(id).Append(": [").Append(unknown.Type).Append(']');
                    break;
                default:
                    sb.Append(id).Append(": [").Append(answer.ToString()).Append(']');
                    break;
            }
        }
        return sb.ToString();
    }
}
