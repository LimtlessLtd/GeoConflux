using System.Text;
using System.Text.Json;

namespace Geopolitics.Application.Conflicts;

/// <param name="Value">The accepted choice, or <see langword="null"/> when validation failed.</param>
/// <param name="Errors">Every problem found, phrased so it can be handed back as a repair instruction.</param>
public sealed record ConflictChoiceValidation(ConflictChoice? Value, IReadOnlyList<string> Errors)
{
    public bool IsValid => Value is not null;

    public string ErrorSummary => string.Join(" ", Errors);
}

/// <summary>
/// The trust boundary for a model's conflict choice.
/// <para>
/// It enforces the one rule the whole design rests on, and it enforces it here rather than trusting
/// the schema: <b>the answer must be a key that was offered.</b> Constrained decoding makes an
/// invented key unlikely, and providers without that capability are given the same shape as prose
/// instead — so a key that was never offered has to be rejected on the way back, or the guarantee
/// would hold only for the providers that happened to support it.
/// </para>
/// <para>
/// Every problem is reported rather than the first, because the list is what the repair turn is
/// given, and a one-error-at-a-time loop burns a round trip per mistake.
/// </para>
/// </summary>
public static class ConflictChoiceValidator
{
    public static ConflictChoiceValidation Validate(string? responseText, IReadOnlyList<string> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Invalid("The response was empty.");
        }

        ConflictChoicePayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<ConflictChoicePayload>(
                StripCodeFence(responseText),
                ConflictChoiceContract.SerializerOptions);
        }
        catch (JsonException exception)
        {
            return Invalid($"The response was not valid JSON matching the schema: {exception.Message}");
        }

        return payload is null
            ? Invalid("The response deserialised to nothing.")
            : Validate(payload, offered);
    }

    private static ConflictChoiceValidation Validate(ConflictChoicePayload payload, IReadOnlyList<string> offered)
    {
        var errors = new List<string>();

        if (payload.SchemaVersion != ConflictChoiceContract.SchemaVersion)
        {
            errors.Add($"schemaVersion must be {ConflictChoiceContract.SchemaVersion} but was {payload.SchemaVersion}.");
        }

        var key = payload.ConflictKey?.Trim();
        var chose = null as string;

        if (string.IsNullOrEmpty(key))
        {
            errors.Add("conflictKey is required.");
        }
        else if (string.Equals(key, ConflictChoiceContract.None, StringComparison.OrdinalIgnoreCase))
        {
            chose = null;
        }
        else if (offered.Contains(key, StringComparer.Ordinal))
        {
            chose = key;
        }
        else
        {
            // The rule the register exists to enforce. A model that answers with a conflict nobody
            // offered it has not chosen a category, it has invented one, and the difference is the
            // difference between a model that categorises and a model that decides what the
            // categories are.
            errors.Add(
                $"conflictKey '{Sanitise(key, 60)}' was not one of the conflicts offered. Answer with one of "
                + $"{string.Join(", ", offered)} or {ConflictChoiceContract.None}.");
        }

        if (payload.Confidence is < 0 or > 1 || double.IsNaN(payload.Confidence))
        {
            errors.Add("confidence must be a number between 0 and 1.");
        }

        var proposed = Sanitise(payload.ProposedName, ConflictChoiceContract.MaxProposedNameLength);

        if (proposed is not null && chose is not null)
        {
            // Proposing a new conflict and picking an existing one are different answers, and a
            // response that does both has not answered. Rejecting it is cheaper than deciding which
            // half to believe.
            errors.Add("proposedName is only allowed when conflictKey is " + ConflictChoiceContract.None + ".");
        }

        return errors.Count > 0
            ? new ConflictChoiceValidation(null, errors)
            : new ConflictChoiceValidation(
                new ConflictChoice(
                    chose,
                    payload.Confidence,
                    Sanitise(payload.Rationale, ConflictChoiceContract.MaxRationaleLength),
                    proposed),
                []);
    }

    private static ConflictChoiceValidation Invalid(string error) => new(null, [error]);

    /// <summary>
    /// Removes control characters and collapses whitespace. This text is rendered in a browser and
    /// written to structured logs, and newline injection in a field that holds one sentence has no
    /// legitimate use.
    /// </summary>
    private static string? Sanitise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value.Trim())
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var cleaned = builder.ToString().Trim();

        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length <= maxLength ? cleaned : string.Concat(cleaned.AsSpan(0, maxLength - 1), "…");
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');

        if (firstNewline < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);

        return (closing < 0 ? body : body[..closing]).Trim();
    }
}
