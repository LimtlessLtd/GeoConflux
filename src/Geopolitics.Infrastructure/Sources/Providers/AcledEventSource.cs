using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Polls the ACLED read API for recently coded conflict events.
/// <para>
/// ACLED is the strongest structured source the system can take: its records are human-coded, carry
/// their own coordinates, and state a category and a fatality count. The adapter therefore declares
/// all of those on the envelope so the pipeline treats them as provider facts rather than re-deriving
/// weaker versions of them from the notes field. It is also the only source covering Ukraine, Yemen
/// and Ethiopia in one schema, which is why it comes first for theatre depth.
/// </para>
/// <para>
/// It is optional in the strict sense the specification requires. Without a credential the adapter
/// never starts, the application runs unchanged, and CI has nothing to skip.
/// </para>
/// <para>
/// Internal, unlike its RSS and FIRMS siblings, because it is the only adapter with a collaborator
/// that is itself internal: the token cache, which holds a credential and reaches the secret
/// redactor. Widening those two to public so that this one could stay public would be exposing the
/// credential-handling parts of the assembly to buy back a symmetry nothing depends on.
/// </para>
/// </summary>
internal sealed partial class AcledEventSource : PollingEventSource
{
    public const string HttpClientName = "osint.acled";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly AcledTokenProvider tokens;
    private readonly ProviderOptions options;
    private readonly TimeProvider timeProvider;

    public AcledEventSource(
        IHttpClientFactory httpClientFactory,
        AcledTokenProvider tokens,
        IOptions<ProviderOptions> options,
        PipelineDiagnostics diagnostics,
        TimeProvider timeProvider,
        ILogger<AcledEventSource> logger)
        : base(diagnostics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.httpClientFactory = httpClientFactory;
        this.tokens = tokens;
        this.options = options.Value;
        this.timeProvider = timeProvider;
    }

    public override string Name => "acled";

    /// <summary>
    /// A username and password, because the current API authenticates an account rather than a key.
    /// The old adapter checked for a key and an email; a deployment carrying that older configuration
    /// therefore stays dormant rather than issuing requests ACLED would reject.
    /// </summary>
    protected override bool IsEnabled =>
        options.IsLive(options.Acled)
        && !string.IsNullOrWhiteSpace(options.Acled.Username)
        && !string.IsNullOrWhiteSpace(options.Acled.Password);

    protected override TimeSpan PollInterval => options.Acled.PollInterval;

    protected override async Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Acled;
        var since = timeProvider.GetUtcNow().AddDays(-Math.Max(1, settings.DaysBack));
        var body = await ReadAsync(settings, since, cancellationToken);
        var events = AcledResponseParser.Parse(body);

        LogRead(Logger, events.Count, since);

        return [.. events.Take(settings.MaxItemsPerPoll).Select(ToEnvelope)];
    }

    /// <summary>
    /// Requests the events, re-authenticating once if the token is refused.
    /// <para>
    /// The retry exists because an access token can stop being honoured before its stated expiry —
    /// revoked, or invalidated by a change on the account — and the cached copy looks perfectly valid
    /// from here. Without it, such a token would be replayed on every poll until the process
    /// restarted, which for a six-hour interval is most of a day of silence. It is deliberately one
    /// retry: a second 401 means the credential itself is wrong, and hammering an authentication
    /// endpoint with a bad credential is how an account gets locked.
    /// </para>
    /// </summary>
    private async Task<string> ReadAsync(
        AcledProviderOptions settings,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings, since);

        for (var attempt = 0; ; attempt++)
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var token = await tokens.GetAccessTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized && attempt == 0)
            {
                LogTokenRejected(Logger);
                tokens.Invalidate();
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Composes the read query.
    /// <para>
    /// Country filtering is how this adapter is pointed at a theatre, and ACLED joins multiple values
    /// for one field with a pipe. The companion <c>country_where=%3D</c> asks for exact matches: the
    /// default for a text field is <c>LIKE</c>, which would quietly widen a request for one country
    /// into every country whose name contains it.
    /// </para>
    /// </summary>
    private static Uri BuildRequestUri(AcledProviderOptions settings, DateTimeOffset since)
    {
        var query = new List<string>
        {
            $"event_date={Uri.EscapeDataString(since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            "event_date_where=%3E%3D",
            $"limit={Math.Max(1, settings.MaxItemsPerPoll).ToString(CultureInfo.InvariantCulture)}",
            "_format=json",
        };

        var countries = settings.Countries
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country.Trim())
            .ToArray();

        if (countries.Length > 0)
        {
            query.Add($"country={Uri.EscapeDataString(string.Join('|', countries))}");
            query.Add("country_where=%3D");
        }

        return new Uri($"acled/read?{string.Join('&', query)}", UriKind.Relative);
    }

    private static ObservationEnvelope ToEnvelope(AcledEvent record)
    {
        var fatalities = record.Fatalities > 0
            ? string.Create(CultureInfo.InvariantCulture, $" Reported fatalities: {record.Fatalities}.")
            : string.Empty;

        return new ObservationEnvelope
        {
            SourceName = "acled",
            Kind = ObservationKind.ExternalEvent,
            Content = $"{record.Notes}{fatalities}",
            SourceIdentifier = record.Identifier,
            Title = record.Headline,
            OccurredAt = record.OccurredAt,
            DeclaredLocationName = record.LocationName,
            DeclaredLatitude = record.Latitude,
            DeclaredLongitude = record.Longitude,

            // Stated, not assumed. ACLED codes how precisely its coordinates locate the event, and
            // its best case is the coordinates of a town rather than of the event — so declaring
            // every record exact, as this adapter used to, overstated every one of them.
            DeclaredPrecision = record.Latitude is null ? null : record.Precision,
            DeclaredCountryCode = CountryCode(record.CountryName),

            // Coded by the provider, so stated rather than inferred. The pipeline keeps a declared
            // category over an enriched one, which is what makes this adapter worth having.
            DeclaredEventType = record.EventType,
            DeclaredSeverity = record.Severity,
            Provenance = ObservationProvenance.Polled,
        };
    }

    /// <summary>
    /// Turns ACLED's country name into the ISO 3166-1 alpha-2 code the rest of the system uses.
    /// <para>
    /// The previous adapter read an <c>iso3</c> field and then discarded anything that was not two
    /// characters, so it never produced a country code at all — and the current schema has no
    /// <c>iso3</c> field to read. What it does have is <c>country</c>, a name, and the gazetteer
    /// already maps names to codes for its country entries. This is a name-to-code lookup in the
    /// existing lexicon, not a coordinate: ADR 005 is untouched, and an unrecognised country still
    /// yields no code rather than a guess.
    /// </para>
    /// </summary>
    private static string? CountryCode(string? countryName) =>
        !string.IsNullOrWhiteSpace(countryName) && Gazetteer.TryResolve(countryName, out var entry)
            ? entry.CountryCode
            : null;

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACLED returned {EventCount} event(s) recorded since {Since}.")]
    private static partial void LogRead(ILogger logger, int eventCount, DateTimeOffset since);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACLED rejected the cached access token before its stated expiry; re-authenticating once.")]
    private static partial void LogTokenRejected(ILogger logger);
}
