using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Default normalisation. Source-declared values are preferred because a structured provider knows
/// its own record better than any inference over its text; the classifier only fills genuine gaps.
/// </summary>
public sealed class ObservationNormaliser(IEventClassifier classifier) : IObservationNormaliser
{
    private const int MaxTitleLength = 300;
    private const int MaxSummaryLength = 4000;

    public RawObservation Normalise(ObservationEnvelope envelope, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var observation = new RawObservation(
            Guid.CreateVersion7(receivedAt),
            envelope.Kind,
            envelope.SourceName,
            envelope.Content,
            envelope.SourceIdentifier,
            receivedAt,
            envelope.IsDemo);

        // Classify once over title plus body so a headline-only signal is not lost.
        var classification = classifier.Classify($"{envelope.Title} {envelope.Content}");

        observation.ApplyNormalisation(
            BuildTitle(envelope),
            BuildSummary(envelope),
            envelope.DeclaredEventType ?? classification.EventType,
            envelope.DeclaredSeverity ?? classification.Severity,

            // An event time in the future is a provider clock problem, not evidence about the
            // world; clamp it so it cannot distort recency-weighted analytics.
            Min(envelope.OccurredAt ?? receivedAt, receivedAt),
            envelope.DeclaredLocationName);

        return observation;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static string BuildTitle(ObservationEnvelope envelope)
    {
        var title = string.IsNullOrWhiteSpace(envelope.Title)
            ? FirstSentence(envelope.Content)
            : envelope.Title.Trim();

        return Truncate(title, MaxTitleLength);
    }

    private static string BuildSummary(ObservationEnvelope envelope) =>
        Truncate(envelope.Content.Trim(), MaxSummaryLength);

    /// <summary>Derives a headline from body text when the source supplies none.</summary>
    private static string FirstSentence(string content)
    {
        var trimmed = content.Trim();
        var terminator = trimmed.AsSpan().IndexOfAny('.', '!', '?');

        if (terminator > 0)
        {
            trimmed = trimmed[..terminator];
        }

        return trimmed.Length == 0 ? "Untitled observation" : trimmed;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
}
