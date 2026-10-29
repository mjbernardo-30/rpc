using System.Text;

namespace ReleaseChecker.Core;

/// <summary>
/// A pluggable release check (SQL scripts, config, ...).
/// To add one: implement this interface, return a <see cref="CheckSection"/>, and register it in <see cref="CheckRegistry"/>.
/// </summary>
public interface IReleaseCheck
{
    /// <summary>Which check type argument selects it (All always includes it).</summary>
    CheckMode Mode { get; }

    /// <summary>Section title in the console and the report, e.g. "SQL SCRIPTS".</summary>
    string Title { get; }

    CheckSection Run(CliOptions options, ReleaseRules rules);
}

/// <summary>Result of one check: counts for the summary, plus how to print itself.</summary>
public abstract class CheckSection
{
    public required string Title { get; init; }

    public abstract int Items { get; }     // e.g. scripts checked
    public abstract int Passed { get; }
    public abstract int Failed { get; }
    public abstract int Errors { get; }
    public abstract int Warnings { get; }

    public virtual bool Skipped => false;
    public string Status => Skipped ? "SKIPPED" : Errors > 0 ? "FAIL" : "PASS";

    /// <summary>Section body for the text report (the title/summary header is written by ReportWriter).</summary>
    public abstract void WriteReport(StringBuilder sb);

    /// <summary>Section body for the console.</summary>
    public abstract void WriteConsole();
}

/// <summary>For checks that are registered but not built yet, or have nothing to check.</summary>
public sealed class SkippedSection : CheckSection
{
    public required string Reason { get; init; }

    public override int Items => 0;
    public override int Passed => 0;
    public override int Failed => 0;
    public override int Errors => 0;
    public override int Warnings => 0;
    public override bool Skipped => true;

    public override void WriteReport(StringBuilder sb) => sb.AppendLine(Reason);
    public override void WriteConsole() => Console.WriteLine($"  {Reason}");
}
