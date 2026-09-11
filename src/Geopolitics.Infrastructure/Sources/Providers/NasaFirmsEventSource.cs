using System.Globalization;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Polls the NASA FIRMS area API for thermal anomaly detections.
/// <para>
/// This is the one source in the system that supplies its own coordinates, and they are authoritative:
/// a satellite instrument geolocating a pixel is a measurement, not an inference, which is exactly the
/// distinction ADR 005 draws. The envelope therefore declares its position and the resolver passes it
/// straight through instead of consulting the gazetteer.
/// </para>
/// <para>
/// What a detection is <em>not</em> is a geopolitical event. FIRMS reports heat, and the overwhelming
/// majority of it is agricultural burning, industrial flaring, or wildfire. Detections are therefore
/// declared as <see cref="EventType.NaturalHazard"/> at low severity and the text says plainly what
/// was measured, so nothing downstream can present a crop fire as a conflict indicator. Their value
/// here is corroborative: a thermal signature near an incident other sources are already reporting is
/// evidence, and the correlator is what decides whether it is.
/// </para>
/// </summary>
public sealed partial class NasaFirmsEventSource(
    IHttpClientFactory httpClientFactory,
    IOptions<ProviderOptions> options,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<NasaFirmsEventSource> logger) : PollingEventSource(diagnostics, timeProvider, logger)
{
    public const string HttpClientName = "osint.firms";

    private readonly ProviderOptions options = options.Value;

    public override string Name => "nasa-firms";

    /// <summary>
    /// Requires a key as well as the enabled flag. FIRMS answers a keyless request with HTTP 200 and
    /// an error sentence, so without this the adapter would poll indefinitely and log parse failures
    /// rather than saying the one useful thing: no key was configured.
    /// </summary>
    protected override bool IsEnabled =>
        options.IsLive(options.NasaFirms) && !string.IsNullOrWhiteSpace(options.NasaFirms.ApiKey);

    protected override TimeSpan PollInterval => options.NasaFirms.PollInterval;

    protected override async Task<IReadOnlyList<ObservationEnvelope>> FetchAsync(CancellationToken cancellationToken)
    {
        var settings = options.NasaFirms;
        var client = httpClientFactory.CreateClient(HttpClientName);
        var dayRange = Math.Clamp(settings.DayRange, 1, 10);

        // The key is a path segment in the FIRMS area API. It is escaped and never logged; the log
        // messages below name the dataset and area only.
        var requestUri = new Uri(
            $"api/area/csv/{Uri.EscapeDataString(settings.ApiKey)}/{Uri.EscapeDataString(settings.Dataset)}"
                + $"/{Uri.EscapeDataString(settings.Area)}/{dayRange.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative);

        using var response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var hotspots = FirmsCsvParser.Parse(body);
        var accepted = new List<ObservationEnvelope>();
        var belowConfidence = 0;

        foreach (var hotspot in hotspots.OrderByDescending(value => value.AcquiredAt))
        {
            if (hotspot.Confidence < settings.MinimumConfidence)
            {
                belowConfidence++;
                continue;
            }

            if (accepted.Count >= settings.MaxItemsPerPoll)
            {
                break;
            }

            accepted.Add(ToEnvelope(hotspot, settings.Dataset));
        }

        LogFiltered(Logger, settings.Dataset, settings.Area, hotspots.Count, accepted.Count, belowConfidence);
        return accepted;
    }

    private static ObservationEnvelope ToEnvelope(FirmsHotspot hotspot, string dataset)
    {
        var power = hotspot.RadiativePowerMegawatts is { } value
            ? string.Create(CultureInfo.InvariantCulture, $" Fire radiative power {value:F1} MW.")
            : string.Empty;

        var detection = string.Create(
            CultureInfo.InvariantCulture,
            $"Satellite thermal anomaly detected at {hotspot.Latitude:F4}, {hotspot.Longitude:F4} by {hotspot.Satellite} on {hotspot.AcquiredAt:u}.");

        var intensity = string.Create(
            CultureInfo.InvariantCulture,
            $"Brightness temperature {hotspot.BrightnessKelvin:F1} K at {hotspot.Confidence}% detection confidence.");

        // Worded as the measurement it is. A reader and an enrichment model both need to see that
        // this describes a thermal detection rather than a report of something happening.
        var content = $"{detection} {intensity}{power} A thermal detection records heat, not its cause.";

        return new ObservationEnvelope
        {
            SourceName = $"firms:{dataset.ToLowerInvariant()}",
            Kind = ObservationKind.Satellite,
            Content = content,
            SourceIdentifier = hotspot.Identifier,
            Title = string.Create(
                CultureInfo.InvariantCulture,
                $"Thermal anomaly at {hotspot.Latitude:F2}, {hotspot.Longitude:F2}"),
            OccurredAt = hotspot.AcquiredAt,

            // Measured by the instrument, so declared and authoritative.
            DeclaredLatitude = hotspot.Latitude,
            DeclaredLongitude = hotspot.Longitude,

            // Declared so that no enrichment step can reclassify a heat signature as a conflict.
            DeclaredEventType = EventType.NaturalHazard,
            DeclaredSeverity = Severity.Low,
            IsDemo = false,
        };
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "FIRMS dataset {Dataset} over {Area} returned {DetectionCount} detection(s): {AcceptedCount} accepted, {BelowConfidenceCount} below the confidence floor.")]
    private static partial void LogFiltered(
        ILogger logger,
        string dataset,
        string area,
        int detectionCount,
        int acceptedCount,
        int belowConfidenceCount);
}
