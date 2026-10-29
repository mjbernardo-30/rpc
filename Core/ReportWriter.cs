using System.Text;

namespace ReleaseChecker.Core;

/// <summary>Writes the ReleaseChecker text report: overall summary, then one section per check.</summary>
public static class ReportWriter
{
    public const string Line = "================================================================================";
    public const string Thin = "--------------------------------------------------------------------------------";

    public static void Write(string path, CliOptions options, IReadOnlyList<CheckSection> sections)
    {
        int errors = sections.Sum(s => s.Errors);
        int warnings = sections.Sum(s => s.Warnings);

        var sb = new StringBuilder();
        sb.AppendLine(Line);
        sb.AppendLine("ReleaseChecker");
        sb.AppendLine(Line);
        sb.AppendLine($"Repo      : {options.RepoRoot}");
        sb.AppendLine($"Checks    : {options.ModeName}");
        sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Result    : {(errors > 0 ? "FAIL" : "PASS")}");
        sb.AppendLine($"Errors    : {errors}");
        sb.AppendLine($"Warnings  : {warnings}");
        sb.AppendLine();

        sb.AppendLine("Summary:");
        foreach (var s in sections)
        {
            sb.AppendLine(s.Skipped
                ? $"  {s.Title,-16} {s.Status}"
                : $"  {s.Title,-16} {s.Status,-5}  {s.Items} checked, {s.Passed} passed, {s.Failed} failed");
        }

        foreach (var s in sections)
        {
            sb.AppendLine();
            sb.AppendLine(Line);
            sb.AppendLine($"{s.Title}  [{s.Status}]");
            sb.AppendLine(Line);
            s.WriteReport(sb);
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
