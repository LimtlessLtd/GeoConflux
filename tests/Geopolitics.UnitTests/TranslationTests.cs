using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;

namespace Geopolitics.UnitTests;

/// <summary>
/// The defect these pin down: the dashboard decided that a translation had happened by looking at
/// the language tag, and the language tag says nothing about the matter. It is normally copied off
/// the feed at intake, before any model has seen the text, and the provider that ships by default
/// states plainly that it cannot translate. A reader was shown untranslated Arabic under a label
/// reading "ar to en".
/// <para>
/// So the pipeline records what the provider says it did, the source text is kept rather than
/// overwritten, and neither can be inferred from the other.
/// </para>
/// </summary>
public sealed class TranslationTests
{
    [Fact]
    public void AnObservationIsUntranslatedUntilSomethingTranslatesIt()
    {
        var observation = Observe();

        Assert.Equal(TranslationState.NotTranslated, observation.Translation);
        Assert.False(observation.HasEnglishText);
        Assert.Null(observation.TranslatedTitle);
        Assert.Null(observation.TranslationMethod);
    }

    [Fact]
    public void ATranslationKeepsTheSourceTextRatherThanReplacingIt()
    {
        var observation = Observe("اشتباكات في المنطقة الساحلية أسفرت عن إصابات.");

        observation.RecordTranslation(
            TranslationState.MachineTranslated,
            "Clashes in the coastal district",
            "Clashes were reported in the coastal district, with injuries.",
            "ai:ollama/llama3.2");

        // The original is what a reader checks a translated claim against, so losing it would defeat
        // the point of translating at all.
        Assert.Equal("اشتباكات في المنطقة الساحلية أسفرت عن إصابات.", observation.Content);
        Assert.Equal("Clashes in the coastal district", observation.TranslatedTitle);
        Assert.Equal("ai:ollama/llama3.2", observation.TranslationMethod);
        Assert.True(observation.HasEnglishText);
    }

