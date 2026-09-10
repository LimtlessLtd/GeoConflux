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

    /// <summary>Whether ingestion sources are started by the host.</summary>
    public bool SourcesEnabled { get; set; } = true;

    /// <summary>Whether the background processor drains the queue in this host.</summary>
    public bool ProcessorEnabled { get; set; } = true;
}
