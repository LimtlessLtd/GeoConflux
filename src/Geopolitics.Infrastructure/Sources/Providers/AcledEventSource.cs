using System.Globalization;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Polls the ACLED read API for recently coded conflict events.
/// <para>
/// ACLED is the strongest structured source the system can take: its records are human-coded, carry
/// their own coordinates, and state a category and a fatality count. The adapter therefore declares
/// all of those on the envelope so the pipeline treats them as provider facts rather than re-deriving
/// weaker versions of them from the notes field.
/// </para>
/// <para>
/// It is optional in the strict sense the specification requires. Without a credential the adapter
/// never starts, the application runs unchanged, and CI has nothing to skip.
/// </para>
/// </summary>
public sealed partial class AcledEventSource(
    IHttpClientFactory httpClientFactory,
    IOptions<ProviderOptions> options,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<AcledEventSource> logger) : PollingEventSource(diagnostics, timeProvider, logger)
{
    public const string HttpClientName = "osint.acled";

    private readonly ProviderOptions options = options.Value;
    private readonly TimeProvider timeProvider = timeProvider;

    public override string Name => "acled";

    protected override bool IsEnabled =>
        options.IsLive(options.Acled)
        && !string.IsNullOrWhiteSpace(options.Acled.ApiKey)
        && !string.IsNullOrWhiteSpace(options.Acled.Email);

    protected override TimeSpan PollInterval => options.Acled.PollInterval;

    protected override async Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Acled;
        var client = httpClientFactory.CreateClient(HttpClientName);
        var since = timeProvider.GetUtcNow().AddDays(-Math.Max(1, settings.DaysBack));

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["key"] = settings.ApiKey,
            ["email"] = settings.Email,
            ["event_date"] = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["event_date_where"] = ">=",
            ["limit"] = Math.Max(1, settings.MaxItemsPerPoll).ToString(CultureInfo.InvariantCulture),
        };

        var requestUri = new Uri(
            $"acled/read?{string.Join('&', query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"))}",
            UriKind.Relative);

        using var response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var events = AcledResponseParser.Parse(body);

        LogRead(Logger, events.Count, since);

        return [.. events.Take(settings.MaxItemsPerPoll).Select(ToEnvelope)];
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
            DeclaredCountryCode = record.CountryCode,

            // Coded by the provider, so stated rather than inferred. The pipeline keeps a declared
            // category over an enriched one, which is what makes this adapter worth having.
            DeclaredEventType = record.EventType,
            DeclaredSeverity = record.Severity,
            IsDemo = false,
        };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACLED returned {EventCount} event(s) recorded since {Since}.")]
    private static partial void LogRead(ILogger logger, int eventCount, DateTimeOffset since);
}