    [Fact]
    public void ATranslationMustNameWhatProducedIt()
    {
        var observation = Observe();

        var error = Assert.Throws<DomainException>(() => observation.RecordTranslation(
            TranslationState.MachineTranslated,
            "An English headline",
            "An English summary.",
            method: null));

        Assert.Contains("what produced it", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATranslationThatCarriesNoEnglishIsRefused()
    {
        // Otherwise the record would assert that a translation exists while holding none, which puts
        // the dashboard back where it started: promising English and rendering the source language.
        var observation = Observe();

        Assert.Throws<DomainException>(() => observation.RecordTranslation(
            TranslationState.MachineTranslated,
            translatedTitle: "   ",
            translatedSummary: null,
            method: "ai:ollama/llama3.2"));
    }

    [Fact]
    public void ARecordedTranslationCanBeWithdrawn()
    {
        var observation = Observe();
        observation.RecordTranslation(TranslationState.MachineTranslated, "A headline", "A summary.", "ai:test/model");

        observation.RecordTranslation(TranslationState.NotTranslated, "A headline", "A summary.", "ai:test/model");

        // Nothing survives a state that says nothing was translated, or the fields would outlive the
        // claim that justified them.
        Assert.Equal(TranslationState.NotTranslated, observation.Translation);
        Assert.Null(observation.TranslatedTitle);
        Assert.Null(observation.TranslatedSummary);
        Assert.Null(observation.TranslationMethod);
    }

    [Fact]
    public void AnOverlongTranslatedTitleIsTruncatedRatherThanRejected()
    {
        var observation = Observe();

        observation.RecordTranslation(
            TranslationState.MachineTranslated,
            new string('x', RawObservation.MaxTranslatedTitleLength + 50),
            "A summary.",
            "ai:test/model");

        Assert.Equal(RawObservation.MaxTranslatedTitleLength, observation.TranslatedTitle!.Length);
    }

    [Fact]
    public async Task AProviderThatTranslatesHasItsEnglishRecordedAndAttributed()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            summary: "A cargo vessel was approached by small craft.",
            language: "ar",
            translated: true,
            translatedTitle: "Cargo vessel approached near Bab-el-Mandeb");

        var processor = harness.BuildProcessor();
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("سفينة شحن اقتربت منها زوارق صغيرة.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        var stored = harness.Observations.Committed[0];

        Assert.Equal(TranslationState.MachineTranslated, stored.Translation);
        Assert.Equal("Cargo vessel approached near Bab-el-Mandeb", stored.TranslatedTitle);
        Assert.Equal("A cargo vessel was approached by small craft.", stored.TranslatedSummary);
        Assert.StartsWith("ai:", stored.TranslationMethod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderThatCannotTranslateLeavesTheObservationMarkedUntranslated()
    {
        // This is the deterministic provider's case, and therefore the published dashboard's case.
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            summary: "[Mock enrichment — not translated] The source text is in 'ar'.",
            language: "ar",
            translated: false);

        var processor = harness.BuildProcessor();
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("سفينة شحن اقتربت منها زوارق صغيرة.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        var stored = harness.Observations.Committed[0];

        Assert.Equal(TranslationState.NotTranslated, stored.Translation);
        Assert.Null(stored.TranslatedTitle);
        Assert.False(stored.HasEnglishText);
    }

    [Fact]
    public async Task AnEnglishSourceIsRecordedAsNeedingNoTranslation()
    {
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(language: "en", translated: false);

        var processor = harness.BuildProcessor();
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("A vessel was boarded near the strait.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        var stored = harness.Observations.Committed[0];

        // Distinct from NotTranslated: this is a complete answer, and the dashboard shows no caveat
        // for it. Treating the two alike would put a warning on most of the feed.
        Assert.Equal(TranslationState.AlreadyEnglish, stored.Translation);
        Assert.True(stored.HasEnglishText);
    }

    [Fact]
    public async Task ASourceThatDeclaredItsLanguageIsNotOverruledByAProviderThatOnlyReadsScripts()
    {
        // Found on the published run, which stored 74 French and Spanish reports as needing no
        // translation. The deterministic provider identifies the script and nothing finer, so it
        // answers "en" for anything in Latin letters. A language the source stated is a fact about
        // the record and outranks that guess, which is the precedence AdoptDetectedLanguage already
        // applies -- so the state is read back off the observation rather than decided again here.
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            language: "en",
            translated: false);

        var processor = harness.BuildProcessor();
        var envelope = PipelineTestHarness.Envelope(
            "Un navire de charge a été approché par de petites embarcations.",
            sourceIdentifier: "a-1") with
        {
            DeclaredLanguage = "fr",
        };

        await processor.ProcessAsync(envelope, CancellationToken.None);

        var stored = harness.Observations.Committed[0];

        Assert.Equal("fr", stored.DetectedLanguage);
        Assert.Equal(TranslationState.NotTranslated, stored.Translation);
        Assert.False(stored.HasEnglishText);
    }

    [Fact]
    public async Task ALowConfidenceClassificationStillKeepsTheTranslationItPaidFor()
    {
        // Uncertainty about whether a report is piracy or a maritime incident is not uncertainty
        // about the English the model wrote. Discarding the translation here would leave a reader
        // looking at Arabic on the one path where a translation had already been performed.
        var harness = new PipelineTestHarness();
        harness.ResolveAllTo(12.585, 43.334, "Bab-el-Mandeb");
        harness.Enrichment.MinimumAcceptedConfidence = 0.6;
        harness.EnrichmentService.Behaviour = _ => StubEnrichmentService.Success(
            summary: "A cargo vessel was approached by small craft.",
            confidence: 0.2,
            language: "ar",
            translated: true,
            translatedTitle: "Cargo vessel approached near Bab-el-Mandeb");

        var processor = harness.BuildProcessor();
        await processor.ProcessAsync(
            PipelineTestHarness.Envelope("سفينة شحن اقتربت منها زوارق صغيرة.", sourceIdentifier: "a-1"),
            CancellationToken.None);

        var stored = harness.Observations.Committed[0];

        Assert.Equal(TranslationState.MachineTranslated, stored.Translation);
        Assert.Equal("Cargo vessel approached near Bab-el-Mandeb", stored.TranslatedTitle);

        // The classification itself was still refused, which is the behaviour this path exists for.
        Assert.Equal("keyword", stored.ClassificationMethod);
    }

    [Fact]
    public void TheValidatorRefusesATranslationClaimWithNoEnglishBehindIt()
    {
        var payload = new AiEnrichmentPayload
        {
            SchemaVersion = EnrichmentContract.SchemaVersion,
            Language = "ar",
            Translated = true,
            TitleEnglish = "   ",
            Summary = "   ",
            EventType = "PIRACY",
            Severity = "HIGH",
            Confidence = 0.8,
        };

        var result = EnrichmentPayloadValidator.Validate(payload);

        // The missing summary fails the payload outright, which is the stronger protection. The
        // point held here is that "translated" is never taken on the model's word alone.
        Assert.False(result.IsValid);
    }

    [Fact]
    public void TheValidatorHonoursATranslationClaimThatCarriesEnglish()
    {
        var payload = new AiEnrichmentPayload
        {
            SchemaVersion = EnrichmentContract.SchemaVersion,
            Language = "ar",
            Translated = true,
            TitleEnglish = "Clashes in the coastal district",
            Summary = "Clashes were reported in the coastal district.",
            EventType = "CONFLICT",
            Severity = "HIGH",
            Confidence = 0.8,
        };

        var result = EnrichmentPayloadValidator.Validate(payload);

        Assert.True(result.IsValid);
        Assert.True(result.Value!.Translated);
        Assert.Equal("Clashes in the coastal district", result.Value.TranslatedTitle);
    }

    private static RawObservation Observe(string content = "A vessel was boarded near the strait.") => new(
        Guid.CreateVersion7(),
        ObservationKind.News,
        "test:source",
        content,
        sourceIdentifier: "a-1",
        DateTimeOffset.UtcNow,
        ObservationProvenance.Polled);
}
