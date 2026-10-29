using ReleaseChecker.Checks.Config;
using ReleaseChecker.Checks.Sql;

namespace ReleaseChecker.Core;

/// <summary>All available checks, in the order they run and appear in the report.</summary>
public static class CheckRegistry
{
    private static readonly IReadOnlyList<IReleaseCheck> Checks = new IReleaseCheck[]
    {
        new SqlScriptCheck(),
        new ConfigCheck(),
        // add new checks here
    };

    public static IEnumerable<IReleaseCheck> For(CheckMode mode) =>
        mode == CheckMode.All ? Checks : Checks.Where(c => c.Mode == mode);
}
