using Geopolitics.Domain;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// The fatality bands the coded-event providers share.
/// <para>
/// ACLED and UCDP both publish a death count and neither publishes a severity, so both adapters have
/// to derive one. Deriving it from the same table is what lets the two appear on one map and mean the
/// same thing: a five-death event reads the same whichever dataset coded it, and a reader comparing
/// two markers is comparing like with like. Two private copies of these numbers would drift, and the
/// drift would be invisible — the map would still render, it would just quietly be lying about which
/// events were worse than which.
/// </para>
/// <para>
/// It is a stated, auditable rule rather than a judgement. The provider supplies a number and the
/// number maps to a band; it is deliberately not a model output, because the provider already knows
/// the fact that matters and inferring it from prose would be strictly worse.
/// </para>
/// </summary>
internal static class ConflictSeverity
{
    public static Severity FromDeaths(int deaths) => deaths switch
    {
        >= 25 => Severity.Critical,
        >= 5 => Severity.High,
        >= 1 => Severity.Medium,
        _ => Severity.Low,
    };
}
