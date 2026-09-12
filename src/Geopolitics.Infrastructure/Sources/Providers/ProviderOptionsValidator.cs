using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Rejects provider configuration that would send this process somewhere it should not go.
/// <para>
/// It runs at startup rather than at poll time because a feed URL is a deployment decision, and a
/// deployment decision that is wrong should stop the deployment. Discovering it later means a source
/// that silently produced nothing, which is the failure mode hardest to notice from the dashboard.
/// </para>
/// </summary>
internal sealed class ProviderOptionsValidator : IValidateOptions<ProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        foreach (var feed in options.Rss.Feeds)
        {
            // An empty URL stays a poll-time warning: it names a feed the operator has not finished
            // configuring, which is different from one configured to point somewhere wrong.
            if (string.IsNullOrWhiteSpace(feed.Url))
            {
                continue;
            }

            if (!Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri))
            {
                failures.Add($"RSS feed '{feed.Name}' has a URL that is not absolute: '{feed.Url}'.");
                continue;
            }

            if (uri.Scheme is not (("http") or ("https")))
            {
                failures.Add(
                    $"RSS feed '{feed.Name}' uses the '{uri.Scheme}' scheme. Feeds are fetched over HTTP, and "
                    + "allowing other schemes would let configuration read local files through the same code path.");
            }
        }

        foreach (var (label, provider) in new (string, ProviderOptionsBase)[]
        {
            ("Providers:Rss", options.Rss),
            ("Providers:NasaFirms", options.NasaFirms),
            ("Providers:Acled", options.Acled),
        })
        {
            // A non-positive interval does not mean "poll often". Zero completes the wait
            // immediately, so the loop becomes a hot loop that requests as fast as the network
            // allows — a flood of someone else's API issued in this project's name. A negative value
            // throws from inside the iterator, where only cancellation is handled, which kills the
            // source silently for the lifetime of the process.
            if (provider.PollInterval <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{label}:PollInterval must be greater than zero, but was '{provider.PollInterval}'. "
                    + "A non-positive interval either polls the provider continuously or stops the source outright.");
            }

            // Zero is not a smaller batch, it is a source that fetches on every cycle and throws the
            // answer away, which is indistinguishable from a dead feed on the dashboard.
            if (provider.MaxItemsPerPoll <= 0)
            {
                failures.Add(
                    $"{label}:MaxItemsPerPoll must be at least 1, but was {provider.MaxItemsPerPoll}. "
                    + "A source that emits nothing looks identical to one that is failing.");
            }
        }

        foreach (var (label, address) in new[]
        {
            ("Providers:NasaFirms:BaseAddress", options.NasaFirms.BaseAddress),
            ("Providers:Acled:BaseAddress", options.Acled.BaseAddress),
        })
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not (("http") or ("https")))
            {
                failures.Add($"{label} must be an absolute http or https URL, but was '{address}'.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
