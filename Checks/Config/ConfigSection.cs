using System.Text;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Config;

/// <summary>Config results for one country: its services and their findings.</summary>
public sealed class ConfigCountryResult
{
    public required string Country { get; init; }
    public List<string> Services { get; } = new();
    public List<Finding> Findings { get; } = new();

    public int Errors => Findings.Count(f => f.Severity == Severity.Error);
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);

    public List<Finding> FindingsFor(string service) =>
        Findings.Where(f => string.Equals(f.File, service, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Severity).ThenBy(f => f.Check).ThenBy(f => f.Message)
                .ToList();

    public bool ServiceFailed(string service) =>
        Findings.Any(f => f.Severity == Severity.Error && string.Equals(f.File, service, StringComparison.OrdinalIgnoreCase));

    public int ServicesFailed => Services.Count(ServiceFailed);
    public int ServicesPassed => Services.Count - ServicesFailed;

    public List<Finding> CountryLevelFindings => Findings.Where(f => f.File is null).ToList();
}

/// <summary>CONFIG section of the report: one entry per country / service with PASS/FAIL and remarks.</summary>
public sealed class ConfigSection : CheckSection
{
    public required List<ConfigCountryResult> Countries { get; init; }
    public required List<string> Environments { get; init; }
    public required string PathTemplate { get; init; }

    public override int Items => Countries.Sum(c => c.Services.Count);
    public override int Passed => Countries.Sum(c => c.ServicesPassed);
    public override int Failed => Countries.Sum(c => c.ServicesFailed);
    public override int Errors => Countries.Sum(c => c.Errors);
    public override int Warnings => Countries.Sum(c => c.Warnings);

    public override void WriteReport(StringBuilder sb)
    {
        sb.AppendLine($"Config path : {PathTemplate}");
        sb.AppendLine($"Environments: {string.Join(", ", Environments)}");

        foreach (var c in Countries)
        {
            sb.AppendLine();
            sb.AppendLine($"{c.Country}  [{(c.Errors > 0 ? "FAIL" : "PASS")}]  " +
                          $"{c.Services.Count} services: {c.ServicesPassed} passed, {c.ServicesFailed} failed");
            sb.AppendLine(ReportWriter.Thin);

            foreach (var f in c.CountryLevelFindings)
                sb.AppendLine($"     {f.Remark}");

            int no = 0;
            foreach (var service in c.Services)
            {
                no++;
                sb.AppendLine($"{no}. {service} - {(c.ServiceFailed(service) ? "FAIL" : "PASS")}");
                foreach (var f in c.FindingsFor(service))
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
            Console.WriteLine($" ({c.Services.Count} services: {c.ServicesPassed} passed, {c.ServicesFailed} failed" +
                              $" | {c.Errors} errors, {c.Warnings} warnings)");

            foreach (var f in c.CountryLevelFindings)
                ConsoleOut.Remark(f);

            int no = 0;
            foreach (var service in c.Services)
            {
                no++;
                string s = c.ServiceFailed(service) ? "FAIL" : "PASS";
                Console.Write($"    {no}. {service} - ");
                ConsoleOut.WriteLine(s, ConsoleOut.StatusColor(s));
                foreach (var f in c.FindingsFor(service))
                    ConsoleOut.Remark(f);
            }
        }
    }
}
