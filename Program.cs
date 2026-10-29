using ReleaseChecker.Checks.Sql;
using ReleaseChecker.Core;

var options = CliOptions.Parse(args);
if (options is null)
{
    Console.WriteLine(CliOptions.Usage);
    return 2;
}

var rules = ReleaseRules.Load(options.RulesPath);
if (rules is null)
    return 2;

// ---- SQL helper commands ----
if (options.ListStatements || options.ExplainFile is not null)
{
    SqlRules.Use(rules.Sql);
    if (options.ListStatements)
    {
        StatementInfo.ListAll();
        return 0;
    }
    return StatementInfo.Explain(options.ExplainFile!);
}

if (!Directory.Exists(options.RepoRoot))
{
    Console.Error.WriteLine($"Repo folder not found: {options.RepoRoot}");
    return 2;
}

// ---- run the selected checks ----
Console.WriteLine($"ReleaseChecker - {options.ModeName} - {options.RepoRoot}");

var sections = new List<CheckSection>();
foreach (var check in CheckRegistry.For(options.Mode))
{
    var section = check.Run(options, rules);
    sections.Add(section);

    Console.WriteLine();
    ConsoleOut.Write($"=== {section.Title} ", ConsoleColor.Cyan);
    ConsoleOut.WriteLine(section.Status, ConsoleOut.StatusColor(section.Status));
    section.WriteConsole();
}

ReportWriter.Write(options.ReportPath, options, sections);

int errors = sections.Sum(s => s.Errors);
int warnings = sections.Sum(s => s.Warnings);

Console.WriteLine();
Console.WriteLine("=== SUMMARY");
foreach (var s in sections)
{
    Console.Write($"  {s.Title,-16} ");
    ConsoleOut.Write($"{s.Status,-7}", ConsoleOut.StatusColor(s.Status));
    Console.WriteLine(s.Skipped ? "" : $" {s.Items} checked, {s.Passed} passed, {s.Failed} failed ({s.Errors} errors, {s.Warnings} warnings)");
}
Console.WriteLine($"Report: {Path.GetFullPath(options.ReportPath)}");

return errors > 0 || (options.FailOnWarning && warnings > 0) ? 1 : 0;
