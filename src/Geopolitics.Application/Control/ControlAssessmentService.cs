using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.Extensions.Options;

namespace Geopolitics.Application.Control;

/// <summary>What an assessment came out as. Three of the four are refusals or qualifications.</summary>
public enum ControlVerdict
{
    /// <summary>Not enough evidence to say anything. The most common outcome, and a correct one.</summary>
    Insufficient = 0,

    /// <summary>One actor is asserted to hold the place, on evidence recent enough to stand.</summary>
    Assessed,

    /// <summary>Evidence supports more than one actor within the contest window. Never averaged.</summary>
    Contested,

    /// <summary>An actor was asserted, and the evidence is old enough that it describes the past.</summary>
    Stale,
}

/// <param name="ObservationId">The record itself, so the assertion can be checked rather than believed.</param>
/// <param name="Source">Who reported it.</param>
/// <param name="At">When the source says it happened.</param>
/// <param name="Signal">What kind of evidence it is.</param>
/// <param name="Basis">Where it came from — coded, claimed, or classified by a model.</param>
/// <param name="Actor">Who it is about.</param>
public sealed record ControlEvidence(
    Guid ObservationId,
    string Source,
    DateTimeOffset At,
    ControlSignal Signal,
    ControlEvidenceBasis Basis,
    string Actor);

/// <summary>
/// What is assessed about one place, with everything the assessment rests on attached.
/// </summary>
/// <param name="Place">The place, as the deterministic resolver named it.</param>
/// <param name="CountryCode">Where it turned out to be.</param>
/// <param name="Latitude">Null when the place has no usable coordinate.</param>
/// <param name="Longitude">Null when the place has no usable coordinate.</param>
/// <param name="Precision">How precisely the place is known. The map must not draw beyond it.</param>
/// <param name="Actor">Who is asserted to hold it, or null when contested or unassessed.</param>
/// <param name="Verdict">Which of the four answers this is.</param>
/// <param name="AsOf">The newest supporting evidence. The date the assertion is actually about.</param>
/// <param name="AgeDays">How old that is, so staleness is a number rather than an impression.</param>
/// <param name="Evidence">Every record behind this, newest first.</param>
/// <param name="SourceCount">How many distinct sources contributed.</param>
/// <param name="EvidenceIsDemo">
/// True when every record behind this came from the recorded demo stream. Said in the statement as
/// well as carried as a flag, because an assessment is a stronger artefact than an incident and a
/// reader may take "assessed to hold" at face value in a way they would not take a single report.
/// </param>
/// <param name="Statement">The assessment in a sentence, including when it is a refusal.</param>
public sealed record PlaceControl(
    string Place,
    string? CountryCode,
    double? Latitude,
    double? Longitude,
    LocationPrecision Precision,
    string? Actor,
    ControlVerdict Verdict,
    DateTimeOffset? AsOf,
    int AgeDays,
    IReadOnlyList<ControlEvidence> Evidence,
    int SourceCount,
    bool EvidenceIsDemo,
    string Statement);

/// <param name="Places">One entry per place with any control evidence, most recently evidenced first.</param>
/// <param name="PlacesWithEvidence">How many places had any evidence at all.</param>
/// <param name="PlacesAssessed">How many of those reached an assertion.</param>
/// <param name="Method">How these were arrived at, carried into the payload rather than implied.</param>
/// <param name="Note">What this report is and is not.</param>
public sealed record ControlReport(
    IReadOnlyList<PlaceControl> Places,
    int PlacesWithEvidence,
    int PlacesAssessed,
    string Method,
    string Note);

