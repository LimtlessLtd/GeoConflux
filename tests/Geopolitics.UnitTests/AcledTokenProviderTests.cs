using System.Net;
using Geopolitics.Infrastructure.Sources.Providers;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// The ACLED token cache, driven directly against a controlled clock.
/// <para>
/// Tested apart from the adapter because everything interesting about it is a function of time, and
/// time is the one thing the end-to-end adapter tests cannot move: they run the real poll loop, which
/// waits on the same <see cref="TimeProvider"/>, so a fake clock there stops the loop instead of
/// ageing the token. Here the clock is the input.
/// </para>
/// </summary>
public sealed class AcledTokenProviderTests
{
    private const string Username = "tester@example.invalid";
    private const string Password = "not-a-real-password";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    private static Func<HttpRequestMessage, HttpResponseMessage> Token() =>
        ScriptedHttpHandler.Respond(Fixture("acled-token.json"), mediaType: "application/json");

    [Fact]
    public async Task ATokenIsRequestedOnceAndThenReused()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Token());
        using var host = Build(handler, out _);
        var tokens = host.GetRequiredService<AcledTokenProvider>();

        var first = await tokens.GetAccessTokenAsync(stop.Token);
        var second = await tokens.GetAccessTokenAsync(stop.Token);

        Assert.Equal("fixture-access-token-not-a-credential", first);
        Assert.Equal(first, second);

        // One request for two calls. This is the whole reason the type exists: ACLED issues a token
        // valid for a day, and re-fetching it per poll spends the account rate limit on
        // authentication instead of on data.
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TheFirstRequestUsesThePasswordGrant()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Token());
        using var host = Build(handler, out _);

        await host.GetRequiredService<AcledTokenProvider>().GetAccessTokenAsync(stop.Token);

        var body = Assert.IsType<string>(handler.Exchanges.Single().Body);
        Assert.Contains("grant_type=password", body, StringComparison.Ordinal);
        Assert.Contains($"username={Uri.EscapeDataString(Username)}", body, StringComparison.Ordinal);
        Assert.Contains($"password={Uri.EscapeDataString(Password)}", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renewal prefers the refresh token, because it does not put the account password on the wire.
    /// </summary>
    [Fact]
    public async Task AnExpiredTokenIsRenewedWithTheRefreshGrant()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Token(), Token());
        using var host = Build(handler, out var clock);
        var tokens = host.GetRequiredService<AcledTokenProvider>();

        await tokens.GetAccessTokenAsync(stop.Token);

        // Past the fixture's stated 24-hour lifetime, so the cached token is spent.
        clock.Advance(TimeSpan.FromHours(25));
        await tokens.GetAccessTokenAsync(stop.Token);

        var bodies = handler.Exchanges.Select(exchange => exchange.Body ?? string.Empty).ToArray();

        Assert.Equal(2, bodies.Length);
        Assert.Contains("grant_type=refresh_token", bodies[1], StringComparison.Ordinal);
        Assert.Contains(
            "refresh_token=fixture-refresh-token-not-a-credential",
            bodies[1],
            StringComparison.Ordinal);

        // The password is not sent again when a refresh token will do.
        Assert.DoesNotContain("grant_type=password", bodies[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The recovery that stops a revoked refresh token from taking the provider down until someone
    /// restarts the process. A refresh token can be invalidated at any time — by a password change on
    /// the account, or by ACLED — and the response does not state its lifetime, so the provider
    /// cannot know in advance that it has gone.
    /// </summary>
    [Fact]
    public async Task ARefusedRefreshTokenFallsBackToThePasswordGrant()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            Token(),
            ScriptedHttpHandler.Respond(
                Fixture("acled-token-rejected.json"),
                HttpStatusCode.BadRequest,
                "application/json"),
            Token());

        using var host = Build(handler, out var clock);
        var tokens = host.GetRequiredService<AcledTokenProvider>();

        await tokens.GetAccessTokenAsync(stop.Token);
        clock.Advance(TimeSpan.FromHours(25));

        var renewed = await tokens.GetAccessTokenAsync(stop.Token);

        Assert.Equal("fixture-access-token-not-a-credential", renewed);

        var bodies = handler.Exchanges.Select(exchange => exchange.Body ?? string.Empty).ToArray();

        Assert.Equal(3, bodies.Length);
        Assert.Contains("grant_type=password", bodies[0], StringComparison.Ordinal);
        Assert.Contains("grant_type=refresh_token", bodies[1], StringComparison.Ordinal);
        Assert.Contains("grant_type=password", bodies[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// Invalidation keeps the refresh token. The access token was refused early, which says nothing
    /// about the refresh token, and recovering without the password is the better of the two routes.
    /// </summary>
    [Fact]
    public async Task InvalidationDiscardsTheAccessTokenButNotTheRefreshToken()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Token(), Token());
        using var host = Build(handler, out _);
        var tokens = host.GetRequiredService<AcledTokenProvider>();

        await tokens.GetAccessTokenAsync(stop.Token);
        tokens.Invalidate();
        await tokens.GetAccessTokenAsync(stop.Token);

        var bodies = handler.Exchanges.Select(exchange => exchange.Body ?? string.Empty).ToArray();

        Assert.Equal(2, bodies.Length);
        Assert.Contains("grant_type=refresh_token", bodies[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected credential is reported with the reason ACLED gave. The alternative — reporting a
    /// wrong password as zero events — is the failure that is hardest to notice from the dashboard,
    /// and the reason also distinguishes a bad password from a retired endpoint.
    /// </summary>
    [Fact]
    public async Task ARejectedCredentialSurfacesTheProvidersOwnReason()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond(
                Fixture("acled-token-rejected.json"),
                HttpStatusCode.BadRequest,
                "application/json"));

        using var host = Build(handler, out _);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.GetRequiredService<AcledTokenProvider>().GetAccessTokenAsync(stop.Token));

        Assert.Contains("invalid_grant", failure.Message, StringComparison.Ordinal);
        Assert.Contains("The user credentials were incorrect.", failure.Message, StringComparison.Ordinal);

        // The message travels into the logs through the poll failure, so it must not carry the
        // credential it was rejected for.
        Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenResponseWithNoTokenIsAnError()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond("""{"token_type":"Bearer","expires_in":86400}""", mediaType: "application/json"));

        using var host = Build(handler, out _);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.GetRequiredService<AcledTokenProvider>().GetAccessTokenAsync(stop.Token));

        Assert.Contains("no access token", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATokenEndpointAnsweringWithHtmlIsAFormatProblemRatherThanAnEmptyToken()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond("<html>login</html>", mediaType: "text/html"));

        using var host = Build(handler, out _);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.GetRequiredService<AcledTokenProvider>().GetAccessTokenAsync(stop.Token));

        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lifetime the server did not state is treated as short rather than as long. Trusting an
    /// absent or zero <c>expires_in</c> as valid indefinitely would mean every poll after the real
    /// expiry failing until the process restarted.
    /// </summary>
    [Fact]
    public async Task AnUnstatedLifetimeIsTreatedAsShort()
    {
        using var stop = new CancellationTokenSource();
        var noLifetime = ScriptedHttpHandler.Respond(
            """{"token_type":"Bearer","access_token":"fixture-access-token-not-a-credential"}""",
            mediaType: "application/json");

        var handler = new ScriptedHttpHandler(stop, noLifetime, noLifetime);
        using var host = Build(handler, out var clock);
        var tokens = host.GetRequiredService<AcledTokenProvider>();

        await tokens.GetAccessTokenAsync(stop.Token);

        // Well inside ACLED's documented 24 hours, and outside the one hour the provider assumes when
        // the server says nothing.
        clock.Advance(TimeSpan.FromHours(2));
        await tokens.GetAccessTokenAsync(stop.Token);

        Assert.Equal(2, handler.RequestCount);
    }

    /// <summary>
    /// Several readers arriving at once share one token request. The adapter is a singleton and both
    /// the poll loop and a one-shot batch read can be enumerating it, so without the gate each would
    /// authenticate separately and the last one to finish would win.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallersShareOneTokenRequest()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Token());
        using var host = Build(handler, out _);
        var tokens = host.GetRequiredService<AcledTokenProvider>();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => tokens.GetAccessTokenAsync(stop.Token)));

        Assert.All(results, token => Assert.Equal("fixture-access-token-not-a-credential", token));
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// The real provider registration with the transport replaced and the clock under the test's
    /// control, so the resilience pipeline and the redacting logger are the production ones.
    /// </summary>
    private static ServiceProvider Build(HttpMessageHandler handler, out FakeTimeProvider clock)
    {
        var fake = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-12T00:00:00Z", null));
        clock = fake;

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSingleton<Application.Pipeline.PipelineDiagnostics>();
        services.AddOsintProviders();
        services.Configure<ProviderOptions>(options =>
        {
            options.Mode = ProviderMode.Live;
            options.Acled.Enabled = true;
            options.Acled.Username = Username;
            options.Acled.Password = Password;
        });

        services.AddHttpClient(AcledEventSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        // The retry delay is the one production value a test has to override, and here it matters
        // more than usual: the resilience pipeline waits on the real clock, not the fake one, so a
        // two-second backoff would be two real seconds.
        services.Configure<HttpStandardResilienceOptions>(
            $"{AcledEventSource.HttpClientName}-standard",
            options => options.Retry.Delay = TimeSpan.Zero);

        return services.BuildServiceProvider();
    }
}
