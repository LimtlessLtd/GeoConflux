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
    CorrelationLock correlationLock,
    CorroborationGate corroborationGate,
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
            // Lost a race against a concurrent processor holding the same payload. This is an
            // expected outcome, not an error: the read-then-insert check cannot be atomic, so the
            // unique index settles it and the loser is simply the duplicate it always was.
            diagnostics.ItemsDeduplicated.Add(1, new KeyValuePair<string, object?>("source", envelope.SourceName));
            LogConcurrentDuplicate(logger, envelope.SourceName, exception.Fingerprint);
            RecordDuration(startedAt, ProcessingOutcome.Duplicate);

            var recorded = await TryRetainConcurrentDuplicateAsync(observation, cancellationToken);
            return new ObservationProcessingResult(ProcessingOutcome.Duplicate, recorded, null, false, null);
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
        Guid? duplicateOf;

        using (var stage = diagnostics.StartStage(PipelineDiagnostics.Stages.Deduplicate))
        {
            duplicateOf = await observationRepository.FindByFingerprintAsync(observation.Fingerprint, cancellationToken);
            stage.Tag("observation.duplicate", duplicateOf is not null);
        }

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
        using (var stage = diagnostics.StartStage(PipelineDiagnostics.Stages.Enrich))
        {
            await EnrichAsync(envelope, observation, cancellationToken);
            stage.Tag("classification.method", observation.ClassificationMethod);
            stage.Tag("classification.confidence", observation.ClassificationConfidence);
        }

        // After enrichment, because the model's features include the category and confidence
        // enrichment produced. Before correlation, so the prediction is recorded against the
        // observation whether or not it ends up joining an incident.
        await RecordModelSeverityAsync(observation, cancellationToken);

        using (var stage = diagnostics.StartStage(PipelineDiagnostics.Stages.ResolveLocation))
        {
            await ResolveLocationAsync(envelope, observation, cancellationToken);
            stage.Tag("location.resolved", observation.Location is not null);
        }

        var incidentCreated = false;

        // Null means held: a claim nothing else supports, which is stored and shown and attached to
        // no incident. Modelled as the absence of an incident rather than as a flag beside one,
        // because every downstream step then has to acknowledge it to compile.
        GeopoliticalIncident? incident = null;
        var released = new List<RawObservation>();

        // Held from the candidate read through to the commit, and released there. Correlating
        // outside it would let a second worker holding a report of the same event read the same
        // candidates and open a second incident for it, because neither would see the other's
        // uncommitted write.
        //
        // Enrichment and location resolution are deliberately outside: they are the slow stages and
        // they touch no shared state, so serialising them would cost throughput and buy nothing.
        // Publication is outside for a sharper reason — see below.
        using (await correlationLock.AcquireAsync(observation.EventType, cancellationToken))
        {
            using (var correlateStage = diagnostics.StartStage(PipelineDiagnostics.Stages.Correlate))
            {
                var assessment = await correlator.CorrelateAsync(observation, cancellationToken);

                correlateStage.Tag("correlation.confidence", assessment.Confidence);
                correlateStage.Tag("correlation.matched", assessment.Incident is not null);

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
                    // The corroboration gate. A claim that matched no incident has nothing standing
                    // behind it yet, and one post is evidence that a post exists rather than
                    // evidence of an event. Published reporting skips this entirely: a wire item, a
                    // coded dataset record and a satellite detection each stand on their own.
                    var corroboration = observation.Attribution.IsClaim
                        ? await corroborationGate.AssessAsync(observation, cancellationToken)
                        : new CorroborationOutcome(observation, "published reporting stands on its own");

                    correlateStage.Tag("corroboration.required", observation.Attribution.IsClaim);
                    correlateStage.Tag("corroboration.satisfied", corroboration.IsCorroborated);

                    if (corroboration.IsCorroborated)
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
                    else
                    {
                        diagnostics.ClaimsHeld.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));
                        LogClaimHeld(logger, observation.SourceName, corroboration.Rationale);
                    }
                }

                if (incident is not null)
                {
                    // Held claims this incident now accounts for. Runs on a join as well as on a
                    // creation, because an incident that has just gained a location or an actor may
                    // account for a claim it did not a moment ago.
                    released = [.. await corroborationGate.ReleasableByAsync(incident, cancellationToken)];
                }

                correlateStage.Tag("incident.id", incident?.Id);
                correlateStage.Tag("incident.created", incidentCreated);
                correlateStage.Tag("claim.held", incident is null);
            }

            using (var persistStage = diagnostics.StartStage(PipelineDiagnostics.Stages.Persist))
            {
                if (incident is null)
                {
                    observation.HoldAsUncorroborated();
                    await observationRepository.AddAsync(observation, cancellationToken);
                    await observationRepository.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    observation.LinkToIncident(incident.Id);
                    observation.MarkPersisted();
                    await observationRepository.AddAsync(observation, cancellationToken);

                    foreach (var claim in released)
                    {
                        incident.LinkObservation(claim.Id, timeProvider.GetUtcNow());
                        incident.MergeEntities(claim.Entities, timeProvider.GetUtcNow());
                        claim.LinkToIncident(incident.Id);
                        claim.MarkPersisted();
                        LogClaimReleased(logger, claim.Id, incident.Id);
                    }

                    diagnostics.ClaimsReleased.Add(released.Count);

                    // One save so the observation, the incident, the claims it released, and every
                    // linkage between them commit together. Nothing is announced until it succeeds,
                    // so a released claim can never be shown as corroborated by an incident that
                    // failed to persist.
                    await incidentRepository.SaveChangesAsync(cancellationToken);
                    persistStage.Tag("incident.observation_count", incident.ObservationCount);
                }

                persistStage.Tag("claims.released", released.Count);
            }
        }

        diagnostics.ItemsProcessed.Add(1, new KeyValuePair<string, object?>("source", observation.SourceName));

        // Outside the lock, deliberately. This is a fan-out to every connected client, and its
        // duration is set by the slowest of them rather than by anything this pipeline controls.
        // Holding a per-category lock across it would let one stalled browser serialise the
        // processing of every later observation in that category. Nothing here can change what was
        // committed, so there is nothing left for the lock to protect.
        using (var stage = diagnostics.StartStage(PipelineDiagnostics.Stages.Publish))
        {
            if (incident is null)
            {
                // Announced like anything else. A held claim that clients never heard about would be
                // a claim a reader cannot see, and the point of holding rather than discarding is
                // that the post stays visible and visibly unsupported.
                await PublishObservationAsync(observation, cancellationToken);
            }
            else
            {
                await PublishAsync(observation, incident, incidentCreated, cancellationToken);

                foreach (var claim in released)
                {
                    // Re-announced so each released claim's label changes from an uncorroborated
                    // claim to part of an incident without the reader reloading the page.
                    await PublishObservationAsync(claim, cancellationToken);
                }
            }

            stage.Tag("incident.created", incidentCreated);
            stage.Tag("claim.held", incident is null);
        }

        return incident is null
            ? new ObservationProcessingResult(ProcessingOutcome.Held, observation.Id, null, false, null)
            : new ObservationProcessingResult(ProcessingOutcome.Persisted, observation.Id, incident.Id, incidentCreated, null);
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
        // Asking is already fallible, which is why this is guarded rather than read directly. In the
        // shipped model, readiness is what forces the lazy training to run, so it is the likeliest
        // member of the interface to throw — and it is not the pipeline's business whether some
        // future implementation answers cheaply or expensively.
        if (!IsModelReady(observation))
        {
            return;
        }

        using var stage = diagnostics.StartStage(PipelineDiagnostics.Stages.ScoreSeverity);

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

            diagnostics.ModelPredictions.Add(1, new KeyValuePair<string, object?>("model", prediction.ModelVersion));
            stage.Tag("model.version", prediction.ModelVersion);
            stage.Tag("model.severity", prediction.Severity.ToString());
            stage.Tag("model.disagrees", observation.ModelDisagrees);

            if (observation.ModelDisagrees)
            {
                diagnostics.ModelDisagreements.Add(
                    1,
                    new KeyValuePair<string, object?>("applied", observation.Severity.ToString()),
                    new KeyValuePair<string, object?>("predicted", prediction.Severity.ToString()));

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
            // Version is read through the same guard as readiness: on the shipped model it reaches
            // the same lazily trained value, so a failure there would otherwise be thrown a second
            // time from inside the handler meant to contain the first.
            diagnostics.ModelFailures.Add(1, new KeyValuePair<string, object?>("model", DescribeModel()));
            stage.Fail(exception.Message);
            LogModelPredictionFailed(logger, exception, observation.Id);
        }
    }

    /// <summary>
    /// Whether the model can be asked for an opinion, treating the question itself as fallible.
    /// Anything other than a plain "yes" means no second opinion and no other consequence.
    /// </summary>
    private bool IsModelReady(RawObservation observation)
    {
        try
        {
            return severityModel.IsReady;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.ModelFailures.Add(1, new KeyValuePair<string, object?>("model", "unavailable"));
            LogModelPredictionFailed(logger, exception, observation.Id);
            return false;
        }
    }

    /// <summary>The model's version for tagging, or a placeholder when even that cannot be read.</summary>
    private string DescribeModel()
    {
        try
        {
            return severityModel.Version;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "unavailable";
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
                    envelope.DeclaredCountryCode,
                    envelope.DeclaredPrecision),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLocationResolverFailed(logger, exception, observation.Id);
            resolution = LocationResolution.Failed("The location resolver was unavailable.");
        }

        if (resolution.Location is { } location)
        {
            observation.ResolveLocation(location, resolution.PrecisionNote);
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

            // Retained alone. Saving again through the ordinary path would re-attempt everything the
            // failed commit had staged, so a transient fault would commit an incident sourced
            // entirely from an observation this method is in the middle of marking as failed.
            await observationRepository.RetainEvidenceAsync(observation, cancellationToken);
            return observation.Id;
        }
        catch (Exception retentionException) when (retentionException is not OperationCanceledException)
        {
            LogRetentionFailed(logger, retentionException, observation.Id);
            return null;
        }
    }

    /// <summary>
    /// Keeps the delivery that lost the uniqueness race, recorded as the duplicate it turned out to
    /// be.
    /// <para>
    /// The sequential duplicate path stores its loser, and this one has to agree with it: whether a
    /// repeat was spotted by the read or by the index is a timing accident, and it should not decide
    /// whether the delivery is auditable afterwards. The filtered unique index exempts rows already
    /// marked as duplicates, which is what makes storing it possible at all.
    /// </para>
    /// </summary>
    private async Task<Guid?> TryRetainConcurrentDuplicateAsync(
        RawObservation? observation,
        CancellationToken cancellationToken)
    {
        if (observation is null)
        {
            return null;
        }

        try
        {
            // The winner has committed by now, so this finds it. If it somehow does not, there is no
            // original to point at and the delivery is left unrecorded rather than pointed at
            // nothing.
            var original = await observationRepository.FindByFingerprintAsync(observation.Fingerprint, cancellationToken);

            if (original is not { } originalId || originalId == observation.Id)
            {
                return null;
            }

            observation.MarkDuplicate(originalId);
            await observationRepository.RetainEvidenceAsync(observation, cancellationToken);
            return observation.Id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRetentionFailed(logger, exception, observation.Id);
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
        observation.Provenance,
        observation.ReceivedAt);

    /// <summary>An incident is as severe as the worst corroborated report about it.</summary>
    private static Severity Escalate(Severity current, Severity candidate) =>
        candidate > current ? candidate : current;


    private void RecordDuration(long startedAt, ProcessingOutcome outcome) =>
        diagnostics.ProcessingDuration.Record(
            timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()));

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Claim from {SourceName} is held as uncorroborated: {Reason}")]
    private static partial void LogClaimHeld(ILogger logger, string sourceName, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Held claim {ObservationId} was released into incident {IncidentId}.")]
    private static partial void LogClaimReleased(ILogger logger, Guid observationId, Guid incidentId);

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
