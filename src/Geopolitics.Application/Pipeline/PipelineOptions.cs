namespace Geopolitics.Application.Pipeline;

/// <summary>Tunable behaviour of the ingestion and processing pipeline.</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Maximum envelopes held in the queue before producers are made to wait.</summary>
    public int QueueCapacity { get; set; } = 512;

    /// <summary>Number of observations processed concurrently by the background processor.</summary>
    public int ProcessorConcurrency { get; set; } = 2;

    /// <summary>
    /// How far back the correlator looks for a matching incident. Reports of the same event arrive
    /// from different outlets over hours, so a window materially shorter than this fragments
    /// one real-world event into several incidents.
    /// </summary>
    public TimeSpan CorrelationWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How close two located reports must be to count as the same event. Roughly a metropolitan
    /// area or a stretch of shipping lane.
    /// </summary>
    public double CorrelationRadiusKilometres { get; set; } = 75;

    /// <summary>
    /// How strong a correlation match has to be before an observation joins an existing incident
    /// rather than opening a new one.
    /// <para>
    /// The number to turn up when incidents are being merged that should not be. A wrong merge
    /// destroys the distinction between two events and is hard to notice afterwards; two incidents
    /// that should have been one are obvious on the map, so this errs high on purpose.
    /// </para>
    /// </summary>
    public double MinimumCorrelationConfidence { get; set; } = 0.45;

    /// <summary>
    /// How much two reports must agree in wording before that agreement counts as corroboration.
    /// <para>
    /// This is the threshold the specification calls <c>SemanticSimilarityThreshold</c>. What the
    /// default measure actually compares is shared vocabulary, not meaning, so the bar is set where
    /// two write-ups of one event clear it and two reports that merely share a topic do not.
    /// </para>
    /// </summary>
    public double SemanticSimilarityThreshold { get; set; } = 0.4;

    /// <summary>
    /// Ceiling on confidence when co-location rests on two reports naming the same place rather than
    /// on measured coordinates. A shared name can cover a whole city or a whole strait, so it is
    /// real evidence and weak evidence at the same time, and the ceiling is what says so.
    /// </summary>
    public double PlaceNameConfidence { get; set; } = 0.55;

    /// <summary>
    /// Ceiling on confidence when neither report can be placed at all and the match rests entirely
    /// on shared actors and shared wording. Higher than a place-name match looks wrong until you
    /// notice what it takes to get here: the correlator demands both signals, so this path only
    /// opens for two write-ups that name the same people in near-identical terms.
    /// </summary>
    public double ContentOnlyConfidence { get; set; } = 0.60;

    /// <summary>
    /// How far the corroborating signals are allowed to pull a match down from its positional
    /// ceiling. At 0.6 a match with no corroboration at all keeps 60% of its ceiling and full
    /// corroboration keeps all of it.
    /// <para>
    /// A floor rather than a free multiplier because the positional signal has already established
    /// that these two reports are about the same place at nearly the same time. Weak agreement in
    /// wording after that is a reason for slightly less confidence, not for dismissal.
    /// </para>
    /// </summary>
    public double SupportFloor { get; set; } = 0.6;

    /// <summary>
    /// Relative weights of the corroborating signals: elapsed time, shared actors, and shared
    /// wording. They do not need to sum to one, because the corroboration score is a weighted mean
    /// over whichever of them were available for that particular comparison.
    /// <para>
    /// Time is weakest on purpose. Everything reaching this point is already inside the correlation
    /// window, so time discriminates least among the candidates that survive.
    /// </para>
    /// </summary>
    public double EntityWeight { get; set; } = 0.20;

    public double SimilarityWeight { get; set; } = 0.20;

    public double TimeWeight { get; set; } = 0.15;

    /// <summary>Whether ingestion sources are started by the host.</summary>
    public bool SourcesEnabled { get; set; } = true;

    /// <summary>Whether the background processor drains the queue in this host.</summary>
    public bool ProcessorEnabled { get; set; } = true;
}