/// <summary>Assesses who holds what, from evidence, or declines to.</summary>
public interface IControlAssessmentService
{
    Task<ControlReport> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Turns control evidence into per-place assessments, and refuses where the evidence does not reach.
/// <para>
/// The four disciplines of [ADR 037] are each one decision here. <b>Evidence travels with the
/// assessment</b> — every place carries the observation identifiers behind it, not a count.
/// <b>Only positive evidence asserts</b> — silence produces staleness and never a continued
/// assertion. <b>Confidence decays with age</b> — past the staleness horizon an assessment reads as
/// last asserted rather than as held. <b>Below a floor it declines</b>, because a reader takes in the
/// claim and not the decimal beside it.
/// </para>
/// <para>
/// And one thing it deliberately does not do: nothing here interpolates between assessed places. A
/// polygon drawn through two assessed points asserts control of everything between them, which
/// nothing observed and nobody reported. Assessment is per place, and the absence of a line is a
/// property of the model rather than a rule somebody has to remember.
/// </para>
/// </summary>
public sealed class ControlAssessmentService(
    IControlRepository repository,
    IOptions<ControlOptions> options,
    TimeProvider timeProvider) : IControlAssessmentService
{
    private readonly ControlOptions options = options.Value;

    private const string MethodStatement =
        "Assessed per place from reports that say who holds it, never from reports of fighting. "
        + "Coded territorial change asserts on its own authority; an uncorroborated claim does not. "
        + "Nothing is interpolated between assessed places, so this is not a front line.";

    public async Task<ControlReport> BuildAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var signals = await repository.ListSignalsAsync(now - options.Window, cancellationToken);

        var places = signals
            .GroupBy(signal => PlaceKey(signal), StringComparer.OrdinalIgnoreCase)
            .Select(group => Assess(group, now))
            .OrderByDescending(place => place.AsOf ?? DateTimeOffset.MinValue)
            .ThenBy(place => place.Place, StringComparer.Ordinal)
            .ToList();

        places = [.. places.Select(Label)];

        var assessed = places.Count(place => place.Verdict is ControlVerdict.Assessed or ControlVerdict.Contested);

        return new ControlReport(
            places,
            places.Count,
            assessed,
            MethodStatement,
            Note(places.Count, assessed));
    }

    /// <summary>
    /// Adds the synthetic-evidence sentence where it applies.
    /// <para>
    /// Appended after the assessment rather than woven into it, so that every branch above — assert,
    /// contest, stale, decline — gets it without each one having to remember. A statement is the only
    /// part of this a reader is certain to read, and the demo notice elsewhere on the page is about
    /// the page rather than about this claim.
    /// </para>
    /// </summary>
    private static PlaceControl Label(PlaceControl place) => place.EvidenceIsDemo
        ? place with
        {
            Statement = $"{place.Statement} Every record behind this is synthetic replay data, so "
                + "this assessment demonstrates the method and describes nothing real.",
        }
        : place;

    /// <summary>
    /// Grouped on the resolved place and its country rather than on the name alone, because the
    /// gazetteer holds a great many places sharing a name and merging two of them would assess one
    /// war's evidence onto another continent.
    /// </summary>
    private static string PlaceKey(ControlObservation signal) =>
        $"{signal.CountryCode ?? "??"}|{signal.PlaceName}";

