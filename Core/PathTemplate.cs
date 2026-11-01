namespace ReleaseChecker.Core;

/// <summary>
/// Folder patterns such as "{country}/Database" or "{country}/App/Config/{env}".
/// Relative patterns are resolved under --repo; absolute ones are used as-is.
/// {country} is optional: without it the check runs once with no country (e.g. a published collection).
/// </summary>
public static class PathTemplate
{
    public static bool HasToken(string template, string token) =>
        template.Contains("{" + token + "}", StringComparison.OrdinalIgnoreCase);

    public static string Resolve(string repoRoot, string template, string? country = null, string? env = null)
    {
        string t = template.Replace('\\', '/');
        if (country is not null) t = t.Replace("{country}", country, StringComparison.OrdinalIgnoreCase);
        if (env is not null) t = t.Replace("{env}", env, StringComparison.OrdinalIgnoreCase);
        t = t.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(repoRoot, t));
    }

    /// <summary>
    /// Countries to check: [""] when the pattern has no {country}; [only] when --country is given;
    /// otherwise every folder at the {country} position that <paramref name="accept"/> keeps.
    /// </summary>
    public static List<string> Countries(string repoRoot, string template, string? only, Func<string, bool> accept)
    {
        if (!HasToken(template, "country")) return new() { "" };
        if (only is not null) return new() { only };

        string t = template.Replace('\\', '/');
        string baseRel = t[..t.IndexOf("{country}", StringComparison.OrdinalIgnoreCase)].TrimEnd('/');
        string baseDir = baseRel.Length == 0
            ? repoRoot
            : Path.GetFullPath(Path.Combine(repoRoot, baseRel.Replace('/', Path.DirectorySeparatorChar)));

        if (!Directory.Exists(baseDir)) return new();

        return Directory.GetDirectories(baseDir)
            .Select(Path.GetFileName)
            .Where(c => !string.IsNullOrEmpty(c) && accept(c!))
            .Select(c => c!)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
