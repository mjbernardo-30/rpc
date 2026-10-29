using System.Text.RegularExpressions;

namespace ReleaseChecker.Core;

/// <summary>Case-insensitive wildcard match: * = any characters (including ':' and '/'), ? = one character.</summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string value) =>
        Regex.IsMatch(value,
            "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static bool AnyMatch(IEnumerable<string>? patterns, string value) =>
        patterns is not null && patterns.Any(p => IsMatch(p, value));
}
