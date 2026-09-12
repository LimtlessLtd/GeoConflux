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
