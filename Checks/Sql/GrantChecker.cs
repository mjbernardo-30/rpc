using System.Text.RegularExpressions;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Sql;

/// <summary>"grantCheck" section inside "sql" in rules.json.</summary>
public sealed class GrantCheckConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Which impl_list entries are grant scripts: file name (GrantAccess.sql) or relative path; "*" wildcards allowed.</summary>
    public List<string> GrantScripts { get; set; } = new() { "GrantAccess.sql" };

    /// <summary>Report GRANT_SCRIPT_NOT_LISTED when objects are created but no grant script is in impl_list.</summary>
    public bool RequireGrantScript { get; set; } = true;

    /// <summary>Object type (Table, Procedure, View, Function) -> principals and the permissions each needs.</summary>
    public Dictionary<string, List<GrantRequirement>> Required { get; set; } = new()
    {
        ["Table"] = new()
        {
            new() { Principal = "SEL", Permissions = new() { "SELECT" } },
            new() { Principal = "RIM", Permissions = new() { "SELECT", "INSERT", "UPDATE", "DELETE" } },
        },
        ["Procedure"] = new()
        {
            new() { Principal = "RIM", Permissions = new() { "EXECUTE" } },
        },
    };

    /// <summary>Objects that don't need grants, e.g. "*Stg" or "dbo.AuditLog". Matched on schema.name or name.</summary>
    public List<string> ExcludeObjects { get; set; } = new();
}

public sealed class GrantRequirement
{
    public string Principal { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// For every table / procedure created by the module's scripts, checks the grant script grants the
/// configured permissions. Findings are attached to the grant script, so it FAILS when a grant is missing.
/// GRANT ... ON SCHEMA::x covers every object in that schema; GRANT ALL covers every permission.
/// </summary>
internal static class GrantChecker
{
    public static void Check(ModuleResult module, List<(string File, ScriptAnalyzer Analyzer)> analyses)
    {
        var cfg = SqlRules.Current.GrantCheck;
        if (cfg is null || !cfg.Enabled) return;

        var required = new Dictionary<string, List<GrantRequirement>>(cfg.Required ?? new(), StringComparer.OrdinalIgnoreCase);

        // objects that need grants, in impl_list order (CREATE OR ALTER re-runs are listed once)
        var created = analyses
            .SelectMany(a => a.Analyzer.CreatedObjects.Select(o => (Obj: o, a.File)))
            .Where(x => required.ContainsKey(x.Obj.Type) && !IsExcluded(x.Obj.Name, cfg.ExcludeObjects))
            .GroupBy(x => x.Obj.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (created.Count == 0) return;

        // grant scripts listed in impl_list
        var grantListed = module.ListedScripts.Where(s => IsGrantScript(s, cfg.GrantScripts)).ToList();
        if (grantListed.Count == 0)
        {
            if (cfg.RequireGrantScript)
            {
                module.Findings.Add(new(Severity.Warning, RuleIds.GrantScriptNotListed, "Grant", null, null,
                    $"{created.Count} object(s) created ({string.Join(", ", created.Select(c => c.Obj.Name))}) " +
                    $"but no grant script ({string.Join(" / ", cfg.GrantScripts)}) is listed in impl_list."));
            }
            return;
        }

        // grant scripts that couldn't be analysed (missing file / syntax error) already have an error - don't pile on
        var grantAnalyses = analyses.Where(a => grantListed.Contains(a.File, StringComparer.OrdinalIgnoreCase)).ToList();
        if (grantAnalyses.Count == 0) return;

        var grants = grantAnalyses.SelectMany(a => a.Analyzer.Grants).ToList();
        string reportOn = grantAnalyses[0].File;   // finding goes on the (first) grant script

        foreach (var (obj, createdIn) in created)
        {
            string schema = obj.Name.Split('.')[0];

            foreach (var req in required[obj.Type])
            {
                var missing = req.Permissions
                    .Select(p => p.Trim().ToUpperInvariant())
                    .Where(p => !grants.Any(g => Covers(g, obj.Name, schema, p, req.Principal)))
                    .ToList();

                if (missing.Count == 0) continue;

                string sql = $"GRANT {string.Join(", ", missing)} ON {obj.Name} TO [{req.Principal}]";
                module.Findings.Add(new(Severity.Error, RuleIds.GrantMissing, "Grant", reportOn, null,
                    $"{obj.Type} {obj.Name} (created in {createdIn}) is missing: {sql}", sql));
            }
        }
    }

    private static bool Covers(GrantInfo g, string objectName, string schema, string permission, string principal) =>
        string.Equals(g.Principal, principal.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) &&
        (g.Permission == permission || g.Permission == "ALL" || g.Permission == "ALL PRIVILEGES") &&
        (g.OnSchema
            ? string.Equals(g.Target, schema, StringComparison.OrdinalIgnoreCase)
            : string.Equals(g.Target, objectName, StringComparison.OrdinalIgnoreCase));

    private static bool IsGrantScript(string listed, List<string> patterns) =>
        patterns.Any(p => Glob(p, p.Contains('/') || p.Contains('\\') ? listed : Path.GetFileName(listed)));

    private static bool IsExcluded(string objectName, List<string> patterns)
    {
        string baseName = objectName[(objectName.IndexOf('.') + 1)..];
        return patterns.Any(p => Glob(p, objectName) || Glob(p, baseName));
    }

    private static bool Glob(string pattern, string value)
    {
        pattern = ImplListReconciler.Normalize(pattern);
        value = ImplListReconciler.Normalize(value);
        string regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }
}
