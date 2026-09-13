using System.Net;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace Geopolitics.UnitTests;

/// <summary>
/// The UCDP adapter and its parser.
/// <para>
/// UCDP earns its place beside ACLED rather than behind it because of one field. <c>where_prec</c> is
/// an explicit statement of how precisely each coordinate is known, and carrying it is what lets a
/// borrowed coordinate be drawn honestly instead of being either over-trusted or thrown away. Most of
/// what is asserted below is that this survives the trip from the payload to the envelope.
/// </para>
/// </summary>
public sealed class UcdpProviderTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    private static Func<HttpRequestMessage, HttpResponseMessage> Events() =>
        ScriptedHttpHandler.Respond(Fixture("ucdp-gedevents.json"), mediaType: "application/json");

    [Fact]
    public void TheParserReadsCodedEventsAndSkipsUnusableRows()
    {
        var events = UcdpResponseParser.Parse(Fixture("ucdp-gedevents.json"));

        // Five rows, one without any identifier. A record that cannot be identified cannot be
        // deduplicated, so it is dropped rather than guessed at.
        Assert.Equal(4, events.Count);
        Assert.Equal("900001", events[0].Identifier);
    }

    [Fact]
    public void TheParserAcceptsNumbersSentAsStringsOrAsNumbers()
    {
        var events = UcdpResponseParser.Parse(Fixture("ucdp-gedevents.json"));

        Assert.Equal(48.2794, events[0].Latitude!.Value, 4);
        Assert.Equal(15.4625, events[1].Latitude!.Value, 4);
        Assert.Equal(9, events[1].Deaths);
    }

    /// <summary>
    /// The whole reason for this adapter, asserted against the GED codebook's own definitions: 1 is
    /// the exact location, 2 is within about 25 km of a coded known point, 4 is a first-order
    /// administrative centroid, and 6 is the country alone.
    /// </summary>
    [Fact]
    public void WherePrecisionIsCarriedOntoThisSystemsScale()
    {
        var events = UcdpResponseParser.Parse(Fixture("ucdp-gedevents.json"));

        Assert.Equal(LocationPrecision.Exact, events[0].Precision);
        Assert.Equal(LocationPrecision.Settlement, events[1].Precision);
        Assert.Equal(LocationPrecision.Region, events[2].Precision);
        Assert.Equal(LocationPrecision.Country, events[3].Precision);
    }

    /// <summary>
    /// The codebook notes <c>where_prec</c> is only populated for data collected since 2013, so an
    /// absent value is a real case rather than a defensive branch — and the honest reading of "the
    /// provider did not say" is not "exact".
    /// </summary>
    [Fact]
    public void AnAbsentPrecisionIsNotReadAsExact()
    {
        var events = UcdpResponseParser.Parse(
            """{"Result":[{"id":1,"source_article":"No precision stated.","latitude":1.0,"longitude":2.0,"country":"Yemen"}]}""");

        Assert.Equal(LocationPrecision.Region, Assert.Single(events).Precision);
    }

    [Fact]
    public void ViolenceTypesMapOntoThisSystemsTaxonomy()
    {
        var events = UcdpResponseParser.Parse(Fixture("ucdp-gedevents.json"));

        // State-based and non-state conflict are both conflict; one-sided violence is violence
        // against civilians, which is where ACLED's equivalent category lands too.
        Assert.Equal(EventType.Conflict, events[0].EventType);
        Assert.Equal(EventType.Terrorism, events[1].EventType);
        Assert.Equal(EventType.Conflict, events[2].EventType);
    }

    /// <summary>
    /// ACLED and UCDP derive severity from the same fatality bands, so a five-death event reads the
    /// same whichever dataset coded it and a reader comparing two markers compares like with like.
    /// </summary>
    [Fact]
    public void SeverityUsesTheSameFatalityBandsAsAcled()
    {
        var ucdp = UcdpResponseParser.Parse(Fixture("ucdp-gedevents.json"));
        var acled = AcledResponseParser.Parse(Fixture("acled-response.json"));

        Assert.Equal(Severity.Medium, ucdp[0].Severity);
        Assert.Equal(Severity.High, ucdp[1].Severity);
        Assert.Equal(Severity.Critical, ucdp[2].Severity);

        // The same counts land in the same bands across the two adapters.
        Assert.Equal(
            acled.Single(record => record.Fatalities == 7).Severity,
            ucdp.Single(record => record.Deaths == 9).Severity);
        Assert.Equal(
            acled.Single(record => record.Fatalities == 31).Severity,
            ucdp.Single(record => record.Deaths == 31).Severity);
    }

    [Fact]
    public void ANonJsonResponseIsAFormatProblem() =>
        Assert.Throws<FormatException>(() => UcdpResponseParser.Parse("API token required."));

    [Fact]
    public void AResponseWithoutAResultArrayIsAFormatProblem() =>
        Assert.Throws<FormatException>(() => UcdpResponseParser.Parse("""{"TotalCount":0}"""));

    [Fact]
    public async Task EventsArriveWithTheProvidersOwnCodingAndPrecisionDeclared()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, LiveUcdp);

        var envelopes = await DrainAsync(Source(provider), stop.Token);

        Assert.Equal(4, envelopes.Count);
        Assert.All(envelopes, envelope => Assert.Equal(ObservationKind.ExternalEvent, envelope.Kind));
        Assert.All(envelopes, envelope => Assert.Equal("ucdp", envelope.SourceName));

        Assert.Equal(LocationPrecision.Exact, envelopes[0].DeclaredPrecision);
        Assert.Equal(LocationPrecision.Country, envelopes[3].DeclaredPrecision);

        // Country names mapped to the alpha-2 codes the rest of the system uses.
        Assert.Equal("UA", envelopes[0].DeclaredCountryCode);
        Assert.Equal("YE", envelopes[1].DeclaredCountryCode);
        Assert.Equal("ET", envelopes[2].DeclaredCountryCode);
    }

    /// <summary>
    /// The token is a header, which is UCDP's design and a helpful one: it keeps the credential out
    /// of the request line that logging and proxies write down. The header name was confirmed against
    /// the live endpoint, which answers an unauthenticated request by naming it.
    /// </summary>
    [Fact]
    public async Task TheAccessTokenTravelsAsAHeaderRatherThanInTheUrl()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, LiveUcdp);

        await DrainAsync(Source(provider), stop.Token);

        var request = handler.Requests.First();

        Assert.StartsWith("https://ucdpapi.pcr.uu.se/api/gedevents/26.0.7?", request, StringComparison.Ordinal);
        Assert.DoesNotContain("test-ucdp-token", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAccessTokenNeverReachesTheLogs()
    {
        using var stop = new CancellationTokenSource();
        var logs = new RecordingLoggerProvider();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, LiveUcdp, logs);

        await DrainAsync(Source(provider), stop.Token);

        Assert.DoesNotContain("test-ucdp-token", logs.Transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// Countries are Gleditsch and Ward numbers rather than ISO codes or names, which is why none are
    /// configured by default: a wrong number is a silently wrong country.
    /// </summary>
    [Fact]
    public async Task ConfiguredCountriesAreRequestedAsGleditschAndWardNumbers()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, options =>
        {
            LiveUcdp(options);
            options.Ucdp.Countries.Add(369);
            options.Ucdp.Countries.Add(678);
            options.Ucdp.Countries.Add(530);
        });

        await DrainAsync(Source(provider), stop.Token);

        Assert.Contains("Country=369%2C678%2C530", handler.Requests.First(), StringComparison.Ordinal);
    }

    /// <summary>
    /// UCDP states how many pages a query matched and links to the next one, so completeness needs
    /// no inference here. The fixture is the case a row-count heuristic gets wrong: two rows back,
    /// and an envelope saying the query matched four hundred.
    /// </summary>
    [Fact]
    public void CompletenessIsReadFromUcdpsOwnEnvelopeRatherThanFromHowManyRowsArrived()
    {
        var complete = UcdpResponseParser.ParsePage(Fixture("ucdp-gedevents.json"));
        var paged = UcdpResponseParser.ParsePage(Fixture("ucdp-gedevents-paged.json"));

        Assert.False(complete.Truncated);
        Assert.True(paged.Truncated);

        // The rows that did arrive are still read. A truncated answer is a partial answer, not a
        // failed one.
        Assert.Equal(2, paged.Events.Count);
    }

    /// <summary>
    /// A link in a response is a fact about the response, not an instruction. Following one would
    /// let a provider — or anything that could answer as one — choose the next address this process
    /// dials, which is the same reason redirects are judged at connection time rather than trusted.
    /// </summary>
    [Fact]
    public async Task TheNextPageLinkIsReadAsASignalAndNeverDialled()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond(Fixture("ucdp-gedevents-paged.json"), mediaType: "application/json"));

        using var provider = Build(handler, LiveUcdp);

        await DrainAsync(Source(provider), stop.Token);

        Assert.All(handler.Requests, request =>
            Assert.DoesNotContain("page=2", request, StringComparison.Ordinal));
    }

    /// <summary>
    /// The request that makes history readable at all. Sending only <c>StartDate</c> asked for
    /// everything from that date to the end of the dataset, which is a fine way to poll for recent
    /// events and no way at all to ask about a bounded slice of 2019.
    /// </summary>
    [Fact]
    public async Task ARequestAsksForABoundedWindowRatherThanEverythingSinceADate()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, LiveUcdp);

        await DrainAsync(Source(provider), stop.Token);

        var request = handler.Requests.First();

        Assert.Contains("StartDate=", request, StringComparison.Ordinal);
        Assert.Contains("EndDate=", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCountryFilterIsSentWhenNoneIsConfigured()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, LiveUcdp);

        await DrainAsync(Source(provider), stop.Token);

        Assert.DoesNotContain("Country=", handler.Requests.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAdapterStaysDormantWithoutAToken()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, options =>
        {
            options.Mode = ProviderMode.Live;
            options.Ucdp.Enabled = true;
        });

        var envelopes = await DrainAsync(Source(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TheAdapterMakesNoRequestUnderTheConfigurationShippedInTheRepository()
    {
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHttpHandler(stop, Events());
        using var provider = Build(handler, options =>
        {
            options.Mode = ProviderMode.Demo;
            options.Ucdp.AccessToken = "test-ucdp-token";
        });

        var envelopes = await DrainAsync(Source(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>
    /// A rejected token must not look like a quiet day. UCDP answers without one in plain text, which
    /// is not a response this parser can read — and saying so is the point.
    /// </summary>
    [Fact]
    public async Task ARejectedTokenIsReportedRatherThanReadAsNoEvents()
    {
        using var stop = new CancellationTokenSource();
        var logs = new RecordingLoggerProvider();
        var handler = new ScriptedHttpHandler(
            stop,
            ScriptedHttpHandler.Respond(
                "API token required. Add header: x-ucdp-access-token: <your-token>",
                HttpStatusCode.Unauthorized,
                "text/plain"));

        using var provider = Build(handler, LiveUcdp, logs);

        var envelopes = await DrainAsync(Source(provider), stop.Token);

        Assert.Empty(envelopes);
        Assert.Contains("failed this poll", logs.Transcript, StringComparison.Ordinal);
    }

    private static void LiveUcdp(ProviderOptions options)
    {
        options.Mode = ProviderMode.Live;
        options.Ucdp.Enabled = true;
        options.Ucdp.AccessToken = "test-ucdp-token";
        options.Ucdp.PollInterval = TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// The adapter as the container composed it, picked out of the registered sources by type so the
    /// registration itself is part of what these tests exercise.
    /// </summary>
    private static UcdpEventSource Source(IServiceProvider provider) =>
        provider.GetServices<IEventSource>().OfType<UcdpEventSource>().Single();

    private static async Task<List<ObservationEnvelope>> DrainAsync(
        UcdpEventSource source,
        CancellationToken cancellationToken)
    {
        var envelopes = new List<ObservationEnvelope>();

        try
        {
            await foreach (var envelope in source.ReadAsync(cancellationToken))
            {
                envelopes.Add(envelope);
            }
        }
        catch (OperationCanceledException)
        {
            // The scripted transport ends the run by cancelling. Reaching the end of the script is
            // the success condition, not a failure.
        }

        return envelopes;
    }

    private static ServiceProvider Build(
        HttpMessageHandler handler,
        Action<ProviderOptions> configure,
        RecordingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(logging =>
        {
            if (logs is not null)
            {
                logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs);
            }
        });

        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Application.Pipeline.PipelineDiagnostics>();
        services.AddOsintProviders();
        services.Configure(configure);

        services.AddHttpClient(UcdpEventSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        services.Configure<HttpStandardResilienceOptions>(
            $"{UcdpEventSource.HttpClientName}-standard",
            options => options.Retry.Delay = TimeSpan.Zero);

        return services.BuildServiceProvider();
    }
}
