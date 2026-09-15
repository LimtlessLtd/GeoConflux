using System.Text.RegularExpressions;

namespace Geopolitics.UnitTests;

/// <summary>
/// The one rule that makes a self-hosted runner safe on a public repository: nothing a stranger can
/// trigger may run on it.
/// <para>
/// GitHub's own warning is blunt — "forks of your public repository can potentially run dangerous
/// code on your self-hosted runner by creating a pull request" — and it is describing exactly one
/// mechanism. A fork cannot push to this repository's <c>main</c>, cannot dispatch a workflow, and
/// cannot influence a schedule. It <em>can</em> open a pull request. So a workflow that builds on a
/// pull request must run on a machine belonging to somebody who can afford to lose it, which means a
/// GitHub-hosted one.
/// </para>
/// <para>
/// That property currently holds by accident of how the triggers happen to be written, and an
/// accident is not a control. It is a lint here for the same reason the host-project and bundle
/// lints are: <c>dotnet test</c> runs everywhere, so this cannot be forgotten, and the failure it
/// prevents is silent — adding <c>pull_request:</c> to the publish workflow would look like a
/// convenience and would hand arbitrary code execution to anyone with a GitHub account.
/// </para>
/// </summary>
public sealed class WorkflowRunnerTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "workflows");

    /// <summary>Labels GitHub operates. Anything else is somebody's own machine.</summary>
    private static readonly string[] HostedRunners =
        ["ubuntu-latest", "ubuntu-24.04", "ubuntu-22.04", "windows-latest", "macos-latest"];

    public static TheoryData<string> Workflows()
    {
        var data = new TheoryData<string>();

        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.yml"))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Fact]
    public void TheWorkflowsWereActuallyCopiedForThisLintToRead()
    {
        // Without this the theories below pass by iterating nothing, which is the failure mode of
        // every lint that locates its own inputs.
        Assert.NotEmpty(System.IO.Directory.GetFiles(Directory, "*.yml"));
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void AForkTriggerableWorkflowRunsOnlyOnAGitHubHostedRunner(string workflow)
    {
        var content = Strip(File.ReadAllText(Path.Combine(Directory, workflow)));

        if (!IsForkTriggerable(content))
        {
            return;
        }

        foreach (var runner in RunnersIn(content))
        {
            Assert.True(
                HostedRunners.Contains(runner, StringComparer.OrdinalIgnoreCase),
                $"{workflow} can be triggered by a fork's pull request and runs on '{runner}'. "
                + "A stranger's code would execute on that machine. Either drop the pull_request "
                + "trigger or pin the job to a GitHub-hosted runner.");
        }
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void NoWorkflowUsesPullRequestTarget(string workflow)
    {
        // The dangerous sibling of pull_request: it checks out the fork's code while running in the
        // base repository's context, with its secrets and a write-scoped token. There is no use for
        // it here, and the safest time to say so is before somebody reaches for it.
        var content = Strip(File.ReadAllText(Path.Combine(Directory, workflow)));

        Assert.DoesNotContain("pull_request_target", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void EveryThirdPartyActionIsPinnedToACommit(string workflow)
    {
        // A tag is a pointer its owner can move, and an action runs with this workflow's token. On a
        // self-hosted runner it would also run on somebody's own machine, so the pinning that was
        // already policy here matters more than it did.
        var content = Strip(File.ReadAllText(Path.Combine(Directory, workflow)));

        foreach (Match match in Regex.Matches(content, @"uses:\s*(\S+)"))
        {
            var reference = match.Groups[1].Value;

            // A local composite action is this repository's own code, not a third party's.
            if (reference.StartsWith("./", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                Regex.IsMatch(reference, @"@[0-9a-f]{40}$"),
                $"{workflow} uses '{reference}', which is not pinned to a commit SHA.");
        }
    }

    /// <summary>
    /// Whether somebody without write access can cause this workflow to run. Only the pull-request
    /// triggers qualify: a fork cannot push to <c>main</c>, dispatch a workflow, or reach a schedule,
    /// all of which run from this repository's own default branch.
    /// </summary>
    private static bool IsForkTriggerable(string content) =>
        Regex.IsMatch(content, @"^\s{2,}pull_request(_target)?\s*:", RegexOptions.Multiline);

    private static IEnumerable<string> RunnersIn(string content) =>
        Regex.Matches(content, @"runs-on:\s*(.+)")
            .Select(match => match.Groups[1].Value.Trim().Trim('\'', '"'));

    /// <summary>
    /// Removes comments before anything is matched. Every one of these workflows explains itself at
    /// length, and this file's own rule is discussed in those comments — matching prose would make
    /// the lint fire on the sentence describing it.
    /// </summary>
    private static string Strip(string content) =>
        string.Join('\n', content.Split('\n').Select(line =>
        {
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            return hash < 0 ? line : line[..hash];
        }));
}
