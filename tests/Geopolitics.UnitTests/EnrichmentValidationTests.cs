using System.Text.Json;
using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;

namespace Geopolitics.UnitTests;

/// <summary>
/// The validator is the trust boundary for model output, so these tests are written from the
/// position that the model is adversarial rather than merely imperfect: malformed, oversized,
/// out-of-range, and injection-shaped responses all arrive here, and none of them may reach the
/// pipeline as if they were valid.
/// </summary>
public sealed class EnrichmentValidationTests
{
    private const string ValidPayload = """
    {
      "schemaVersion": 2,
      "language": "en",
      "translated": false,
      "titleEnglish": "",
      "summary": "A cargo vessel was approached by small craft.",
      "eventType": "PIRACY",
      "severity": "HIGH",
      "confidence": 0.82,
      "severityRationale": "Armed approach to a crewed vessel.",
      "locations": [{ "name": "Bab-el-Mandeb", "country": "Yemen" }],
      "entities": [{ "name": "Combined Maritime Forces", "type": "ORGANISATION" }]
    }
    """;

    [Fact]
    public void TheSchemaItselfIsWellFormedJson()
    {
        // Guards the static initialiser: a malformed schema would otherwise surface as a type
        // initialisation failure on the first observation processed rather than at build time.
        var schema = EnrichmentContract.ResponseSchema;

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("eventType", out _));

