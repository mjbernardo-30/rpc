using System.Text;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Sql;

/// <summary>
/// SQL SCRIPTS check: for each &lt;dbPath&gt;\scripts\&lt;Module&gt;\impl_list.txt,
/// verifies the listed scripts exist and validates their content (encoding, syntax, allowlist, grants, ordering).
/// dbPath comes from --db-path or rules.json "sql.dbPath" (default "{country}/Database"; {country} is optional).
/// </summary>
public sealed class SqlScriptCheck : IReleaseCheck
{
    public CheckMode Mode => CheckMode.Sql;
    public string Title => "SQL SCRIPTS";

    public CheckSection Run(CliOptions options, ReleaseRules rules)
    {
        SqlRules.Use(rules.Sql);

        string dbPath = options.DbPath ?? rules.Sql.DbPath;
        var modules = Discover(options, rules.Sql, dbPath);
        if (modules.Count == 0)
        {
            return new SkippedSection
            {
                Title = Title,
                Reason = $"No {options.ImplListFileName} found (expected {dbPath}/{rules.Sql.ScriptsFolder}/<Module>/{options.ImplListFileName} under {options.RepoRoot})."
            };
        }

        foreach (var m in modules)
        {
            var existing = ImplListReconciler.Reconcile(m);
            SqlValidator.Validate(m, existing);
            SqlRules.ApplySeverityOverrides(m);
            SuppressionEngine.Apply(m, options.SuppressionsPath);
        }

        return new SqlSection { Title = Title, Modules = modules, DbPath = dbPath };
    }

    /// <summary>Finds &lt;dbPath&gt;\&lt;scriptsFolder&gt;\&lt;Module&gt;\impl_list.txt for each country (or once, without {country}).</summary>
    private static List<ModuleResult> Discover(CliOptions options, RulesConfig cfg, string dbPath)
    {
        var modules = new List<ModuleResult>();

        var countries = PathTemplate.Countries(options.RepoRoot, dbPath, options.Country,
            c => Directory.Exists(Path.Combine(PathTemplate.Resolve(options.RepoRoot, dbPath, c), cfg.ScriptsFolder)));

        if (options.Country is not null && !PathTemplate.HasToken(dbPath, "country"))
            Console.WriteLine($"Note: --country is ignored for SQL because the db path '{dbPath}' has no {{country}}.");

        foreach (var country in countries)
        {
            string dbRoot = PathTemplate.Resolve(options.RepoRoot, dbPath, country);
            string scriptsRoot = Path.Combine(dbRoot, cfg.ScriptsFolder);
            string sqlRoot = Path.Combine(dbRoot, cfg.SqlFolder);
            string label = country.Length == 0 ? dbRoot : country;

            if (!Directory.Exists(scriptsRoot))
            {
                Console.Error.WriteLine($"[{label}] scripts folder not found: {scriptsRoot}");
                continue;
            }

            var moduleDirs = options.Module is not null
                ? new[] { Path.Combine(scriptsRoot, options.Module) }
                : Directory.GetDirectories(scriptsRoot);

            foreach (var moduleDir in moduleDirs.OrderBy(d => d))
            {
                string module = Path.GetFileName(moduleDir);
                string implList = Path.Combine(moduleDir, options.ImplListFileName);

                if (!File.Exists(implList))
                {
                    Console.Error.WriteLine($"[{label}/{module}] {options.ImplListFileName} not found, skipped.");
                    continue;
                }

                modules.Add(new ModuleResult
                {
                    Country = country,
                    Module = module,
                    ImplListPath = implList,
                    SqlRoot = Path.Combine(sqlRoot, module)
                });
            }
        }
        return modules;
    }
}

/// <summary>Result of the SQL check: one entry per impl_list script with PASS/FAIL and remarks.</summary>
public sealed class SqlSection : CheckSection
{
    public required List<ModuleResult> Modules { get; init; }
    public required string DbPath { get; init; }

    public override int Items => Modules.Sum(m => m.ListedScripts.Count);
    public override int Passed => Modules.Sum(m => m.ScriptsPassed);
    public override int Failed => Modules.Sum(m => m.ScriptsFailed);
    public override int Errors => Modules.Sum(m => m.Errors);
    public override int Warnings => Modules.Sum(m => m.Warnings);

    public override void WriteReport(StringBuilder sb)
    {
        sb.AppendLine($"DB path : {DbPath}");

        foreach (var m in Modules)
        {
            sb.AppendLine();
            sb.AppendLine($"{m.Label}  [{(m.Errors > 0 ? "FAIL" : "PASS")}]  " +
                          $"{m.ListedScripts.Count} scripts: {m.ScriptsPassed} passed, {m.ScriptsFailed} failed");
            sb.AppendLine(ReportWriter.Thin);

            int no = 0;
            foreach (var script in m.ListedScripts)
            {
                no++;
                sb.AppendLine($"{no}. {script} - {(m.ScriptFailed(script) ? "FAIL" : "PASS")}");
                foreach (var f in m.FindingsFor(script))
                    sb.AppendLine($"     {f.Remark}");
            }

            var other = m.ModuleLevelFindings;
            if (other.Count > 0)
            {
                sb.AppendLine("Other findings (not tied to a listed script):");
                foreach (var f in other.OrderByDescending(f => f.Severity))
                    sb.AppendLine($"     {f.Remark}{(f.File is null ? "" : $" ({f.File})")}");
            }
        }
    }

    public override void WriteConsole()
    {
        Console.WriteLine($"  DB path : {DbPath}");

        foreach (var m in Modules)
        {
            Console.WriteLine();
            ConsoleOut.Write($"  {m.Label} ", ConsoleColor.Cyan);
            string status = m.Errors > 0 ? "FAIL" : "PASS";
            ConsoleOut.Write(status, ConsoleOut.StatusColor(status));
            Console.WriteLine($" ({m.ListedScripts.Count} scripts: {m.ScriptsPassed} passed, {m.ScriptsFailed} failed" +
                              $" | {m.Errors} errors, {m.Warnings} warnings)");

            int no = 0;
            foreach (var script in m.ListedScripts)
            {
                no++;
                string s = m.ScriptFailed(script) ? "FAIL" : "PASS";
                Console.Write($"    {no}. {script} - ");
                ConsoleOut.WriteLine(s, ConsoleOut.StatusColor(s));
                foreach (var f in m.FindingsFor(script))
                    ConsoleOut.Remark(f);
            }

            var other = m.ModuleLevelFindings;
            if (other.Count > 0)
            {
                Console.WriteLine("    Other findings (not tied to a listed script):");
                foreach (var f in other.OrderByDescending(f => f.Severity))
                    ConsoleOut.Remark(f, showFile: true);
            }
        }
    }
}
