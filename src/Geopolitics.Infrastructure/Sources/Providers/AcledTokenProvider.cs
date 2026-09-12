using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Holds the ACLED OAuth access token and renews it when it is close to expiring.
/// <para>
/// ACLED replaced its key-and-email query parameters with OAuth 2.0. That changes the shape of the
/// integration rather than just a field name: a credential is now exchanged for a short-lived token,
/// and something has to own that token's lifetime. Doing it inside the poll would mean a token
/// request before every read — twenty-four hours of validity spent on a single six-hour poll, and an
/// account's rate limit consumed on authentication instead of data.
/// </para>
/// <para>
/// Renewal prefers the refresh token and falls back to the password grant. That ordering is what
/// separates a robust integration from a brittle one: refresh tokens can be revoked, invalidated by a
/// password change, or expire earlier than the fourteen days ACLED documents, and in every one of
/// those cases a provider that only knew how to refresh would go dark until someone restarted the
/// process. Falling back costs one extra request in an uncommon case.
/// </para>
/// </summary>
internal sealed partial class AcledTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ProviderOptions> options,
    ProviderSecretRedactor redactor,
    TimeProvider timeProvider,
    ILogger<AcledTokenProvider> logger) : IDisposable
{
    /// <summary>The scope ACLED issues API tokens under. Protocol detail, not a deployment choice.</summary>
    private const string Scope = "authenticated";

    /// <summary>
    /// How long before stated expiry a token is treated as spent. A token that is valid when the
    /// request is composed and expired when it arrives fails the poll for no reason, and ACLED's
    /// twenty-four hour lifetime means this margin costs nothing.
    /// </summary>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Assumed refresh token lifetime, because the token response does not state one. ACLED documents
    /// fourteen days; this is deliberately shorter so the assumption fails safe — if it is wrong the
    /// provider falls back to the password grant, which it already knows how to do, rather than
    /// presenting a token the server has forgotten.
    /// </summary>
    private static readonly TimeSpan AssumedRefreshLifetime = TimeSpan.FromDays(13);

    /// <summary>
    /// Fallback lifetime when the server does not state one. Short on purpose: re-authenticating
    /// sooner than necessary costs one request, while treating an unknown lifetime as long means
    /// every poll after the real expiry fails until the process restarts.
    /// </summary>
    private static readonly TimeSpan AssumedAccessLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Serialises renewal so concurrent readers share one token request rather than each making their
    /// own. The adapter is a singleton and both the poll loop and a one-shot batch read can be
    /// enumerating it at once, so this is the state that would otherwise be raced.
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// The whole cached grant behind one reference, swapped atomically.
    /// <para>
    /// Held as a single immutable object rather than as four fields because the fast path reads it
    /// without taking the gate. Separate fields would let a reader see a new token beside an old
    /// expiry, and <see cref="DateTimeOffset"/> is wide enough that even reading one of them is not
    /// guaranteed to be atomic. One reference assignment is.
    /// </para>
    /// </summary>
    private CachedGrant? cache;

    /// <summary>
    /// Returns a token that is valid now, renewing it first if necessary.
    /// </summary>
    /// <exception cref="InvalidOperationException">ACLED refused to issue a token.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (UsableAccessToken() is { } cached)
        {
            return cached;
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            // Checked again inside the gate: several readers can arrive together, and only the first
            // of them should spend a request.
            if (UsableAccessToken() is { } justRenewed)
            {
                return justRenewed;
            }

            return await RenewAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Discards the cached access token while keeping the refresh token. Called when ACLED rejects a
    /// token that had not reached its stated expiry, which means the server stopped honouring it
    /// early — revoked, or invalidated by a change on the account. Keeping the refresh token lets the
    /// recovery happen without putting the password on the wire again.
    /// </summary>
    public void Invalidate()
    {
        if (Volatile.Read(ref cache) is { } current)
        {
            Volatile.Write(ref cache, current with { AccessToken = null, AccessExpiresAt = default });
        }
    }

    public void Dispose() => gate.Dispose();

    private string? UsableAccessToken()
    {
        var current = Volatile.Read(ref cache);

        return current?.AccessToken is { } token && timeProvider.GetUtcNow() + RenewalMargin < current.AccessExpiresAt
            ? token
            : null;
    }

    private async Task<string> RenewAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Acled;
        var current = Volatile.Read(ref cache);

        // The refresh grant is tried first when there is a refresh token that should still be good,
        // because it does not put the account password on the wire.
        if (current?.RefreshToken is { } refresh && timeProvider.GetUtcNow() + RenewalMargin < current.RefreshExpiresAt)
        {
            try
            {
                return await RequestAsync(settings, RefreshGrant(settings, refresh), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not fatal, and not silent. A refresh token can be revoked or invalidated by a
                // password change at any time, so this is an expected path rather than a fault — but
                // an operator watching a provider re-authenticate on every poll wants to see why.
                // Rendered to text and redacted rather than logged as an exception, for the same
                // reason RedactingHttpClientLogger does it: a transport failure carries the request
                // in its message often enough that handing the object to the logger would undo the
                // redaction applied everywhere else. Guarded because rendering and scanning a stack
                // trace is real work to do for a line nobody will read.
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var reason = redactor.Redact(exception.ToString());
                    LogRefreshFailed(logger, reason);
                }

                // The whole grant goes, not just the access token: the refresh token is the thing
                // that was just refused, so keeping it would mean trying it again next cycle.
                Volatile.Write(ref cache, null);
            }
        }

        return await RequestAsync(settings, PasswordGrant(settings), cancellationToken);
    }

    private static Dictionary<string, string> PasswordGrant(AcledProviderOptions settings) => new(StringComparer.Ordinal)
    {
        ["grant_type"] = "password",
        ["client_id"] = settings.ClientId,
        ["scope"] = Scope,
        ["username"] = settings.Username,
        ["password"] = settings.Password,
    };

    private static Dictionary<string, string> RefreshGrant(AcledProviderOptions settings, string refresh) => new(StringComparer.Ordinal)
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = settings.ClientId,
        ["refresh_token"] = refresh,
    };

    private async Task<string> RequestAsync(
        AcledProviderOptions settings,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(AcledEventSource.HttpClientName);

        // Absolute, and posted through the same named client as the data requests so it inherits the
        // outbound address policy, the resilience pipeline, and the redacting logger. An
        // authentication request reaching the network on a different path from the reads it
        // authenticates would be the one request in this system with none of those protections.
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The status alone does not distinguish a wrong password from a retired endpoint, and
            // that is exactly the distinction an operator needs. OAuth error bodies are specified, so
            // the reason is read out rather than guessed at — redacted, because this message travels
            // into the logs by way of the poll failure.
            throw new InvalidOperationException(
                $"ACLED refused to issue an access token ({(int)response.StatusCode}): "
                + redactor.Redact(OAuthError(body)));
        }

        var grant = ReadToken(body);
        var now = timeProvider.GetUtcNow();
        var previous = Volatile.Read(ref cache);

        // A refresh grant may or may not rotate the refresh token. Keeping the previous one when the
        // server does not issue a new one is what stops a rotation-free server from forcing a
        // password grant on every renewal.
        var refreshToken = grant.RefreshToken ?? previous?.RefreshToken;

        Volatile.Write(ref cache, new CachedGrant(
            grant.AccessToken,
            now + grant.Lifetime,
            refreshToken,
            grant.RefreshToken is not null ? now + AssumedRefreshLifetime : previous?.RefreshExpiresAt ?? default));

        LogRenewed(logger, grant.Lifetime, now + grant.Lifetime);
        return grant.AccessToken;
    }

    /// <summary>
    /// Reads the token response, treating every field as untrusted: this is a third-party payload
    /// arriving over the network like any other, and carrying a credential makes it no more
    /// trustworthy in shape.
    /// </summary>
    /// <exception cref="InvalidOperationException">The response is not a usable token response.</exception>
    private static TokenGrant ReadToken(string body)
    {
        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            // The body is not echoed. A token endpoint answering with HTML is almost always a portal
            // or a proxy error page, and the useful fact is that it did so, not its markup.
            throw new InvalidOperationException(
                $"The ACLED token response was not valid JSON: {exception.Message}", exception);
        }

        using (parsed)
        {
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The ACLED token response was not a JSON object.");
            }

            if (Text(root, "access_token") is not { } access)
            {
                throw new InvalidOperationException("The ACLED token response carried no access token.");
            }

            return new TokenGrant(access, Text(root, "refresh_token"), Lifetime(root));
        }
    }

    private static TimeSpan Lifetime(JsonElement root)
    {
        var seconds = root.TryGetProperty("expires_in", out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDouble(out var number) ? number : 0,
                JsonValueKind.String => double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed) ? parsed : 0,
                _ => 0,
            }
            : 0;

        // A non-positive or absurd lifetime is treated as "not stated". Trusting a zero would make
        // every token immediately stale and turn the poll loop into an authentication loop.
        return seconds is > 0 and < 60 * 60 * 24 * 30
            ? TimeSpan.FromSeconds(seconds)
            : AssumedAccessLifetime;
    }

    /// <summary>The reason from an RFC 6749 error body, or a stated absence when there is none.</summary>
    private static string OAuthError(string body)
    {
        try
        {
            using var parsed = JsonDocument.Parse(body);

            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                var code = Text(parsed.RootElement, "error");
                var description = Text(parsed.RootElement, "error_description");

                if (code is not null && description is not null)
                {
                    return $"{code}: {description}";
                }

                if ((code ?? description) is { } single)
                {
                    return single;
                }
            }
        }
        catch (JsonException)
        {
            // Falls through. An unparseable error body is itself the diagnosis.
        }

        return "no reason given";
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <param name="AccessToken">Null once invalidated, which forces a renewal without losing the refresh token.</param>
    private sealed record CachedGrant(
        string? AccessToken,
        DateTimeOffset AccessExpiresAt,
        string? RefreshToken,
        DateTimeOffset RefreshExpiresAt);

    private sealed record TokenGrant(string AccessToken, string? RefreshToken, TimeSpan Lifetime);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Renewed the ACLED access token; it is valid for {Lifetime} and expires at {ExpiresAt}.")]
    private static partial void LogRenewed(ILogger logger, TimeSpan lifetime, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "The ACLED refresh token was not accepted, so the account credential will be used instead. {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string reason);
}
