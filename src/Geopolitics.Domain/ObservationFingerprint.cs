using System.Security.Cryptography;
using System.Text;

namespace Geopolitics.Domain;

/// <summary>
/// Deterministic content identity for an observation. Two payloads that describe the same
/// report from the same source produce the same fingerprint, which lets the pipeline discard
/// exact re-deliveries without consulting an external service or a language model.
/// </summary>
public static class ObservationFingerprint
{
    public static string Compute(string sourceName, string? sourceIdentifier, string content)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            throw new DomainException("A fingerprint requires a source name.");
        }

        // A source-assigned identifier is the strongest signal available; fall back to the
        // normalised content so sources without stable identifiers still deduplicate.
        var discriminator = string.IsNullOrWhiteSpace(sourceIdentifier)
            ? Normalise(content)
            : sourceIdentifier.Trim().ToLowerInvariant();

        if (discriminator.Length == 0)
        {
            throw new DomainException("A fingerprint requires a source identifier or content.");
        }

        var payload = $"{sourceName.Trim().ToLowerInvariant()}|{discriminator}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Collapses punctuation, whitespace, and casing so that trivial formatting differences
    /// between re-published copies of the same text do not defeat deduplication.
    /// </summary>
    private static string Normalise(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(content.Length);
        var pendingSeparator = false;

        foreach (var character in content)
        {
            if (!char.IsLetterOrDigit(character))
            {
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(character));
            pendingSeparator = false;
        }

        return builder.ToString();
    }
}
