namespace ReleaseChecker.Core;

public enum Severity { Info, Warning, Error }

/// <summary>One issue found by any check. RuleId is what severity overrides and suppressions refer to.</summary>
public sealed class Finding
{
    public Finding(Severity severity, string ruleId, string check, string? file, int? line, string message, string? snippet = null)
    {
        Severity = severity;
        RuleId = ruleId;
        Check = check;
        File = file;
        Line = line;
        Message = message;
        Snippet = snippet;
    }

    public Severity Severity { get; set; }
    public string RuleId { get; }
    public string Check { get; }        // sub-area inside a check, e.g. ImplList, Syntax, Rule, Ordering
    public string? File { get; }        // item the finding belongs to (for SQL: path as in impl_list)
    public int? Line { get; }
    public string Message { get; }
    public string? Snippet { get; }     // offending text (used by suppression "contains")

    /// <summary>Optional detail, e.g. the ScriptDom class name for SQL findings.</summary>
    public string? ClassName { get; init; }

    // set when a suppression accepted this finding
    public string? SuppressedReason { get; set; }
    public string? SuppressedBy { get; set; }
    public bool IsSuppressed => SuppressedReason is not null;

    public string Tag => ClassName is null ? $"[{RuleId}]" : $"[{RuleId} | {ClassName}]";

    /// <summary>"- Line 6: message [RULE_ID]" for errors, "Warning: ..." / "Accepted: ..." otherwise.</summary>
    public string Remark
    {
        get
        {
            string at = Line is null ? "" : $"Line {Line}: ";

            if (IsSuppressed)
                return $"Accepted: {at}{Message} {Tag} - {SuppressedReason}" +
                       (SuppressedBy is null ? "" : $" (approved by {SuppressedBy})");

            return Severity switch
            {
                Severity.Error => $"- {at}{Message} {Tag}",
                Severity.Warning => $"Warning: {at}{Message} {Tag}",
                _ => $"Info: {at}{Message} {Tag}"
            };
        }
    }
}