    private PlaceControl Assess(IEnumerable<ControlObservation> group, DateTimeOffset now)
    {
        var evidence = group
            .OrderByDescending(signal => signal.OccurredAt)
            .ToList();

        var first = evidence[0];
        var records = evidence
            .Select(signal => new ControlEvidence(
                signal.ObservationId, signal.SourceName, signal.OccurredAt, signal.Signal, signal.Basis, signal.Actor))
            .ToList();

        var sources = evidence.Select(signal => signal.SourceName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Withdrawal is read out of the assertion set rather than into it: it is the only signal that
        // can end an assessment without another actor making one, and a picture that could only ever
        // gain control claims would drift permanently towards whoever was reported first.
        var asserting = evidence.Where(signal => signal.Signal != ControlSignal.WithdrawalReported).ToList();

        var place = new PlaceControl(
            first.PlaceName,
            first.CountryCode,
            first.Latitude,
            first.Longitude,
            first.Precision,
            null,
            ControlVerdict.Insufficient,
            first.OccurredAt,
            Age(first.OccurredAt, now),
            records,
            sources,
            evidence.All(signal => signal.IsDemo),
            string.Empty);

        if (asserting.Count == 0)
        {
            return place with
            {
                Statement = "Reported as left or withdrawn from, with nothing asserting who holds it now.",
            };
        }

        var newest = asserting[0];

        if (!CanAssert(asserting))
        {
            return place with
            {
                Statement = Insufficient(asserting.Count, sources),
            };
        }

        // Contested is decided on the newest evidence rather than on the whole window. A place that
        // changed hands in January and again in March has changed hands; a place claimed by two
        // actors inside a fortnight is being fought over, and picking the later claim would present
        // a snapshot of a moving thing as a settled fact.
        var contesting = asserting
            .Where(signal => newest.OccurredAt - signal.OccurredAt <= options.ContestWindow)
            .Select(signal => signal.Actor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (contesting.Count > 1)
        {
            return place with
            {
                Verdict = ControlVerdict.Contested,
                AsOf = newest.OccurredAt,
                AgeDays = Age(newest.OccurredAt, now),
                Statement = $"Contested: {string.Join(" and ", contesting.Order(StringComparer.Ordinal))} were each "
                    + $"reported to hold this within {options.ContestWindow.TotalDays:F0} days. Both are shown; "
                    + "neither is preferred.",
            };
        }

        var age = Age(newest.OccurredAt, now);
        var stale = now - newest.OccurredAt > options.StaleAfter;

        return place with
        {
            Actor = newest.Actor,
            Verdict = stale ? ControlVerdict.Stale : ControlVerdict.Assessed,
            AsOf = newest.OccurredAt,
            AgeDays = age,
            Statement = stale
                ? $"{newest.Actor} was last asserted to hold this {age} days ago. Nothing since says whether "
                    + "that still holds, and silence is not evidence that it does."
                : $"{newest.Actor} is assessed to hold this, on {Describe(records.Count)} from "
                    + $"{Describe(sources, "source")}, newest {age} days old.",
        };
    }

    /// <summary>
    /// Whether this place's evidence can assert anything at all.
    /// <para>
    /// One coded record is enough, because a named organisation's coder working to published criteria
    /// has already made this assessment and relaying it is aggregation. Anything else needs the
    /// corroboration gate's number of independent sources, for the corroboration gate's reason.
    /// </para>
    /// </summary>
    private bool CanAssert(IReadOnlyList<ControlObservation> asserting)
    {
        if (asserting.Any(signal => signal.Basis == ControlEvidenceBasis.Coded))
        {
            return true;
        }

        return asserting
            .Select(signal => signal.SourceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() >= options.MinimumClaimSources;
    }

    private static string Insufficient(int records, int sources) =>
        $"{Describe(records)} from {Describe(sources, "source")}; too little to assess. Nothing here is "
        + "coded by a conflict-coding project, and an uncorroborated claim that a place has fallen is "
        + "evidence that somebody said so.";

    private static int Age(DateTimeOffset at, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Round((now - at).TotalDays));

    private static string Describe(int count, string noun = "report") =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    /// <summary>
    /// What the report as a whole is, said beside it.
    /// <para>
    /// The empty case is the one that ships and the one that must not read as peace. A clone with no
    /// dataset credential polls nothing that codes territorial change, so it assesses nothing — which
    /// is a statement about this system's reach and not about the world's wars.
    /// </para>
    /// </summary>
    private static string Note(int withEvidence, int assessed) => withEvidence == 0
        ? "No report reaching this system said anything about who holds anywhere. That is a statement "
            + "about what was collected, not about the world: control is assertable only from reports "
            + "that are about control, and coded territorial change arrives only with a dataset "
            + "credential this deployment does not have."
        : $"{Describe(withEvidence, "place")} had control evidence and {assessed} reached an assertion. "
            + "The rest are shown with what they have and why it was not enough. Absence of a place "
            + "here means nothing reported about who holds it, not that nobody does.";
}
