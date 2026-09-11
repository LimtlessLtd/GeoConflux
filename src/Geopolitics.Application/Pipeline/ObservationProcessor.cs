using System.Diagnostics;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IAiInferenceRepository inferenceRepository,
    IEventEnrichmentService enrichmentService,
    IOptions<EnrichmentOptions> enrichmentOptions,
    ISeverityModel severityModel,
    ILocationResolver locationResolver,
    IIncidentCorrelator correlator,
    CorrelationGate correlationGate,
    IIncidentNotifier notifier,
    PipelineDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<ObservationProcessor> logger) : IObservationProcessor
{
    private readonly EnrichmentOptions enrichment = enrichmentOptions.Value;

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

        // Enrichment runs after deduplication, not before. A re-delivery of a report that has
        // already been enriched would otherwise buy a second identical inference at full latency
        // and full provider cost for an observation the pipeline is about to stop processing.
        await EnrichAsync(envelope, observation, cancellationToken);

        // After enrichment, because the model's features include the category and confidence
        // enrichment produced. Before correlation, so the prediction is recorded against the
        // observation whether or not it ends up joining an incident.
        await RecordModelSeverityAsync(observation, cancellationToken);

        await ResolveLocationAsync(envelope, observation, cancellationToken);

        // Held from the candidate read through to the commit. Correlating outside it would let a
        // second worker holding a report of the same event read the same candidates and open a
        // second incident for it, because neither would see the other's uncommitted write.
        //
        // Enrichment and location resolution are deliberately outside: they are the slow stages and
        // they touch no shared state, so serialising them would cost throughput and buy nothing.
        using var gate = await correlationGate.AcquireAsync(observation.EventType, cancellationToken);

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

            // Adopted only if this report is better supported than what the incident already holds.
            incident.RecordAssessment(
                observation.ClassificationConfidence,
                observation.ClassificationMethod,
                timeProvider.GetUtcNow());

            // The incident's cast of actors is the union of what its evidence has named, so a later
            // report naming someone new widens it. This is also what lets the next observation be
            // scored on shared actors without reloading every observation behind the incident.
            incident.MergeEntities(observation.Entities, timeProvider.GetUtcNow());

            diagnostics.IncidentsCorrelated.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));
            LogCorrelated(logger, observation.Id, incident.Id, assessment.Confidence, assessment.Rationale);
        }
        else
        {
            incident = CreateIncident(observation);
            incident.LinkObservation(observation.Id, timeProvider.GetUtcNow());
            incident.MergeEntities(observation.Entities, timeProvider.GetUtcNow());
            incident.RecordAssessment(
                observation.ClassificationConfidence,
                observation.ClassificationMethod,
                timeProvider.GetUtcNow());
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
    /// Asks the trained model what it would have said, and records the answer.
    /// <para>
    /// Nothing downstream depends on the result. The prediction is stored beside the severity the
    /// pipeline acted on so the two can be compared later on real traffic, which a labelled corpus
    /// cannot tell you. It never changes a severity: a model fitted to a small synthetic corpus has
    /// no standing over a source that declared its own.
    /// </para>
    /// <para>
    /// Every failure path here is swallowed for the same reason. A second opinion that can fail the
    /// processing of an observation is not a second opinion, it is a new dependency — so an
    /// unavailable model, a slow one, or one that throws all produce the same outcome as a disabled
    /// one: no prediction, and the pipeline continues.
    /// </para>
    /// </summary>
    private async Task RecordModelSeverityAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        if (!severityModel.IsReady)
        {
            return;
        }

        try
        {
            var prediction = await severityModel.PredictAsync(
                SeverityFeatures.From(
                    observation.Title,
                    observation.Summary ?? observation.Content,
                    observation.EventType,
                    sourceCount: 1,
                    observation.ClassificationConfidence,
                    observation.Entities.Count,
                    observation.Location is not null),
                cancellationToken);

            if (prediction is null)
            {
                return;
            }

            observation.RecordModelSeverity(prediction.Severity, prediction.Confidence, prediction.ModelVersion);

            if (observation.ModelDisagrees)
            {
                // Logged rather than silently stored. A run in which the model disagrees with
                // everything is the signal that it has drifted from what the pipeline is seeing,
                // and it should be visible without anyone querying for it.
                LogModelDisagreement(
                    logger,
                    observation.Id,
                    observation.Severity,
                    prediction.Severity,
                    prediction.Confidence);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Not this stage's business to report.
            throw;
        }
        catch (Exception exception)
        {
            LogModelPredictionFailed(logger, exception, observation.Id);
        }
    }

    /// <summary>
    /// Runs semantic enrichment and decides whether to adopt the result.
    /// <para>
    /// Three things are true of every path through this method. The observation survives: a provider
    /// that is down, slow, or wrong costs quality, never evidence. The attempt is recorded: an
    /// <see cref="AiInference"/> row is written whether it succeeded or failed, so degraded
    /// classification is visible rather than silent. And the deterministic classification from
    /// normalisation stays in place unless something demonstrably better replaces it.
    /// </para>
    /// </summary>
    private async Task EnrichAsync(
        ObservationEnvelope envelope,
        RawObservation observation,
        CancellationToken cancellationToken)
    {
        var result = await enrichmentService.EnrichAsync(
            new EnrichmentRequest(observation.SourceName, observation.Title, observation.Content),
            cancellationToken);

        if (result.Outcome != AiInferenceOutcome.Skipped)
        {
            await RecordInferenceAsync(observation, result, cancellationToken);
        }

        if (result.Enrichment is not { } accepted)
        {
            // Nothing usable came back. The keyword classification recorded during normalisation
            // stands, and the observation continues through the pipeline unchanged.
            observation.MarkValidated();
            return;
        }

        if (accepted.Confidence < enrichment.MinimumAcceptedConfidence)
        {
            // The model answered and was honest about being unsure. A transparent heuristic that
            // says "keyword, 0.55" is more useful to a reader than an opaque model that says 0.2,
            // so the deterministic classification is kept and the inference stays on record.
            //
            // The factual extractions are kept, though. Low confidence in the *classification* says
            // nothing about whether the text is Arabic or names a real strait, and those are exactly
            // what lets an unreadable report still be placed on the map.
            observation.ApplyExtractions(accepted.Language, accepted.LocationName, accepted.Entities);
            LogEnrichmentBelowThreshold(logger, observation.Id, accepted.Confidence, enrichment.MinimumAcceptedConfidence);
            observation.MarkValidated();
            return;
        }

        // A structured provider stating its own category outranks an inference drawn from prose, so
        // a declared value is preserved and only the gaps are filled.
        var eventType = envelope.DeclaredEventType ?? accepted.EventType;
        var severity = envelope.DeclaredSeverity ?? accepted.Severity;
        var method = $"ai:{result.Provider}/{result.Model}";

        observation.ApplyEnrichment(
            accepted.Summary,
            eventType,
            severity,
            accepted.Confidence,
            envelope.DeclaredEventType is null ? method : $"source-declared+{method}",
            accepted.Language,
            accepted.SeverityRationale,
            accepted.LocationName,
            accepted.Entities);

        observation.MarkValidated();
        LogEnriched(logger, observation.Id, result.Provider, accepted.Confidence, result.Attempts);
    }

    /// <summary>
    /// Writes the audit record. Failing to store it must not fail the observation: the inference is
    /// telemetry about processing, and losing telemetry is a smaller loss than losing evidence.
    /// </summary>
    private async Task RecordInferenceAsync(
        RawObservation observation,
        EnrichmentResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var inference = result.IsSuccess
                ? AiInference.Succeeded(
                    Guid.CreateVersion7(timeProvider.GetUtcNow()),
                    observation.Id,
                    result.Provider,
                    result.Model,
                    result.PromptVersion,
                    result.SchemaVersion,
                    result.Enrichment!.Confidence,
                    result.Attempts,
                    result.LatencyMilliseconds,
                    timeProvider.GetUtcNow(),
                    result.StructuredOutput!)
                : AiInference.Failed(
                    Guid.CreateVersion7(timeProvider.GetUtcNow()),
                    observation.Id,
                    result.Provider,
                    result.Model,
                    result.PromptVersion,
                    result.SchemaVersion,
                    result.Outcome,
                    result.Attempts,
                    result.LatencyMilliseconds,
                    timeProvider.GetUtcNow(),
                    result.Error ?? "The enrichment attempt failed without a stated reason.");

            // Added, not saved: it commits with the observation and incident in the single
            // transaction below, so the audit trail cannot describe a row that was never stored.
            await inferenceRepository.AddAsync(inference, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogInferenceRecordFailed(logger, exception, observation.Id);
        }
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
        var response = IncidentResponse.FromDomain(incident);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Observation {ObservationId} enriched by {Provider} at confidence {Confidence} in {Attempts} attempt(s).")]
    private static partial void LogEnriched(ILogger logger, Guid observationId, string provider, double confidence, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrichment for observation {ObservationId} reported confidence {Confidence}, below the {Threshold} threshold; the deterministic classification was kept.")]
    private static partial void LogEnrichmentBelowThreshold(ILogger logger, Guid observationId, double confidence, double threshold);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Severity model disagreed on observation {ObservationId}: pipeline held {Applied}, model predicted {Predicted} at {Confidence:F2}.")]
    private static partial void LogModelDisagreement(
        ILogger logger,
        Guid observationId,
        Severity applied,
        Severity predicted,
        double confidence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The severity model could not score observation {ObservationId}; processing continued without a second opinion.")]
    private static partial void LogModelPredictionFailed(ILogger logger, Exception exception, Guid observationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the enrichment audit entry for observation {ObservationId}; processing continued.")]
    private static partial void LogInferenceRecordFailed(ILogger logger, Exception exception, Guid observationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Location resolution failed for observation {ObservationId}; it will be stored without coordinates.")]
    private static partial void LogLocationResolverFailed(ILogger logger, Exception exception, Guid observationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing failed for an observation from {SourceName}.")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string sourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime publication failed after a successful save for {EntityId}.")]
    private static partial void LogPublicationFailed(ILogger logger, Exception exception, Guid entityId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not retain failed observation {ObservationId}; the source payload is lost.")]
    private static partial void LogRetentionFailed(ILogger logger, Exception exception, Guid observationId);
}
