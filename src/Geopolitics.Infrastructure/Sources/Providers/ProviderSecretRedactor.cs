using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Removes configured provider credentials from text that is about to be written down.
/// <para>
/// This exists because one of the upstream APIs takes its credential as a URL path segment. The
/// runtime's own HTTP logging redacts query strings and header values by default but not path
/// segments, so a FIRMS poll writes its map key into every log sink at <c>Information</c> level.
/// ACLED no longer puts a credential in the URL at all: it posts an account password to an OAuth
/// endpoint and carries a bearer token in a header, neither of which the default logging writes down.
/// The password is still listed below, because the place it reliably resurfaces is an exception
/// message — a socket failure or a provider echoing the request back — and that text reaches the logs
/// through a different door from the request line.
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
    /// its literal and its URL-escaped form. The escaped form matters: the ACLED username and
    /// password are form-encoded into a request body, so the characters on the wire are not the
    /// characters in configuration.
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
    /// Everything in provider configuration that must never be written down. The ACLED username is
    /// included because it is half of that credential as well as a personal identifier — it is the
    /// email address of a named account holder.
    /// </summary>
    private static IEnumerable<string> Secrets(ProviderOptions options)
    {
        yield return options.NasaFirms.ApiKey;
        yield return options.Acled.Username;
        yield return options.Acled.Password;
        yield return options.Ucdp.AccessToken;
    }
}
