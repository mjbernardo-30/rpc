using System.Text.Json;
using System.Text.RegularExpressions;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Sql;

/// <summary>sqlcheck.suppressions.json - findings accepted "by design", with a reason for the audit trail.</summary>
public sealed class SuppressionFile
{
    public List<Suppression> Suppressions { get; set; } = new();
}

public sealed class Suppression
{
    /// <summary>Rule ID, e.g. UPDATE_NO_WHERE (see the report's Rule column).</summary>
    public string Rule { get; set; } = "";

    /// <summary>Script path relative to Database\sql\&lt;Module&gt;, same as impl_list. "*" wildcards allowed.</summary>
    public string File { get; set; } = "*";

    /// <summary>Optional: text that must appear in the offending statement (survives line shifts - preferred over Line).</summary>
    public string? Contains { get; set; }

    /// <summary>Optional: exact line number. Breaks when the script changes, so prefer Contains.</summary>
    public int? Line { get; set; }

    /// <summary>Optional, only needed in a shared --suppressions file covering several modules.</summary>
    public string? Country { get; set; }
    public string? Module { get; set; }

    /// <summary>Required. Why this is acceptable.</summary>
    public string Reason { get; set; } = "";
    public string? ApprovedBy { get; set; }

    /// <summary>Optional yyyy-MM-dd. After this date the suppression stops applying.</summary>
    public DateOnly? Expires { get; set; }
}

public static class SuppressionEngine
{
    public static void Apply(ModuleResult module, string? sharedFilePath)
    {
        string path = sharedFilePath ?? Path.Combine(module.ScriptsDir, SqlRules.Current.SuppressionFileName);
        if (!File.Exists(path)) return;

        string displayPath = sharedFilePath is null
            ? $"scripts/{module.Module}/{SqlRules.Current.SuppressionFileName}"
            : Path.GetFileName(path);

        List<Suppression> suppressions;
        try
        {
            suppressions = JsonSerializer.Deserialize<SuppressionFile>(File.ReadAllText(path), SqlRules.JsonOptions)?.Suppressions ?? new();
        }
        catch (JsonException ex)
        {
            module.Findings.Add(new(Severity.Error, RuleIds.SuppressionFileInvalid, "Suppression", displayPath, null,
                $"Suppression file is not valid JSON: {ex.Message}"));
            return;
        }

        // only the entries for this module
        suppressions = suppressions.Where(s =>
            (s.Country is null || s.Country.Equals(module.Country, StringComparison.OrdinalIgnoreCase)) &&
            (s.Module is null || s.Module.Equals(module.Module, StringComparison.OrdinalIgnoreCase))).ToList();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var used = new HashSet<Suppression>();
        var extra = new List<Finding>();

        foreach (var f in module.Findings.ToList())
        {
            if (f.IsSuppressed || f.RuleId == RuleIds.SyntaxError) continue;

            var match = suppressions.FirstOrDefault(s => Matches(s, f));
            if (match is null) continue;
            used.Add(match);

            string where = $"{match.Rule} on {match.File}";

            if (string.IsNullOrWhiteSpace(match.Reason))
            {
                extra.Add(new(Severity.Warning, RuleIds.SuppressionNoReason, "Suppression", displayPath, null,
                    $"Suppression for {where} has no reason - not applied."));
                continue;
            }

            if (match.Expires is { } exp && exp < today)
            {
                extra.Add(new(Severity.Warning, RuleIds.SuppressionExpired, "Suppression", displayPath, null,
                    $"Suppression for {where} expired on {exp:yyyy-MM-dd} - not applied."));
                continue;
            }

            if (f.Severity == Severity.Error && !SqlRules.Current.AllowSuppressingErrors)
            {
                extra.Add(new(Severity.Warning, RuleIds.SuppressionNotPermitted, "Suppression", displayPath, null,
                    $"Suppression for {where} targets an ERROR; errors can't be suppressed (allowSuppressingErrors=false)."));
                continue;
            }

            f.SuppressedReason = match.Reason;
            f.SuppressedBy = match.ApprovedBy;
        }

        // stale entries: the script was fixed or renamed, so the waiver should be removed
        foreach (var s in suppressions.Where(s => !used.Contains(s)))
        {
            extra.Add(new(Severity.Warning, RuleIds.SuppressionUnused, "Suppression", displayPath, null,
                $"Suppression for {s.Rule} on {s.File}{(s.Contains is null ? "" : $" ('{s.Contains}')")} matched nothing - remove it if no longer needed."));
        }

        module.Findings.AddRange(extra);
    }

    private static bool Matches(Suppression s, Finding f)
    {
        if (!s.Rule.Equals(f.RuleId, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.File is null || !GlobMatch(s.File, f.File)) return false;
        if (s.Line is not null && s.Line != f.Line) return false;

        if (!string.IsNullOrWhiteSpace(s.Contains))
        {
            string haystack = Collapse(f.Snippet ?? f.Message);
            if (!haystack.Contains(Collapse(s.Contains), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool GlobMatch(string pattern, string path)
    {
        pattern = ImplListReconciler.Normalize(pattern);
        path = ImplListReconciler.Normalize(path);
        if (pattern == "*") return true;

        string regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase);
    }

    // so "UPDATE dbo.ConfigParam   SET" matches "update dbo.ConfigParam SET"
    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