        // The contract must make coordinates unexpressible, not merely discouraged.
        var locationProperties = properties.GetProperty("locations").GetProperty("items").GetProperty("properties");
        Assert.False(locationProperties.TryGetProperty("latitude", out _));
        Assert.False(locationProperties.TryGetProperty("longitude", out _));
    }

    [Fact]
    public void AWellFormedResponseIsAccepted()
    {
        var result = EnrichmentPayloadValidator.Validate(ValidPayload);

        Assert.True(result.IsValid);
        Assert.Equal(EventType.Piracy, result.Value!.EventType);
        Assert.Equal(Severity.High, result.Value.Severity);
        Assert.Equal(0.82, result.Value.Confidence);
        Assert.Equal("en", result.Value.Language);
        Assert.Equal("Bab-el-Mandeb", result.Value.LocationName);
        Assert.Equal("Combined Maritime Forces", Assert.Single(result.Value.Entities).Name);
        Assert.Equal(EntityType.Organisation, result.Value.Entities[0].Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm sorry, I can't help with that.")]
    [InlineData("{\"schemaVersion\": 2, \"summary\": ")]
    [InlineData("[]")]
    public void MalformedOutputIsRejectedRatherThanPartiallyRead(string response)
    {
        var result = EnrichmentPayloadValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void AMarkdownFencedResponseIsAcceptedBecauseThatIsFormattingNotAnError()
    {
        var fenced = "```json\n" + ValidPayload + "\n```";

        Assert.True(EnrichmentPayloadValidator.Validate(fenced).IsValid);
    }

    [Fact]
    public void AResponseFromADifferentSchemaVersionIsRejected()
    {
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3"));

        Assert.False(result.IsValid);
        Assert.Contains("schemaVersion", result.ErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEventTypeIsRejectedAndTheErrorNamesTheAllowedValues()
    {
        // Mapping an unknown category to Other would hide model drift behind a plausible default.
        // Rejecting it produces a repair message that tells the model exactly what it may return.
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace("\"PIRACY\"", "\"ARMED_CLASH\""));

        Assert.False(result.IsValid);
        Assert.Contains("MARITIME_INCIDENT", result.ErrorSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("-0.2")]
    public void ConfidenceOutsideTheUnitIntervalIsRejected(string confidence)
    {
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace("0.82", confidence));

        Assert.False(result.IsValid);
        Assert.Contains("confidence", result.ErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExtraPropertyIsRejectedBecauseTheContractIsExact()
    {
        var withExtra = ValidPayload.Replace(
            "\"language\": \"en\",",
            "\"language\": \"en\", \"reasoning\": \"step 1... step 2...\",");

        var result = EnrichmentPayloadValidator.Validate(withExtra);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AllProblemsAreReportedTogetherSoOneRepairTurnCanFixThemAll()
    {
        var badlyBroken = """
        {
          "schemaVersion": 9,
          "language": "en",
          "summary": "",
          "eventType": "NONSENSE",
          "severity": "EXTREMELY_BAD",
          "confidence": 7,
          "severityRationale": "x",
          "locations": [],
          "entities": []
        }
        """;

        var result = EnrichmentPayloadValidator.Validate(badlyBroken);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 5, $"Expected every problem to be reported; got: {result.ErrorSummary}");
    }

    [Fact]
    public void ControlCharactersAreStrippedFromTextFields()
    {
        // Model output reaches a browser and a structured log. An escape sequence or an injected
        // newline in a field meant to hold one sentence has no legitimate use.
        var hostile = ValidPayload.Replace(
            "A cargo vessel was approached by small craft.",
            "Line one\\u0007\\u001b[31mred\\nLine two");

        var result = EnrichmentPayloadValidator.Validate(hostile);

        Assert.True(result.IsValid);
        Assert.DoesNotContain('\n', result.Value!.Summary);
        Assert.DoesNotContain(result.Value.Summary, character => char.IsControl(character));
    }

    [Fact]
    public void AnOversizedSummaryIsTruncatedRatherThanRejected()
    {
        var huge = ValidPayload.Replace(
            "A cargo vessel was approached by small craft.",
            new string('a', EnrichmentContract.MaxSummaryLength * 3));

        var result = EnrichmentPayloadValidator.Validate(huge);

        Assert.True(result.IsValid);
        Assert.Equal(EnrichmentContract.MaxSummaryLength, result.Value!.Summary.Length);
    }

    [Fact]
    public void TooManyEntitiesIsRejectedSoOneResponseCannotBloatARow()
    {
        var many = string.Join(",", Enumerable.Range(0, EnrichmentContract.MaxEntities + 5)
            .Select(index => $"{{ \"name\": \"Actor {index}\", \"type\": \"ORGANISATION\" }}"));

        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace(
            "[{ \"name\": \"Combined Maritime Forces\", \"type\": \"ORGANISATION\" }]",
            $"[{many}]"));

        Assert.False(result.IsValid);
        Assert.Contains("entities", result.ErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEntityTypeKeepsTheActorAndDropsOnlyTheLabel()
    {
        // Lower stakes than a category: losing an actor the text really names is worse than
        // recording it as untyped.
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace("\"ORGANISATION\"", "\"WARLORD\""));

        Assert.True(result.IsValid);
        Assert.Equal(EntityType.Unknown, Assert.Single(result.Value!.Entities).Type);
    }

    [Fact]
    public void DuplicateEntitiesAreCollapsed()
    {
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace(
            "[{ \"name\": \"Combined Maritime Forces\", \"type\": \"ORGANISATION\" }]",
            "[{ \"name\": \"Houthi\", \"type\": \"ORGANISATION\" }, { \"name\": \"houthi\", \"type\": \"ORGANISATION\" }]"));

        Assert.True(result.IsValid);
        Assert.Single(result.Value!.Entities);
    }

    [Fact]
    public void ALanguageTagIsReducedToLettersDigitsAndHyphens()
    {
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace(
            "\"language\": \"en\"",
            "\"language\": \"  AR-<script>Latn  \""));

        Assert.True(result.IsValid);
        Assert.Equal("ar-scriptlatn", result.Value!.Language);
    }

    [Fact]
    public void TheFirstUsableLocationNameIsTakenAndNoCoordinateEverIs()
    {
        var result = EnrichmentPayloadValidator.Validate(ValidPayload.Replace(
            "[{ \"name\": \"Bab-el-Mandeb\", \"country\": \"Yemen\" }]",
            "[{ \"name\": \"  \", \"country\": \"Yemen\" }, { \"name\": \"Gulf of Aden\", \"country\": \"\" }]"));

        Assert.True(result.IsValid);
        Assert.Equal("Gulf of Aden", result.Value!.LocationName);
    }
}
