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

    public UcdpProviderOptions Ucdp { get; set; } = new();

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

    /// <summary>
    /// Root of the current ACLED API. The previous platform lived at <c>api.acleddata.com</c>, which
    /// was retired along with the key-and-email query shape and no longer resolves at all.
    /// </summary>
    public string BaseAddress { get; set; } = "https://acleddata.com/api/";

    /// <summary>
    /// OAuth token endpoint, as an absolute URL rather than a path under
    /// <see cref="BaseAddress"/>: it sits outside the API root, so deriving one from the other would
    /// be a guess that happens to be right today.
    /// </summary>
    public string TokenEndpoint { get; set; } = "https://acleddata.com/oauth/token";

    /// <summary>
    /// Email address of the registered ACLED account. Half of the credential and a personal
    /// identifier in its own right, so it is redacted from logs on both counts.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password for that account. Never committed; supplied per deployment.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client identifier. ACLED publishes a single shared value for all API consumers, so this
    /// is a protocol constant rather than a secret — it is configurable only so that a change on
    /// their side does not require a new build.
    /// </summary>
    public string ClientId { get; set; } = "acled";

    /// <summary>How far back to request events on each poll.</summary>
    public int DaysBack { get; set; } = 2;

    /// <summary>
    /// Country names to request, as ACLED spells them, for example <c>Ukraine</c>. Empty means no
    /// country filter, which is the whole dataset — a deliberate default of "ask for nothing in
    /// particular" rather than a hidden geographic scope baked into the application.
    /// </summary>
    public IList<string> Countries { get; } = [];
}

public sealed class UcdpProviderOptions : ProviderOptionsBase
{
    public UcdpProviderOptions()
    {
        // GED Candidate publishes monthly. Polling faster than daily returns the rows already seen,
        // and the API's allowance of 5,000 requests a day is not a reason to spend them.
        PollInterval = TimeSpan.FromHours(24);
    }

    public string BaseAddress { get; set; } = "https://ucdpapi.pcr.uu.se/api/";

    /// <summary>
    /// UCDP access token, requested from the maintainer by email. Supplied through environment
    /// variables or user secrets only; an empty value keeps the adapter dormant rather than producing
    /// failing requests.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Which UCDP resource to read. <c>gedevents</c> is the Georeferenced Event Dataset.
    /// </summary>
    public string Resource { get; set; } = "gedevents";

    /// <summary>
    /// Dataset version. Configuration rather than a constant because UCDP publishes the yearly and
    /// candidate series at different versions at the same time, and a monthly release should not
    /// require a build. <c>26.0.7</c> is GED Candidate, which is the one with under a month's lag.
    /// </summary>
    public string Version { get; set; } = "26.0.7";

    /// <summary>How far back to request events on each poll, against the event end date.</summary>
    public int DaysBack { get; set; } = 45;

    /// <summary>
    /// Countries to request, as Gleditsch and Ward numbers — not ISO codes and not names. Empty by
    /// default and deliberately so: a wrong number here is a silently wrong country, and a guessed
    /// one committed to this repository would be exactly that.
    /// </summary>
    public IList<int> Countries { get; } = [];
}
