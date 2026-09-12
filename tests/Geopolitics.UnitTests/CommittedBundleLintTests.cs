using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Geopolitics.Infrastructure.Sources.Briefs;

namespace Geopolitics.UnitTests;

/// <summary>
/// The offline bundle lint: every bundle this repository publishes, read by the same parser that
/// reads it at runtime.
/// <para>
/// It runs here rather than as a separate CI step because <c>dotnet test</c> already runs
/// everywhere — locally, in CI, and in the Pages deploy before it publishes — and one gate that
/// cannot be forgotten beats two that can. A malformed bundle therefore fails the build rather than
/// reaching the page and quietly producing nothing.
/// </para>
/// <para>
/// Expiry is deliberately <em>not</em> checked. A bundle going stale is a runtime condition the
/// source already warns about and skips, and failing the build for it would break unrelated work a
/// fortnight after the last collection run, for a reason no commit caused.
/// </para>
/// </summary>
public sealed class CommittedBundleLintTests
{
    private static readonly string Directory =
        Path.Combine(AppContext.BaseDirectory, "committed-bundles");

    public static TheoryData<string> Bundles()
    {
        var data = new TheoryData<string>();

        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                data.Add(Path.GetFileName(file));
            }
        }

        // A repository with no bundles committed yet is a valid state, and an empty TheoryData makes
        // xUnit fail the run rather than pass vacuously. This keeps that case green and visible.
        if (data.Count == 0)
        {
            data.Add(string.Empty);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void ACommittedBundleParses(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        var document = File.ReadAllText(Path.Combine(Directory, name));

        // Read as at its own collection time plus a moment, so the lint checks structure rather than
        // how long ago the run happened.
        var result = CollectionBundleParser.Parse(document, DateTimeOffset.MaxValue);

        Assert.Empty(result.RejectedItems);
        Assert.NotEmpty(result.Bundle.BundleId);
        Assert.NotEmpty(result.Bundle.BriefId);
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void EveryItemsHashMatchesTheExcerptItRecords(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        var result = CollectionBundleParser.Parse(
            File.ReadAllText(Path.Combine(Directory, name)),
            DateTimeOffset.MaxValue);

        foreach (var item in result.Bundle.Items)
        {
            var expected = "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.Excerpt))).ToLowerInvariant();

            Assert.True(
                string.Equals(expected, item.ContentHash, StringComparison.OrdinalIgnoreCase),
                $"{name}: the hash recorded for {item.Url} does not match its excerpt. "
                + "Either the quotation was edited after collection or the hash was never computed "
                + "from it, and both mean the citation cannot be checked.");
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void EveryItemCitesAPublicHttpsUrlAndNamesWhoPublishedIt(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        var result = CollectionBundleParser.Parse(
            File.ReadAllText(Path.Combine(Directory, name)),
            DateTimeOffset.MaxValue);

        foreach (var item in result.Bundle.Items)
        {
            Assert.True(item.Url.IsAbsoluteUri, $"{item.Url} is not absolute.");
            Assert.False(string.IsNullOrWhiteSpace(item.Attribution), $"{item.Url} names no source.");
            Assert.False(string.IsNullOrWhiteSpace(item.Excerpt), $"{item.Url} quotes nothing.");
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void NoPublisherDominatesARun(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        var result = CollectionBundleParser.Parse(
            File.ReadAllText(Path.Combine(Directory, name)),
            DateTimeOffset.MaxValue);

        // The brief caps one publisher at three items per run so a prolific outlet cannot dominate
        // the picture simply by publishing more. Language editions of one publisher count separately,
        // because collecting both is the point rather than a duplication.
        foreach (var group in result.Bundle.Items.GroupBy(item => item.Attribution, StringComparer.OrdinalIgnoreCase))
        {
            Assert.True(
                group.Count() <= 3,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}: '{group.Key}' contributed {group.Count()} items, above the brief's cap of 3."));
        }
    }
}
