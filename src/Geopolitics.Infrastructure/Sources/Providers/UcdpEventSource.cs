using System.Globalization;
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
    IOptions<ProviderOptions> options,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<UcdpEventSource> logger) : PollingEventSource(diagnostics, timeProvider, logger)
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

    protected override async Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Ucdp;
        var client = httpClientFactory.CreateClient(HttpClientName);
        var since = timeProvider.GetUtcNow().AddDays(-Math.Max(1, settings.DaysBack));

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(settings, since));

        // A header rather than a query parameter, which is UCDP's design and a helpful one: it keeps
        // the credential out of the request line that logging and proxies write down.
        request.Headers.Add(TokenHeader, settings.AccessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var events = UcdpResponseParser.Parse(body);

        LogRead(Logger, events.Count, since);

        return [.. events.Take(settings.MaxItemsPerPoll).Select(ToEnvelope)];
    }

    /// <summary>
    /// Composes the read query.
    /// <para>
    /// The resource and version are configuration rather than constants because UCDP versions its
    /// datasets and publishes the yearly and candidate series at different versions at the same time
    /// — GED Candidate is the monthly one, and pinning its version in code would mean a release
    /// requiring a build.
    /// </para>
    /// <para>
    /// <c>StartDate</c> filters on the event's end date, so this asks for events that finished on or
    /// after the window opens. Countries are Gleditsch and Ward numbers rather than names or ISO
    /// codes, which is why none are configured by default: a wrong number here is a silently wrong
    /// country, and guessing at one in a committed default would be exactly that.
    /// </para>
    /// </summary>
    private static Uri BuildRequestUri(UcdpProviderOptions settings, DateTimeOffset since)
    {
        var query = new List<string>
        {
            $"pagesize={Math.Max(1, settings.MaxItemsPerPoll).ToString(CultureInfo.InvariantCulture)}",
            $"StartDate={Uri.EscapeDataString(since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "UCDP returned {EventCount} event(s) ending on or after {Since}.")]
    private static partial void LogRead(ILogger logger, int eventCount, DateTimeOffset since);
}
