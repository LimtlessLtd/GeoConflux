namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>Whether the host is allowed to reach external networks for observations.</summary>
public enum ProviderMode
{
    /// <summary>Recorded data only. No external call is made, and no credential is required.</summary>
    Demo = 0,

    /// <summary>Individually enabled live adapters may poll their upstream providers.</summary>
    Live,
}

/// <summary>
/// Configuration for the live OSINT adapters.
/// <para>
/// Two switches must agree before any network call happens: the deployment must be in
/// <see cref="ProviderMode.Live"/> and the individual provider must be enabled. A single global
/// switch would make it too easy for a demo deployment to start reaching the internet because one
/// provider block was copied in from an example; requiring both means the default configuration
/// shipped in this repository cannot call out no matter which provider section is filled in.
/// </para>
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    /// <summary>
    /// Demo by default, so a clone of this repository runs offline with no credentials, exactly as
    /// the specification requires.
    /// </summary>
    public ProviderMode Mode { get; set; } = ProviderMode.Demo;

    public RssProviderOptions Rss { get; set; } = new();

    public FirmsProviderOptions NasaFirms { get; set; } = new();

    public AcledProviderOptions Acled { get; set; } = new();

    /// <summary>True when this provider may actually poll: live mode and the provider both enabled.</summary>
    public bool IsLive(ProviderOptionsBase provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Mode == ProviderMode.Live && provider.Enabled;
    }
}

/// <summary>Settings every polling adapter needs, regardless of what it talks to.</summary>
public abstract class ProviderOptionsBase
{
    /// <summary>Disabled by default. Enabling one provider must never enable another.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long to wait between polls.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Upper bound on envelopes emitted from a single poll. A provider that suddenly returns its
    /// entire archive should cost one truncated batch, not a saturated queue.
    /// </summary>
    public int MaxItemsPerPoll { get; set; } = 50;
}

public sealed class RssProviderOptions : ProviderOptionsBase
{
    /// <summary>
    /// Feeds to poll. Empty by default: no publisher is baked into the application, so the choice of
    /// sources is a deployment decision rather than an architectural dependency.
    /// </summary>
    public IList<RssFeedOptions> Feeds { get; } = [];
}

/// <param name="Name">Short label used in the source name and in logs, for example <c>world-news</c>.</param>
public sealed class RssFeedOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute URL of an RSS 2.0 or Atom feed.</summary>
    public string Url { get; set; } = string.Empty;
}

public sealed class FirmsProviderOptions : ProviderOptionsBase
{
    public FirmsProviderOptions()
    {
        // Thermal anomalies are published continuously but a given pixel is only revisited a few
        // times a day, so polling faster than this returns the rows already seen.
        PollInterval = TimeSpan.FromHours(1);
    }

    public string BaseAddress { get; set; } = "https://firms.modaps.eosdis.nasa.gov/";

    /// <summary>
    /// NASA FIRMS map key. Supplied through environment variables or user secrets only; an empty
    /// value keeps the adapter dormant rather than producing failing requests.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>FIRMS dataset identifier, for example <c>VIIRS_NOAA20_NRT</c>.</summary>
    public string Dataset { get; set; } = "VIIRS_NOAA20_NRT";

    /// <summary>Area filter as <c>west,south,east,north</c>, or <c>world</c> for the global feed.</summary>
    public string Area { get; set; } = "world";

    /// <summary>How many days of detections to request, 1-10 per the FIRMS area API.</summary>
    public int DayRange { get; set; } = 1;

    /// <summary>
    /// Minimum detection confidence to accept, 0-100. FIRMS publishes low-confidence pixels that are
    /// frequently sun glint or cloud edges, and a dashboard that plots them is showing noise.
    /// </summary>
    public int MinimumConfidence { get; set; } = 50;
}

public sealed class AcledProviderOptions : ProviderOptionsBase
{
    public AcledProviderOptions() => PollInterval = TimeSpan.FromHours(6);

    public string BaseAddress { get; set; } = "https://api.acleddata.com/";

    /// <summary>ACLED access key. Never committed; supplied per deployment.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Email registered with ACLED, which their API requires alongside the key.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>How far back to request events on each poll.</summary>
    public int DaysBack { get; set; } = 2;
}
