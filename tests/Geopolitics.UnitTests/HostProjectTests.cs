using System.Xml.Linq;

namespace Geopolitics.UnitTests;

/// <summary>
/// What a host project has to declare in order to be run rather than merely built.
/// <para>
/// This is a lint over project files, in the same place and for the same reason as the committed
/// bundle lint: <c>dotnet test</c> already runs everywhere, so a gate here cannot be forgotten.
/// The specific omission it exists for was silent — the API project had no user-secrets identifier
/// for months, nothing failed, and the only symptom was that the one documented way to give the
/// continuously-running host a credential did not work.
/// </para>
/// </summary>
public sealed class HostProjectTests
{
    private static readonly string Directory =
        Path.Combine(AppContext.BaseDirectory, "host-projects");

    public static TheoryData<string> HostProjects() =>
        new() { "Geopolitics.Api.csproj", "Geopolitics.Workers.csproj" };

    /// <summary>
    /// Both hosts can hold a credential outside the repository.
    /// <para>
    /// <c>dotnet user-secrets</c> writes to a store keyed by this identifier and refuses outright
    /// without one. A project missing it leaves an operator two routes: an environment variable, or
    /// <c>appsettings.json</c> — which is committed. The rule that no credential enters this
    /// repository is only as good as the alternative being available.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(HostProjects))]
    public void AHostProjectDeclaresAUserSecretsIdentifier(string project)
    {
        var identifier = Property(project, "UserSecretsId");

        Assert.False(
            string.IsNullOrWhiteSpace(identifier),
            $"{project} has no UserSecretsId, so 'dotnet user-secrets set' fails against it and the "
            + "only remaining places for a credential are an environment variable or a committed file.");
    }

    /// <summary>
    /// The identifiers differ between hosts, so a secret set for one is not silently read by the
    /// other. Two hosts sharing a store would make "which process has this credential" unanswerable.
    /// </summary>
    [Fact]
    public void TheTwoHostsDoNotShareASecretStore()
    {
        var api = Property("Geopolitics.Api.csproj", "UserSecretsId");
        var workers = Property("Geopolitics.Workers.csproj", "UserSecretsId");

        Assert.NotEqual(api, workers);
    }

    private static string? Property(string project, string name)
    {
        var document = XDocument.Load(Path.Combine(Directory, project));

        return document.Descendants()
            .Where(element => element.Name.LocalName == name)
            .Select(element => element.Value.Trim())
            .FirstOrDefault();
    }
}
