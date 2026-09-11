using System.Collections.Frozen;
using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Deterministic, offline classifier used when a source declares no category. It is intentionally
/// simple: its purpose is to keep the pipeline useful and testable without credentials, and to act
/// as the fallback when AI enrichment is unavailable or produces output that fails validation.
/// Its confidence is reported honestly and is never presented as a model score.
/// </summary>
public sealed class KeywordEventClassifier : IEventClassifier
{
    /// <summary>Keyword to category. Ordered by specificity so narrower categories win.</summary>
    private static readonly (string Keyword, EventType EventType)[] TypeKeywords =
    [
        ("pirac", EventType.Piracy),

        // "pirates" does not contain "pirac", and it is the commonest word for this in reporting.
        // Found by an integration test that expected Piracy and got MaritimeIncident.
        ("pirate", EventType.Piracy),
        ("hijack", EventType.Piracy),
        ("boarded", EventType.Piracy),
        ("skiff", EventType.Piracy),
        ("naval", EventType.NavalIncident),
        ("warship", EventType.NavalIncident),
        ("frigate", EventType.NavalIncident),
        ("destroyer", EventType.NavalIncident),
        ("vessel", EventType.MaritimeIncident),
        ("tanker", EventType.MaritimeIncident),
        ("cargo ship", EventType.MaritimeIncident),
        ("shipping lane", EventType.MaritimeIncident),
        ("strait", EventType.MaritimeIncident),
        ("cyber", EventType.CyberIncident),
        ("ransomware", EventType.CyberIncident),
        ("malware", EventType.CyberIncident),
        ("data breach", EventType.CyberIncident),
        ("sanction", EventType.Sanctions),
        ("embargo", EventType.Sanctions),
        ("export control", EventType.Sanctions),
        ("terror", EventType.Terrorism),
        ("suicide bomb", EventType.Terrorism),
        ("ied", EventType.Terrorism),
        ("protest", EventType.Protest),
        ("demonstration", EventType.Protest),
        ("rally", EventType.Protest),
        ("strike action", EventType.Protest),
        ("troop", EventType.MilitaryMovement),
        ("deployment", EventType.MilitaryMovement),
        ("convoy", EventType.MilitaryMovement),
        ("mobilis", EventType.MilitaryMovement),
        ("mobiliz", EventType.MilitaryMovement),
        ("earthquake", EventType.NaturalHazard),
        ("wildfire", EventType.NaturalHazard),
        ("flood", EventType.NaturalHazard),
        ("thermal anomaly", EventType.NaturalHazard),
        ("cyclone", EventType.NaturalHazard),
        ("airstrike", EventType.Conflict),
        ("shelling", EventType.Conflict),
        ("clash", EventType.Conflict),
        ("offensive", EventType.Conflict),
        ("artillery", EventType.Conflict),
        ("ceasefire", EventType.Conflict),
    ];

    private static readonly FrozenSet<string> CriticalMarkers = FrozenSet.ToFrozenSet(
        ["mass casualt", "killed dozens", "nuclear", "chemical weapon", "state of emergency"],
        StringComparer.Ordinal);

    private static readonly FrozenSet<string> HighMarkers = FrozenSet.ToFrozenSet(
        ["killed", "fatalit", "casualt", "airstrike", "explosion", "hijack", "evacuat"],
        StringComparer.Ordinal);

    private static readonly FrozenSet<string> MediumMarkers = FrozenSet.ToFrozenSet(
        ["injur", "damage", "disrupt", "seiz", "detain", "blockad"],
        StringComparer.Ordinal);

    public EventClassification Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new EventClassification(EventType.Other, Severity.Unknown, 0, "keyword");
        }

        var haystack = text.ToLowerInvariant();
        var eventType = EventType.Other;
        var matches = 0;

        foreach (var (keyword, candidate) in TypeKeywords)
        {
            if (!haystack.Contains(keyword, StringComparison.Ordinal))
            {
                continue;
            }

            matches++;

            if (eventType == EventType.Other)
            {
                eventType = candidate;
            }
        }

        var severity = ClassifySeverity(haystack);

        // Confidence rises with corroborating keywords but is deliberately capped well below 1:
        // keyword matching is a heuristic and should never look like a calibrated model score.
        var confidence = eventType == EventType.Other
            ? 0.2
            : Math.Min(0.65, 0.35 + (0.1 * matches));

        return new EventClassification(eventType, severity, confidence, "keyword");
    }

    private static Severity ClassifySeverity(string haystack)
    {
        if (ContainsAny(haystack, CriticalMarkers))
        {
            return Severity.Critical;
        }

        if (ContainsAny(haystack, HighMarkers))
        {
            return Severity.High;
        }

        return ContainsAny(haystack, MediumMarkers) ? Severity.Medium : Severity.Low;
    }

    private static bool ContainsAny(string haystack, FrozenSet<string> markers)
    {
        foreach (var marker in markers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
