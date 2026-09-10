namespace Geopolitics.Infrastructure.Sources;

/// <summary>Controls the recorded demo stream described in ADR 007.</summary>
public sealed class ReplayOptions
{
    public const string SectionName = "Replay";

    /// <summary>Whether the replay source is registered as an ingestion source.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Multiplier applied to recorded inter-arrival delays. Below 1 the demo runs faster;
    /// tests set it to 0 to remove wall-clock waits entirely.
    /// </summary>
    public double SpeedFactor { get; set; } = 1;

    /// <summary>Whether the recorded stream restarts once exhausted, for an unattended demo.</summary>
    public bool Loop { get; set; }

    /// <summary>Pause between passes when <see cref="Loop"/> is enabled.</summary>
    public TimeSpan LoopPause { get; set; } = TimeSpan.FromMinutes(2);
}
