using System.Text;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Sql;

/// <summary>
/// SQL SCRIPTS check: for each &lt;Country&gt;\Database\scripts\&lt;Module&gt;\impl_list.txt,
/// verifies the listed scripts exist and validates their content (syntax, allowlist, ordering).
/// </summary>
public sealed class SqlScriptCheck : IReleaseCheck
{
    public CheckMode Mode => CheckMode.Sql;
    public string Title => "SQL SCRIPTS";

    public CheckSection Run(CliOptions options, ReleaseRules rules)
    {
        SqlRules.Use(rules.Sql);

        var modules = Discover(options);
        if (modules.Count == 0)
        {
            return new SkippedSection
            {
                Title = Title,
                Reason = $"No {options.ImplListFileName} found under {options.RepoRoot} " +
                         @"(expected <Country>\Database\scripts\<Module>\" + options.ImplListFileName + ")."
            };
        }

        foreach (var m in modules)
        {
            var existing = ImplListReconciler.Reconcile(m);
            SqlValidator.Validate(m, existing);
            SqlRules.ApplySeverityOverrides(m);
            SuppressionEngine.Apply(m, options.SuppressionsPath);
        }

        return new SqlSection { Title = Title, Modules = modules };
    }

    /// <summary>Finds &lt;Country&gt;\Database\scripts\&lt;Module&gt;\impl_list.txt.</summary>
    private static List<ModuleResult> Discover(CliOptions options)
    {
        var modules = new List<ModuleResult>();
        if (!Directory.Exists(options.RepoRoot)) return modules;

        var countryDirs = options.Country is not null
            ? new[] { Path.Combine(options.RepoRoot, options.Country) }
            : Directory.GetDirectories(options.RepoRoot).Where(d => Directory.Exists(Path.Combine(d, "Database"))).ToArray();

        foreach (var countryDir in countryDirs.OrderBy(d => d))
        {
            string country = Path.GetFileName(countryDir);
            string scriptsRoot = Path.Combine(countryDir, "Database", "scripts");
            string sqlRoot = Path.Combine(countryDir, "Database", "sql");

            if (!Directory.Exists(scriptsRoot))
            {
                Console.Error.WriteLine($"[{country}] scripts folder not found: {scriptsRoot}");
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
                    Console.Error.WriteLine($"[{country}/{module}] {options.ImplListFileName} not found, skipped.");
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

    public override int Items => Modules.Sum(m => m.ListedScripts.Count);
    public override int Passed => Modules.Sum(m => m.ScriptsPassed);
    public override int Failed => Modules.Sum(m => m.ScriptsFailed);
    public override int Errors => Modules.Sum(m => m.Errors);
    public override int Warnings => Modules.Sum(m => m.Warnings);

    public override void WriteReport(StringBuilder sb)
    {
        foreach (var m in Modules)
        {
            sb.AppendLine();
            sb.AppendLine($"{m.Country} / {m.Module}  [{(m.Errors > 0 ? "FAIL" : "PASS")}]  " +
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
        foreach (var m in Modules)
        {
            Console.WriteLine();
            ConsoleOut.Write($"  {m.Country}/{m.Module} ", ConsoleColor.Cyan);
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
