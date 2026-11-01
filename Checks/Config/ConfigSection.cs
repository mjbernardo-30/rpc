using System.Text;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Config;

/// <summary>Config results for one country: its applications and their findings.</summary>
public sealed class ConfigCountryResult
{
    public required string Country { get; init; }
    /// <summary>Application paths relative to the environment root, e.g. Online/Web/V2/RIMCoreOrderService.</summary>
    public List<string> Applications { get; } = new();
    public List<Finding> Findings { get; } = new();

    public int Errors => Findings.Count(f => f.Severity == Severity.Error);
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);

    public List<Finding> FindingsFor(string app) =>
        Findings.Where(f => string.Equals(f.File, app, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Severity).ThenBy(f => f.Check).ThenBy(f => f.Message)
                .ToList();

    public bool AppFailed(string app) =>
        Findings.Any(f => f.Severity == Severity.Error && string.Equals(f.File, app, StringComparison.OrdinalIgnoreCase));

    public int AppsFailed => Applications.Count(AppFailed);
    public int AppsPassed => Applications.Count - AppsFailed;

    public List<Finding> CountryLevelFindings => Findings.Where(f => f.File is null).ToList();
}

/// <summary>CONFIG section of the report: one entry per country / application with PASS/FAIL and remarks.</summary>
public sealed class ConfigSection : CheckSection
{
    public required List<ConfigCountryResult> Countries { get; init; }
    public required List<string> Environments { get; init; }
    public required string PathTemplate { get; init; }

    public override int Items => Countries.Sum(c => c.Applications.Count);
    public override int Passed => Countries.Sum(c => c.AppsPassed);
    public override int Failed => Countries.Sum(c => c.AppsFailed);
    public override int Errors => Countries.Sum(c => c.Errors);
    public override int Warnings => Countries.Sum(c => c.Warnings);

    public override void WriteReport(StringBuilder sb)
    {
        sb.AppendLine($"Config path : {PathTemplate}  (applications found below it)");
        sb.AppendLine($"Environments: {string.Join(", ", Environments)}");

        foreach (var c in Countries)
        {
            sb.AppendLine();
            sb.AppendLine($"{c.Country}  [{(c.Errors > 0 ? "FAIL" : "PASS")}]  " +
                          $"{c.Applications.Count} applications: {c.AppsPassed} passed, {c.AppsFailed} failed");
            sb.AppendLine(ReportWriter.Thin);

            foreach (var f in c.CountryLevelFindings)
                sb.AppendLine($"     {f.Remark}");

            int no = 0;
            foreach (var app in c.Applications)
            {
                no++;
                sb.AppendLine($"{no}. {app} - {(c.AppFailed(app) ? "FAIL" : "PASS")}");
                foreach (var f in c.FindingsFor(app))
                    sb.AppendLine($"     {f.Remark}");
            }
        }
    }

    public override void WriteConsole()
    {
        Console.WriteLine($"  Config path : {PathTemplate}");
        Console.WriteLine($"  Environments: {string.Join(", ", Environments)}");

        foreach (var c in Countries)
        {
            Console.WriteLine();
            ConsoleOut.Write($"  {c.Country} ", ConsoleColor.Cyan);
            string status = c.Errors > 0 ? "FAIL" : "PASS";
            ConsoleOut.Write(status, ConsoleOut.StatusColor(status));
            Console.WriteLine($" ({c.Applications.Count} applications: {c.AppsPassed} passed, {c.AppsFailed} failed" +
                              $" | {c.Errors} errors, {c.Warnings} warnings)");

            foreach (var f in c.CountryLevelFindings)
                ConsoleOut.Remark(f);

            int no = 0;
            foreach (var app in c.Applications)
            {
                no++;
                string s = c.AppFailed(app) ? "FAIL" : "PASS";
                Console.Write($"    {no}. {app} - ");
                ConsoleOut.WriteLine(s, ConsoleOut.StatusColor(s));
                foreach (var f in c.FindingsFor(app))
                    ConsoleOut.Remark(f);
            }
        }
    }
}
