using System.Globalization;
using System.Text;
using System.Text.Json;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Conflicts;
using Geopolitics.Application.Enrichment;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;
using Microsoft.Extensions.AI;

namespace Geopolitics.Infrastructure.Ai;

/// <summary>
/// An in-process stand-in for a chat provider, used so the application demos and its CI run with no
/// credentials and no network.
/// <para>
/// It is a test double, not a small language model, and it is careful about the difference. It
/// composes a summary out of facts it can establish deterministically — the script the text is
/// written in, the category the keyword classifier assigns, the place the gazetteer recognises — and
/// it never invents a translation it cannot perform. Output is labelled <c>mock</c> end to end, so
/// nothing it produces can be mistaken on the dashboard, in a stored inference, or in an evaluation
/// report for the work of a real model.
/// </para>
/// <para>
/// Its value is that it exercises the real path: it returns JSON that must satisfy the same schema
/// validator, and it names places rather than positioning them, so the name-then-resolve separation
/// in ADR 005 is genuinely tested offline rather than bypassed.
/// </para>
/// </summary>
public sealed class DeterministicMockChatClient(IEventClassifier classifier) : IChatClient
{
    public const string ProviderName = "mock";
    public const string ModelName = "deterministic-stub";

    private static readonly string[] OrganisationSuffixes =
        ["navy", "forces", "command", "coalition", "ministry", "guard", "army", "militia", "agency", "council"];

