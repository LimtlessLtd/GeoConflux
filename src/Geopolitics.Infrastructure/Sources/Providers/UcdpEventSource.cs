using System.Globalization;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Polls the UCDP Georeferenced Event Dataset for coded conflict events.
/// <para>
/// UCDP is the strongest free complement to ACLED, and it is a complement rather than a substitute
/// for one specific reason: it publishes <c>where_prec</c>, an explicit statement of how precisely
/// each coordinate is known. A borrowed coordinate whose precision travels with it can be drawn
/// honestly, which is the difference between a map that shows what is known and one that shows what
/// is assumed. That is why the adapter carries it onto the envelope rather than declaring every
/// record exact.
/// </para>
/// <para>
/// The trade is latency. GED Candidate publishes monthly at under a month's lag, so this is a record
/// of what happened rather than a live feed — which is what it should be read as, and why the poll
/// interval is a day rather than minutes.
/// </para>
/// <para>
/// Internal for the same reason as the ACLED adapter: it holds a credential, and the redactor that
/// keeps that credential out of the logs is internal.
/// </para>
/// </summary>
internal sealed partial class UcdpEventSource(
    IHttpClientFactory httpClientFactory,
    IIngestionCheckpointStore checkpoints,
    IOptions<ProviderOptions> options,
    PipelineDiagnostics diagnostics,
    ISourceLivenessRecorder liveness,
    TimeProvider timeProvider,
    ILogger<UcdpEventSource> logger) : PollingEventSource(diagnostics, liveness, timeProvider, logger)
{
    public const string HttpClientName = "osint.ucdp";

    /// <summary>
    /// The header UCDP requires. Named here rather than inline because an unauthenticated request
    /// answers with exactly this instruction, which is how the name was confirmed.
    /// </summary>
    private const string TokenHeader = "x-ucdp-access-token";

    private readonly ProviderOptions options = options.Value;
    private readonly TimeProvider timeProvider = timeProvider;

    public override string Name => "ucdp";

    /// <summary>
    /// Requires a token as well as the enabled flag. UCDP answers a tokenless request with HTTP 401
    /// and a sentence of plain text, so without this the adapter would poll indefinitely and log
    /// parse failures rather than saying the one useful thing: no token was configured.
    /// </summary>
    protected override bool IsEnabled =>
        options.IsLive(options.Ucdp) && !string.IsNullOrWhiteSpace(options.Ucdp.AccessToken);

    protected override TimeSpan PollInterval => options.Ucdp.PollInterval;

    /// <summary>
    /// Reads the live window, then spends whatever request budget is left walking history backwards,
    /// exactly as the ACLED adapter does and for the same reasons.
    /// </summary>
    protected override async Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Ucdp;
        var now = timeProvider.GetUtcNow();
        var budget = new RequestBudget(settings.MaxRequestsPerPoll);
        var live = new DatasetWindow(now.AddDays(-Math.Max(1, settings.DaysBack)), now);

        var current = await DatasetHistory.ReadAsync(
            live, RequestAsync, budget, settings.MinimumWindow, Name, Logger, cancellationToken);

        var history = await DatasetBackfill.ReadAsync(
            settings, live.Start, RequestAsync, budget, checkpoints, Name, Logger, cancellationToken);

        LogRead(Logger, current.Envelopes.Count, history.Count, live.Start, budget.Remaining);

        return [.. current.Envelopes, .. history];

        async Task<DatasetWindowResult> RequestAsync(DatasetWindow window, CancellationToken token)
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(settings, window));

            // A header rather than a query parameter, which is UCDP's design and a helpful one: it
            // keeps the credential out of the request line that logging and proxies write down.
            request.Headers.Add(TokenHeader, settings.AccessToken);

            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(token);
            var page = UcdpResponseParser.ParsePage(body);

            return new DatasetWindowResult([.. page.Events.Select(ToEnvelope)], page.Truncated);
        }
    }

    /// <summary>
    /// Composes the read query for one window.
    /// <para>
    /// The resource and version are configuration rather than constants because UCDP versions its
    /// datasets and publishes the yearly and candidate series at different versions at the same time
    /// — GED Candidate is the monthly one, and pinning its version in code would mean a release
    /// requiring a build.
    /// </para>
    /// <para>
    /// <c>StartDate</c> and <c>EndDate</c> both filter on the event's <em>end</em> date, which is
    /// UCDP's documented behaviour and not an oversight in how this reads: the pair asks for events
    /// that had definitely finished within the window. The adapter previously sent only
    /// <c>StartDate</c>, which meant every request asked for everything from that date to the end of
    /// the dataset and there was no way to ask about a bounded slice of history at all.
    /// </para>
    /// <para>
    /// Countries are Gleditsch and Ward numbers rather than names or ISO codes, which is why none are
    /// configured by default: a wrong number here is a silently wrong country, and guessing at one in
    /// a committed default would be exactly that.
    /// </para>
    /// </summary>
    private static Uri BuildRequestUri(UcdpProviderOptions settings, DatasetWindow window)
    {
        var query = new List<string>
        {
            $"pagesize={Math.Max(1, settings.MaxItemsPerPoll).ToString(CultureInfo.InvariantCulture)}",
            $"StartDate={Uri.EscapeDataString(window.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            $"EndDate={Uri.EscapeDataString(window.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
        };

        var countries = settings.Countries
            .Where(country => country > 0)
            .Select(country => country.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        if (countries.Length > 0)
        {
            query.Add($"Country={Uri.EscapeDataString(string.Join(',', countries))}");
        }

        return new Uri(
            $"{Uri.EscapeDataString(settings.Resource)}/{Uri.EscapeDataString(settings.Version)}?{string.Join('&', query)}",
            UriKind.Relative);
    }

    private static ObservationEnvelope ToEnvelope(UcdpEvent record) => new()
    {
        SourceName = "ucdp",
        Kind = ObservationKind.ExternalEvent,
        Content = record.Notes,
        SourceIdentifier = record.Identifier,
        Title = record.Headline,
        OccurredAt = record.OccurredAt,
        DeclaredLocationName = record.LocationName,
        DeclaredLatitude = record.Latitude,
        DeclaredLongitude = record.Longitude,

        // The reason this adapter exists. UCDP states how well it located each event, and a record
        // coded to a provincial centroid must not be drawn as confidently as one coded to the event.
        DeclaredPrecision = record.Latitude is null ? null : record.Precision,
        DeclaredCountryCode = CountryCode(record.CountryName),

        // The register key, in the form the register uses. Prefixed with the project rather than
        // passed bare, so two coding projects numbering their conflicts from one cannot collide.
        DeclaredConflictKey = record.ConflictId is null ? null : $"ucdp:{record.ConflictId}",

        // Coded by hand at Uppsala, so stated rather than inferred, exactly as for ACLED.
        DeclaredEventType = record.EventType,
        DeclaredSeverity = record.Severity,
        Provenance = ObservationProvenance.Polled,
    };

    /// <summary>
    /// Turns UCDP's country name into the alpha-2 code the rest of the system uses, through the
    /// gazetteer that already maps names to codes. A name-to-code lookup, not a coordinate: ADR 005
    /// is untouched, and an unrecognised country yields no code rather than a guess.
    /// </summary>
    private static string? CountryCode(string? countryName) =>
        !string.IsNullOrWhiteSpace(countryName) && Gazetteer.TryResolve(countryName, out var entry)
            ? entry.CountryCode
            : null;

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UCDP returned {LiveCount} event(s) ending on or after {Since} and {HistoryCount} from backfill, with {RemainingRequests} request(s) left in this poll's budget.")]
    private static partial void LogRead(
        ILogger logger,
        int liveCount,
        int historyCount,
        DateTimeOffset since,
        int remainingRequests);
}
