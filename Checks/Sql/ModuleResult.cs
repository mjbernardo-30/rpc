using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Sql;

/// <summary>One module = one folder under Database/scripts (e.g. DRIM), paired with Database/sql/&lt;module&gt;.</summary>
public sealed class ModuleResult
{
    public required string Country { get; init; }
    public required string Module { get; init; }
    public required string ImplListPath { get; init; }
    public required string SqlRoot { get; init; }
    public string ScriptsDir => Path.GetDirectoryName(ImplListPath)!;

    public List<string> ListedScripts { get; } = new();
    public List<Finding> Findings { get; } = new();

    public IEnumerable<Finding> Active => Findings.Where(f => !f.IsSuppressed);
    public IEnumerable<Finding> Suppressed => Findings.Where(f => f.IsSuppressed);

    public int Errors => Active.Count(f => f.Severity == Severity.Error);
    public int Warnings => Active.Count(f => f.Severity == Severity.Warning);

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(ImplListReconciler.Normalize(a), ImplListReconciler.Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>All findings (open and accepted) for one script, matched on the impl_list path.</summary>
    public List<Finding> FindingsFor(string script) =>
        Findings.Where(f => SamePath(f.File, script))
                .OrderByDescending(f => !f.IsSuppressed).ThenByDescending(f => f.Severity).ThenBy(f => f.Line)
                .ToList();

    /// <summary>A script FAILS if it has at least one open error. Warnings don't fail it.</summary>
    public bool ScriptFailed(string script) =>
        Findings.Any(f => !f.IsSuppressed && f.Severity == Severity.Error && SamePath(f.File, script));

    public int ScriptsFailed => ListedScripts.Count(ScriptFailed);
    public int ScriptsPassed => ListedScripts.Count - ScriptsFailed;

    /// <summary>Open findings not tied to a listed script (empty impl_list, missing sql folder, suppression file...).</summary>
    public List<Finding> ModuleLevelFindings =>
        Active.Where(f => !ListedScripts.Any(s => SamePath(f.File, s))).ToList();
}
