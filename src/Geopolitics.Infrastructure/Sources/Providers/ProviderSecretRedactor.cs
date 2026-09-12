using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Removes configured provider credentials from text that is about to be written down.
/// <para>
/// This exists because one of the upstream APIs takes its credential as a URL path segment. The
/// runtime's own HTTP logging redacts query strings and header values by default but not path
/// segments, so a FIRMS poll writes its map key into every log sink at <c>Information</c> level.
/// That default also means ACLED's key survives only because it happens to sit in the query string,
/// which is protection by coincidence rather than by design — a deployment can turn query redaction
/// off with an environment switch and would then start leaking that key too.
/// </para>
/// <para>
/// The redactor is told what the secrets are rather than guessing at them. A heuristic that looked
/// for key-shaped strings would both miss real credentials and mangle innocent path segments like a
/// dataset name; matching the values the deployment actually configured is exact.
/// </para>
/// </summary>
internal sealed class ProviderSecretRedactor(IOptionsMonitor<ProviderOptions> options)
{
    private const string Mask = "***";

    /// <summary>
    /// Returns <paramref name="value"/> with every configured credential replaced by a mask, in both
    /// its literal and its URL-escaped form. The escaped form matters: the ACLED email is placed in a
    /// query string through <see cref="Uri.EscapeDataString(string)"/>, so the characters on the wire
    /// are not the characters in configuration.
    /// </summary>
    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var current = options.CurrentValue;
        var redacted = value;

        foreach (var secret in Secrets(current))
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                continue;
            }

            redacted = redacted.Replace(secret, Mask, StringComparison.Ordinal);

            var escaped = Uri.EscapeDataString(secret);
            if (!string.Equals(escaped, secret, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(escaped, Mask, StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    /// <summary>
    /// Everything in provider configuration that must never be written down. The ACLED email is
    /// included because it is half of that credential as well as a personal identifier.
    /// </summary>
    private static IEnumerable<string> Secrets(ProviderOptions options)
    {
        yield return options.NasaFirms.ApiKey;
        yield return options.Acled.ApiKey;
        yield return options.Acled.Email;
    }
}
