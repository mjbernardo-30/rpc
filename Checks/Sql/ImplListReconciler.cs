using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Sql;

/// <summary>
/// Reads impl_list.txt and checks each listed script exists. Only listed scripts are looked at:
/// no folder scanning, so the cost is one File.Exists per entry.
///  - listed script missing on disk
///  - duplicate entries
/// Returns the scripts that exist, in impl_list order, for content validation.
/// </summary>
public static class ImplListReconciler
{
    public static List<(string Listed, string FullPath)> Reconcile(ModuleResult module)
    {
        var existing = new List<(string, string)>();

        if (!Directory.Exists(module.SqlRoot))
        {
            module.Findings.Add(new(Severity.Error, RuleIds.ImplSqlFolderMissing, "ImplList", null, null,
                $"SQL folder not found: {module.SqlRoot}"));
        }

        var lines = File.ReadAllLines(module.ImplListPath);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith("--")) continue;

            string listed = Normalize(raw);
            int lineNo = i + 1;

            if (seen.TryGetValue(listed, out int firstLine))
            {
                module.Findings.Add(new(Severity.Error, RuleIds.ImplDuplicate, "ImplList", listed, lineNo,
                    $"Duplicate entry (already listed on line {firstLine}) - script would run twice."));
                continue;
            }
            seen[listed] = lineNo;
            module.ListedScripts.Add(listed);

            if (!listed.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                module.Findings.Add(new(Severity.Warning, RuleIds.ImplNotSql, "ImplList", listed, lineNo, "Entry does not end with .sql"));

            string fullPath = Path.Combine(module.SqlRoot, listed.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                module.Findings.Add(new(Severity.Error, RuleIds.ImplMissingFile, "ImplList", listed, lineNo,
                    "Listed in impl_list but file not found."));
                continue;
            }

            existing.Add((listed, fullPath));
        }

        if (module.ListedScripts.Count == 0)
            module.Findings.Add(new(Severity.Warning, RuleIds.ImplEmpty, "ImplList", null, null, "impl_list is empty."));

        return existing;
    }

    public static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/').Trim();
}
