using System.Diagnostics;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// The single place the observation lifecycle is expressed. Stages are ordered so that evidence is
/// preserved before any fallible enrichment happens, and so that realtime publication is the last
/// step and can never affect what was committed.
/// </summary>
public sealed partial class ObservationProcessor(
    IObservationNormaliser normaliser,
    IObservationRepository observationRepository,
    IIncidentRepository incidentRepository,
    ILocationResolver locationResolver,
    IIncidentCorrelator correlator,
    IIncidentNotifier notifier,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<ObservationProcessor> logger) : IObservationProcessor
{
    public async Task<ObservationProcessingResult> ProcessAsync(ObservationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var startedAt = timeProvider.GetTimestamp();
        using var activity = diagnostics.ActivitySource.StartActivity("pipeline.process", ActivityKind.Consumer);
        activity?.SetTag("observation.source", envelope.SourceName);
        activity?.SetTag("observation.kind", envelope.Kind.ToString());

        RawObservation? observation = null;

        try
        {
            observation = normaliser.Normalise(envelope, timeProvider.GetUtcNow());
            activity?.SetTag("observation.id", observation.Id);
            activity?.SetTag("observation.fingerprint", observation.Fingerprint);

            var result = await RunStagesAsync(envelope, observation, cancellationToken);
            RecordDuration(startedAt, result.Outcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            // Shutdown is not a processing failure. Leave the observation unrecorded so the source
            // can redeliver it, rather than persisting a misleading failure row.
            throw;
        }
        catch (DuplicateObservationException exception)
        {
            // Lost a race against a concurrent processor holding the same payload.
            diagnostics.ItemsDeduplicated.Add(1, new KeyValuePair<string, object?>("source", envelope.SourceName));
            LogConcurrentDuplicate(logger, envelope.SourceName, exception.Fingerprint);
            RecordDuration(startedAt, ProcessingOutcome.Duplicate);
            return new ObservationProcessingResult(ProcessingOutcome.Duplicate, observation?.Id, null, false, null);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            diagnostics.ItemsFailedInPipeline.Add(1, new KeyValuePair<string, object?>("source", envelope.SourceName));
            LogProcessingFailed(logger, exception, envelope.SourceName);
            RecordDuration(startedAt, ProcessingOutcome.Failed);

            var retained = await TryRetainFailedObservationAsync(observation, exception, cancellationToken);
            return ObservationProcessingResult.Failed(retained, exception.Message);
        }
    }

    private async Task<ObservationProcessingResult> RunStagesAsync(
        ObservationEnvelope envelope,
        RawObservation observation,
        CancellationToken cancellationToken)
    {
        var duplicateOf = await observationRepository.FindByFingerprintAsync(observation.Fingerprint, cancellationToken);

        if (duplicateOf is { } originalId)
        {
            observation.MarkDuplicate(originalId);
            await observationRepository.AddAsync(observation, cancellationToken);
            await observationRepository.SaveChangesAsync(cancellationToken);

            diagnostics.ItemsDeduplicated.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));
            LogDuplicate(logger, observation.SourceName, originalId);

            await PublishObservationAsync(observation, cancellationToken);
            return new ObservationProcessingResult(ProcessingOutcome.Duplicate, observation.Id, null, false, null);
        }

        await ResolveLocationAsync(envelope, observation, cancellationToken);

        var assessment = await correlator.CorrelateAsync(observation, cancellationToken);
        var incidentCreated = false;
        GeopoliticalIncident incident;

        if (assessment.Incident is { } existing)
        {
            incident = existing;
            incident.LinkObservation(observation.Id, timeProvider.GetUtcNow());

            // A later report may carry a harsher assessment or coordinates the first one lacked.
            incident.Reassess(
                incident.EventType,
                Escalate(incident.Severity, observation.Severity),
                incident.Summary,
                timeProvider.GetUtcNow());

            if (incident.Location is null && observation.Location is not null)
            {
                // Copied, not aliased: the observation and the incident each own their location, and
                // sharing one instance across two owners makes the persistence layer track a single
                // object under two identities.
                incident.ResolveLocation(observation.Location.Copy(), timeProvider.GetUtcNow());
            }

            diagnostics.IncidentsCorrelated.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));
            LogCorrelated(logger, observation.Id, incident.Id, assessment.Confidence, assessment.Rationale);
        }
        else
        {
            incident = CreateIncident(observation);
            incident.LinkObservation(observation.Id, timeProvider.GetUtcNow());
            await incidentRepository.AddAsync(incident, cancellationToken);
            incidentCreated = true;

            diagnostics.IncidentsCreated.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));
            LogIncidentCreated(logger, incident.Id, observation.Id, assessment.Rationale);
        }

        observation.LinkToIncident(incident.Id);
        observation.MarkPersisted();
        await observationRepository.AddAsync(observation, cancellationToken);

        // One save so the observation, the incident, and their linkage commit together. Nothing is
        // announced to clients until this succeeds.
        await incidentRepository.SaveChangesAsync(cancellationToken);

        diagnostics.ItemsProcessed.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));

        await PublishAsync(observation, incident, incidentCreated, cancellationToken);

        return new ObservationProcessingResult(ProcessingOutcome.Persisted, observation.Id, incident.Id, incidentCreated, null);
    }

    /// <summary>
    /// Attempts coordinate resolution. Failure is expected and non-fatal: the observation stays in
    /// the system without a map position rather than being dropped or given invented coordinates.
    /// </summary>
    private async Task ResolveLocationAsync(
        ObservationEnvelope envelope,
        RawObservation observation,
        CancellationToken cancellationToken)
    {
        LocationResolution resolution;

        try
        {
            resolution = await locationResolver.ResolveAsync(
                new LocationResolutionRequest(
                    observation.LocationName,
                    envelope.DeclaredLatitude,
                    envelope.DeclaredLongitude,
                    envelope.DeclaredCountryCode),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLocationResolverFailed(logger, exception, observation.Id);
            resolution = LocationResolution.Failed("The location resolver was unavailable.");
        }

        if (resolution.Location is { } location)
        {
            observation.ResolveLocation(location);
            diagnostics.GeocodingSuccess.Add(1, new KeyValuePair<string, object?>("method", resolution.Method.ToString()));
        }
        else
        {
            observation.MarkLocationUnresolved(resolution.FailureReason ?? "No coordinates could be resolved.");
            diagnostics.GeocodingFailure.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));
        }
    }

    /// <summary>
    /// Best-effort realtime fan-out. Runs after the transaction has committed, so a notifier fault
    /// costs a client refresh at worst and never affects stored state.
    /// </summary>
    private async Task PublishAsync(
        RawObservation observation,
        GeopoliticalIncident incident,
        bool incidentCreated,
        CancellationToken cancellationToken)
    {
        var response = ToResponse(incident);

        try
        {
            await notifier.ObservationReceivedAsync(ObservationResponse.FromDomain(observation), cancellationToken);

            if (incidentCreated)
            {
                await notifier.IncidentCreatedAsync(response, cancellationToken);
            }
            else
            {
                await notifier.IncidentUpdatedAsync(response, cancellationToken);
            }

            diagnostics.Publications.Add(1, new KeyValuePair<string, object?>("kind", incidentCreated ? "created" : "updated"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.PublicationFailures.Add(1);
            LogPublicationFailed(logger, exception, incident.Id);
        }
    }

    private async Task PublishObservationAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.ObservationReceivedAsync(ObservationResponse.FromDomain(observation), cancellationToken);
            diagnostics.Publications.Add(1, new KeyValuePair<string, object?>("kind", "observation"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.PublicationFailures.Add(1);
            LogPublicationFailed(logger, exception, observation.Id);
        }
    }

    /// <summary>
    /// Keeps the source payload after a processing failure so the report can be diagnosed and
    /// reprocessed. If even this save fails the data is genuinely unrecoverable, and that is logged
    /// rather than hidden.
    /// </summary>
    private async Task<Guid?> TryRetainFailedObservationAsync(
        RawObservation? observation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (observation is null)
        {
            return null;
        }

        try
        {
            observation.MarkFailed(exception.Message);
            await observationRepository.AddAsync(observation, cancellationToken);
            await observationRepository.SaveChangesAsync(cancellationToken);
            return observation.Id;
        }
        catch (Exception retentionException) when (retentionException is not OperationCanceledException)
        {
            LogRetentionFailed(logger, retentionException, observation.Id);
            return null;
        }
    }

    private static GeopoliticalIncident CreateIncident(RawObservation observation) => new(
        Guid.CreateVersion7(observation.ReceivedAt),
        observation.Title ?? observation.SourceName,
        observation.Summary ?? observation.Content,
        observation.EventType,
        observation.Severity,
        observation.OccurredAt ?? observation.ReceivedAt,

        // Copied for the same reason as above: one owned value object, one owner.
        observation.Location?.Copy(),
        observation.IsDemo,
        observation.ReceivedAt);

    /// <summary>An incident is as severe as the worst corroborated report about it.</summary>
    private static Severity Escalate(Severity current, Severity candidate) =>
        candidate > current ? candidate : current;

    private static IncidentResponse ToResponse(GeopoliticalIncident incident) => new(
        incident.Id,
        incident.Title,
        incident.Summary,
        incident.EventType,
        incident.Severity,
        incident.OccurredAt,
        incident.Location is null
            ? null
            : new LocationResponse(
                incident.Location.Name,
                incident.Location.CountryCode,
                incident.Location.Latitude,
                incident.Location.Longitude),
        incident.ObservationCount,
        incident.IsDemo);

    private void RecordDuration(long startedAt, ProcessingOutcome outcome) =>
        diagnostics.ProcessingDuration.Record(
            timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Observation from {SourceName} duplicates observation {OriginalObservationId}.")]
    private static partial void LogDuplicate(ILogger logger, string sourceName, Guid originalObservationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Observation from {SourceName} lost a uniqueness race on fingerprint {Fingerprint}.")]
    private static partial void LogConcurrentDuplicate(ILogger logger, string sourceName, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Observation {ObservationId} correlated with incident {IncidentId} at confidence {Confidence}: {Rationale}")]
    private static partial void LogCorrelated(ILogger logger, Guid observationId, Guid incidentId, double confidence, string rationale);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {IncidentId} opened by observation {ObservationId}: {Rationale}")]
    private static partial void LogIncidentCreated(ILogger logger, Guid incidentId, Guid observationId, string rationale);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Location resolution failed for observation {ObservationId}; it will be stored without coordinates.")]
    private static partial void LogLocationResolverFailed(ILogger logger, Exception exception, Guid observationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing failed for an observation from {SourceName}.")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string sourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime publication failed after a successful save for {EntityId}.")]
    private static partial void LogPublicationFailed(ILogger logger, Exception exception, Guid entityId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not retain failed observation {ObservationId}; the source payload is lost.")]
    private static partial void LogRetentionFailed(ILogger logger, Exception exception, Guid observationId);
}