    private static readonly ChatClientMetadata ClientMetadata = new(ProviderName, null, ModelName);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var report = ExtractReport(messages);
        var json = IsConflictAssignment(messages) ? DeclineToAssign()
            : IsConflictNarrative(messages) ? DeclineToNarrate()
            : BuildPayload(report);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            ModelId = ModelName,
            FinishReason = ChatFinishReason.Stop,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? ClientMetadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
        // Nothing to release: this client holds no connection, handle, or unmanaged resource.
    }

    /// <summary>
    /// Whether this is the conflict-assignment question rather than the enrichment one. Recognised
    /// from the system instruction, because that is the part of the conversation this client is
    /// entitled to read as instruction — the user turn is the untrusted report.
    /// </summary>
    private static bool IsConflictAssignment(IEnumerable<ChatMessage> messages) =>
        messages.Any(message =>
            message.Role == ChatRole.System
            && message.Text?.Contains("assign reports to known armed conflicts", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// The honest answer from a stand-in that cannot read.
    /// <para>
    /// Deciding which of several wars a report belongs to needs an understanding of the text, and
    /// this client has none — it matches keywords. It could pick the largest conflict on the list and
    /// be right most of the time by volume, which is precisely the failure worth refusing: the
    /// conflicts it would then be wrong about are the small ones, and those are the entire reason for
    /// having a register of 319 rather than a list of three. So it declines, at zero confidence, and
    /// the report stays unassigned with its candidates named.
    /// </para>
    /// </summary>
    private static string DeclineToAssign() => JsonSerializer.Serialize(new
    {
        schemaVersion = ConflictChoiceContract.SchemaVersion,
        conflictKey = ConflictChoiceContract.None,
        confidence = 0.0,
        rationale = "The offline stand-in cannot read a report well enough to choose between conflicts.",
    });

    /// <summary>Whether this is the per-conflict summary question.</summary>
    private static bool IsConflictNarrative(IEnumerable<ChatMessage> messages) =>
        messages.Any(message =>
            message.Role == ChatRole.System
            && message.Text?.Contains("summarise what a set of reports", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// A stand-in that cannot read must not write the paragraph.
    /// <para>
    /// Everything else this client produces is a fact it can establish — the script of the text, the
    /// category the keyword classifier assigns, the place the gazetteer recognises — and is labelled
    /// mock so nothing mistakes it for a model. A narrative has no such floor: any sentence it
    /// assembled would be prose about a war, and prose about a war reads as analysis whatever label
    /// sits beside it. So it answers at zero confidence and the service declines on its behalf.
    /// </para>
    /// </summary>
    private static string DeclineToNarrate() => JsonSerializer.Serialize(new
    {
        schemaVersion = ConflictNarrativeContract.SchemaVersion,
        summary = "The offline stand-in does not write summaries of conflicts.",
        confidence = 0.0,
    });

    /// <summary>
    /// Recovers the report body from the prompt's delimiters. The delimiters exist to tell a real
    /// model what is data; here they double as the parse boundary, which keeps the mock honest —
    /// it reads exactly what a provider would have been sent, and nothing the pipeline knows privately.
    /// </summary>
    private static string ExtractReport(IEnumerable<ChatMessage> messages)
    {
        var text = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        var start = text.IndexOf("<<<REPORT", StringComparison.Ordinal);
        var end = text.IndexOf("REPORT>>>", StringComparison.Ordinal);

        return start >= 0 && end > start
            ? text[(start + "<<<REPORT".Length)..end].Trim()
            : text.Trim();
    }

    private string BuildPayload(string report)
    {
        var body = ReadBody(report) ?? report;
        var title = ReadLineField(report, "title:");
        var source = ReadLineField(report, "source:") ?? "unknown";

        // Composed as two sentences rather than joined with a space. A headline carries no full
        // stop, so gluing the body straight onto it leaves the body's first word looking like a
        // continuation of whatever capitalised run ended the title. That is how a title ending
        // "Northern Transit Council" followed by a body opening "Scheduled convoy departures..."
        // produced an organisation called "Northern Transit Council Scheduled", and why two reports
        // about the same actor extracted two different names for it and failed to correlate.
        var text = Compose(title, body);

        var classification = classifier.Classify(text);
        var language = DetectLanguage(body);
        var place = Gazetteer.FindFirstMention(text);
        var entities = ExtractEntities(text, place);

        var payload = new
        {
            schemaVersion = EnrichmentContract.SchemaVersion,
            language,

            // Always false, and that is the honest answer rather than a limitation worked around.
            // This client matches keywords and identifies scripts; it cannot read Arabic, and a
            // stand-in that claimed otherwise would put fabricated English on the dashboard where a
            // reader could not tell it from a real model's work.
            translated = false,
            titleEnglish = string.Empty,
            summary = BuildSummary(source, title, body, language, classification, place),
            eventType = EnrichmentContract.ToWire(classification.EventType),
            severity = EnrichmentContract.ToWire(classification.Severity),
            confidence = Math.Round(Math.Clamp(classification.Confidence, 0.3, 0.9), 2),
            severityRationale = BuildRationale(classification),
            locations = place is null
                ? Array.Empty<object>()
                : [new { name = place, country = string.Empty }],
            entities = entities.Select(entity => new
            {
                name = entity.Name,
                type = EnrichmentContract.ToWire(entity.Type),
            }),
        };

        return JsonSerializer.Serialize(payload, EnrichmentContract.SerializerOptions);
    }

    /// <summary>
    /// States what can be established rather than paraphrasing what cannot.
    /// <para>
    /// English text gets an extractive summary — its own opening sentences, which is close to what a
    /// summariser would return for a short wire report. Text in another script cannot be translated
    /// here, so the summary says so plainly and reports the structured facts instead. Writing
    /// invented English for text this component cannot read would be fabrication, and on the
    /// dashboard it would be indistinguishable from a real model's work.
    /// </para>
    /// </summary>
    private static string BuildSummary(
        string source,
        string? title,
        string body,
        string language,
        EventClassification classification,
        string? place)
    {
        if (language == "en")
        {
            return LeadingSentences(string.IsNullOrWhiteSpace(body) ? title ?? source : body, 2);
        }

        var where = place is null ? "no named location" : place;

        return $"[Mock enrichment — not translated] The source text is in '{language}'. "
            + $"{source} reports a {Humanise(classification.EventType)} involving {where}.";
    }

    /// <summary>Takes the first <paramref name="count"/> sentences, which is the summary for a short report.</summary>
    private static string LeadingSentences(string text, int count)
    {
        var trimmed = text.Trim();
        var taken = 0;

        for (var index = 0; index < trimmed.Length; index++)
        {
            if (trimmed[index] is not ('.' or '!' or '?'))
            {
                continue;
            }

            taken++;

            if (taken == count)
            {
                return trimmed[..(index + 1)];
            }
        }

        return trimmed.Length <= EnrichmentContract.MaxSummaryLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, EnrichmentContract.MaxSummaryLength - 1), "…");
    }

    private static string BuildRationale(EventClassification classification) =>
        $"Severity {EnrichmentContract.ToWire(classification.Severity)} assigned by the deterministic "
        + $"{classification.Method} stand-in, not by a language model.";

    /// <summary>
    /// Identifies the script, which is all that can be determined without a model. It reports a
    /// representative language for each script and does not pretend to distinguish languages that
    /// share one — Arabic script alone does not separate Arabic from Persian or Urdu.
    /// </summary>
    private static string DetectLanguage(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var character in text)
        {
            var tag = character switch
            {
                >= '؀' and <= 'ۿ' => "ar",
                >= 'Ѐ' and <= 'ӿ' => "ru",
                >= '֐' and <= '׿' => "he",
                >= '一' and <= '鿿' => "zh",
                >= 'Ͱ' and <= 'Ͽ' => "el",
                >= '぀' and <= 'ヿ' => "ja",
                >= '가' and <= '힯' => "ko",
                _ => null,
            };

            if (tag is not null)
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        if (counts.Count == 0)
        {
            return "en";
        }

        // Ordered by tag as well as count so a tie resolves the same way on every run.
        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First()
            .Key;
    }

    /// <summary>
    /// Pulls capitalised runs out of the text and types them by shape: a gazetteer hit is a place, a
    /// name ending in an organisational word is an organisation, anything else is left unknown rather
    /// than guessed at. Crude by design — a mock that appeared to do real entity recognition would
    /// misrepresent what the offline demo is capable of.
    /// </summary>
    private static List<ExtractedEntity> ExtractEntities(string text, string? place)
    {
        var results = new List<ExtractedEntity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (place is not null && seen.Add(place))
        {
            results.Add(new ExtractedEntity(place, EntityType.Place));
        }

        foreach (var candidate in CapitalisedRuns(text))
        {
            if (results.Count >= 5 || !seen.Add(candidate))
            {
                continue;
            }

            // "Kerch Strait" is already recorded when the run detector also offers the bare
            // "Strait" it contains. Keeping both would report one actor as two.
            if (results.Any(existing => existing.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var type = Gazetteer.TryResolve(candidate, out _) ? EntityType.Place
                : OrganisationSuffixes.Any(suffix => candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    ? EntityType.Organisation
                    : EntityType.Unknown;

            results.Add(new ExtractedEntity(candidate, type));
        }

        return results;
    }

    /// <summary>
    /// Joins a headline and a body into one block of prose with a sentence boundary between them,
    /// so that anything reading this text sees where the title ended.
    /// </summary>
    private static string Compose(string? title, string body)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return body;
        }

        var trimmed = title.TrimEnd('.', ' ');
        return trimmed.Length == 0 ? body : $"{trimmed}. {body}";
    }

    private static IEnumerable<string> CapitalisedRuns(string text)
    {
        var builder = new StringBuilder();
        var atSentenceStart = true;

        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = token.Trim();
            var terminates = word.EndsWith('.') || word.EndsWith('!') || word.EndsWith('?');
            var bare = word.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')');

            var isName = bare.Length > 1
                && char.IsUpper(bare[0])
                && bare.Skip(1).All(character => char.IsLower(character) || character == '-')
                && !atSentenceStart;

            if (isName)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(bare);
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            atSentenceStart = terminates;
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>Reads a field written beside its marker on one line, such as <c>source:</c>.</summary>
    private static string? ReadLineField(string report, string prefix)
    {
        foreach (var line in report.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = trimmed[prefix.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads everything after the <c>body:</c> marker, which the prompt writes on the lines below it
    /// rather than beside it.
    /// <para>
    /// Kept separate from <see cref="ReadLineField"/> after one helper tried to serve both shapes:
    /// the line scan matched the empty <c>body:</c> marker, returned nothing, and the caller fell
    /// back to the whole report — so every summary began with the prompt's own header lines.
    /// </para>
    /// </summary>
    private static string? ReadBody(string report)
    {
        const string marker = "body:";
        var index = report.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
        {
            return null;
        }

        var body = report[(index + marker.Length)..].Trim();
        return body.Length == 0 ? null : body;
    }

    private static string Humanise(EventType eventType) =>
        CultureInfo.InvariantCulture.TextInfo.ToLower(
            string.Concat(eventType.ToString().Select(character =>
                char.IsUpper(character) ? " " + character : character.ToString())).Trim());
}
