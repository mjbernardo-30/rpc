using System.Data.Common;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Config;

/// <summary>
/// CONFIG check. For each country, every application under the environment root (config.pathTemplate,
/// e.g. {country}/App/Config/{env}) is found automatically: any folder, at any depth, holding one of the config files.
///  1. applications and files tally across SIT / UAT / PROD
///  2. key parity: the same keys exist in every environment
///  3. required keys: e.g. the WMQConfiguration block is complete and non-empty
///  4. database / base URL / MQ host values are different in every environment
///  5. application groups: e.g. RIMCoreOrderService and RIMCoreAppInterface have the same appsettings.json
///     but a different nlog.config
/// Values are never printed except server/database names and host names.
/// </summary>
public sealed class ConfigCheck : IReleaseCheck
{
    public CheckMode Mode => CheckMode.Config;
    public string Title => "CONFIG";

    public CheckSection Run(CliOptions options, ReleaseRules rules)
    {
        var cfg = rules.Config;

        // --config-path overrides rules.json, so a folder move only needs a new argument
        string template = (options.ConfigPath ?? cfg.PathTemplate).Replace('\\', '/');

        // older patterns ended with /{service}; applications are now discovered below the environment root
        if (template.EndsWith("/{service}", StringComparison.OrdinalIgnoreCase))
            template = template[..^"/{service}".Length];

        if (!PathTemplate.HasToken(template, "env"))
            return new SkippedSection { Title = Title, Reason = $"Config path '{template}' must contain {{env}}." };

        var countries = PathTemplate.Countries(options.RepoRoot, template, options.Country,
            c => cfg.Environments.Any(env => Directory.Exists(PathTemplate.Resolve(options.RepoRoot, template, c, env))));

        if (countries.Count == 0)
            return new SkippedSection { Title = Title, Reason = $"No config folders found for '{template}' under {options.RepoRoot}." };

        var overrides = new Dictionary<string, string>(cfg.SeverityOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        var results = new List<ConfigCountryResult>();

        foreach (var country in countries)
        {
            var result = CheckCountry(country, cfg, env => PathTemplate.Resolve(options.RepoRoot, template, country, env));
            ApplySeverityOverrides(result, overrides);
            results.Add(result);
        }

        return new ConfigSection { Title = Title, Countries = results, Environments = cfg.Environments, PathTemplate = template };
    }

    private static ConfigCountryResult CheckCountry(string country, ConfigRules cfg, Func<string, string> envRoot)
    {
        var result = new ConfigCountryResult { Country = country.Length == 0 ? "(all)" : country };

        // ---- environments ----
        var envs = new List<string>();
        foreach (var env in cfg.Environments)
        {
            string root = envRoot(env);
            if (Directory.Exists(root)) envs.Add(env);
            else result.Findings.Add(new(Severity.Warning, ConfigRuleIds.EnvMissing, "Config", null, null,
                $"Environment {env} folder not found ({root}) - {env} is left out of the comparison."));
        }
        if (envs.Count == 0) return result;

        // ---- applications: env -> set of app paths ----
        var appsByEnv = envs.ToDictionary(e => e, e => FindApplications(envRoot(e), cfg), StringComparer.OrdinalIgnoreCase);
        result.Applications.AddRange(appsByEnv.Values
            .SelectMany(a => a)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));

        // ---- load every file once: (app, file, env) -> data ----
        var data = new Dictionary<(string App, string File, string Env), ConfigFileData>(new KeyComparer());

        foreach (var app in result.Applications)
        {
            // folders don't tally across environments -> warning; compare the environments that have it
            var appEnvs = envs.Where(e => appsByEnv[e].Contains(app)).ToList();
            var appMissing = envs.Except(appEnvs).ToList();
            if (appMissing.Count > 0)
            {
                result.Findings.Add(new(Severity.Warning, ConfigRuleIds.AppMissing, "Config", app, null,
                    $"Application folder missing in {string.Join(", ", appMissing)} (present in {string.Join(", ", appEnvs)})."));
            }

            foreach (var spec in cfg.Files)
            {
                string PathIn(string env) => Path.Combine(envRoot(env), app.Replace('/', Path.DirectorySeparatorChar), spec.Name);

                var present = appEnvs.Where(e => File.Exists(PathIn(e))).ToList();
                var absent = appEnvs.Except(present).ToList();
                bool required = Glob.AnyMatch(spec.RequiredIn, app);

                if (absent.Count > 0 && (required || present.Count > 0))
                {
                    result.Findings.Add(new(Severity.Error, ConfigRuleIds.FileMissing, spec.Name, app, null,
                        present.Count > 0
                            ? $"[{spec.Name}] missing in {string.Join(", ", absent)} (present in {string.Join(", ", present)})."
                            : $"[{spec.Name}] missing in {string.Join(", ", absent)}."));
                }

                var loaded = new List<(string Env, ConfigFileData File)>();
                foreach (var env in present)
                {
                    var file = ConfigFlattener.Load(PathIn(env), spec.IsJson, cfg.XmlIdentityAttributes);
                    if (file.Error is not null)
                    {
                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.InvalidFile, spec.Name, app, null,
                            $"[{spec.Name}] {env}: can't be parsed - {file.Error}"));
                        continue;
                    }

                    foreach (var dup in file.DuplicateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.DuplicateKey, spec.Name, app, null,
                            $"[{spec.Name}] {env}: '{dup}' is declared more than once (the last one silently wins).", dup));

                    data[(app, spec.Name, env)] = file;
                    loaded.Add((env, file));
                }

                if (loaded.Count == 0) continue;

                var requiredKeys = CheckRequiredKeys(result, cfg, app, spec.Name, loaded);
                if (loaded.Count < 2) continue;

                CheckKeyParity(result, cfg, app, spec.Name, loaded, skip: requiredKeys);
                CheckMustDiffer(result, cfg, app, spec.Name, loaded);
            }
        }

        CheckServiceGroups(result, cfg, envs, data);
        return result;
    }

    /// <summary>Every folder (any depth) below the environment root that directly holds a configured file.</summary>
    private static HashSet<string> FindApplications(string root, ConfigRules cfg)
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string rel = Path.GetRelativePath(root, dir).Replace('\\', '/');

            if (rel != "." && cfg.Files.Any(f => File.Exists(Path.Combine(dir, f.Name))) &&
                !Glob.AnyMatch(cfg.ExcludeApplications, rel))
            {
                apps.Add(rel);
            }

            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children) pending.Push(child);
        }
        return apps;
    }

    // ---------- 3. required keys (e.g. WMQConfiguration) ----------
    /// <summary>Returns the keys it covered, so key parity doesn't report them a second time.</summary>
    private static HashSet<string> CheckRequiredKeys(ConfigCountryResult result, ConfigRules cfg, string app, string file,
                                                     List<(string Env, ConfigFileData File)> loaded)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in cfg.RequiredKeys.Where(r =>
                     r.File.Equals(file, StringComparison.OrdinalIgnoreCase) && Glob.AnyMatch(r.Applications, app)))
        {
            // "used by this application" = the section exists in ANY environment, so a section missing
            // entirely from PROD is still reported
            if (rule.WhenSectionExists is { } section &&
                !loaded.Any(l => l.File.Values.Keys.Any(k => k.Equals(section, StringComparison.OrdinalIgnoreCase) ||
                                                             k.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase))))
                continue;

            covered.UnionWith(rule.Keys);
            string name = string.IsNullOrWhiteSpace(rule.Name) ? "required keys" : rule.Name;

            foreach (var (env, data) in loaded)
            {
                var missing = rule.Keys.Where(k => !data.Values.ContainsKey(k)).ToList();
                var empty = rule.AllowEmpty ? new List<string>()
                    : rule.Keys.Where(k => data.Values.TryGetValue(k, out var v) && string.IsNullOrWhiteSpace(v)).ToList();

                if (missing.Count > 0)
                    result.Findings.Add(new(Severity.Error, ConfigRuleIds.RequiredKeyMissing, file, app, null,
                        $"[{file}] {env}: {name} incomplete - missing {string.Join(", ", missing)}", name));

                if (empty.Count > 0)
                    result.Findings.Add(new(Severity.Error, ConfigRuleIds.RequiredKeyEmpty, file, app, null,
                        $"[{file}] {env}: {name} has empty values - {string.Join(", ", empty)}", name));
            }
        }
        return covered;
    }

    // ---------- 2. same keys in every environment ----------
    private static void CheckKeyParity(ConfigCountryResult result, ConfigRules cfg, string app, string file,
                                       List<(string Env, ConfigFileData File)> loaded, HashSet<string> skip)
    {
        var allKeys = loaded.SelectMany(l => l.File.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !skip.Contains(k) && !Glob.AnyMatch(cfg.IgnoreKeys, k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var key in allKeys)
        {
            var missingIn = loaded.Where(l => !l.File.Values.ContainsKey(key)).Select(l => l.Env).ToList();
            if (missingIn.Count == 0) continue;

            var presentIn = loaded.Where(l => l.File.Values.ContainsKey(key)).Select(l => l.Env);
            result.Findings.Add(new(Severity.Error, ConfigRuleIds.KeyMissing, file, app, null,
                $"[{file}] '{key}' missing in {string.Join(", ", missingIn)} (present in {string.Join(", ", presentIn)}).", key));
        }
    }

    // ---------- 4. database / base URL / MQ host must differ per environment ----------
    private static void CheckMustDiffer(ConfigCountryResult result, ConfigRules cfg, string app, string file,
                                        List<(string Env, ConfigFileData File)> loaded)
    {
        var keys = loaded.SelectMany(l => l.File.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !Glob.AnyMatch(cfg.IgnoreKeys, k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            var rule = cfg.MustDiffer.FirstOrDefault(r => Glob.IsMatch(r.Key, key));
            if (rule is null) continue;

            var tokens = loaded
                .Where(l => l.File.Values.ContainsKey(key))
                .Select(l => (l.Env, Token: CompareToken(l.File.Values[key], rule.CompareAs)))
                .Where(t => t.Token is not null)
                .ToList();

            foreach (var same in tokens.GroupBy(t => t.Token!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                string what = rule.CompareAs.ToLowerInvariant() switch
                {
                    "connectionstring" => "database",
                    "urlhost" or "host" => "host",
                    _ => "value"
                };
                string shown = what == "value" ? "" : $" ({same.Key})"; // never print raw values - may be secrets

                result.Findings.Add(new(Severity.Error, ConfigRuleIds.SameAcrossEnv, file, app, null,
                    $"[{file}] '{key}': {string.Join(", ", same.Select(s => s.Env))} point to the same {what}{shown} - it must be different per environment.", key));
            }
        }
    }

    /// <summary>What "the same" means for a value. Null = not comparable (skipped).</summary>
    private static string? CompareToken(string? value, string compareAs)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        switch (compareAs.ToLowerInvariant())
        {
            case "urlhost":
                return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? $"{uri.Host}:{uri.Port}".ToLowerInvariant()
                    : null;

            case "host":
                return value.Trim().ToLowerInvariant();

            case "connectionstring":
                try
                {
                    var b = new DbConnectionStringBuilder { ConnectionString = value };
                    string? server = First(b, "Data Source", "Server", "Address", "Addr", "Network Address");
                    string? database = First(b, "Initial Catalog", "Database");
                    return server is null && database is null ? null : $"{server}/{database}".ToLowerInvariant();
                }
                catch (ArgumentException)
                {
                    return null; // not a connection string
                }

            default:
                return value.Trim();
        }
    }

    private static string? First(DbConnectionStringBuilder b, params string[] keywords)
    {
        foreach (var k in keywords)
            if (b.TryGetValue(k, out var v) && v?.ToString() is { Length: > 0 } s) return s.Trim();
        return null;
    }

    // ---------- 5. application groups: same appsettings, different nlog ----------
    private static void CheckServiceGroups(ConfigCountryResult result, ConfigRules cfg, List<string> envs,
                                           Dictionary<(string App, string File, string Env), ConfigFileData> data)
    {
        foreach (var group in cfg.ServiceGroups)
        {
            // members found in this country, in configured order (e.g. MY may not have the interface).
            // A member is matched on its folder name (RIMCoreOrderService) or its full application path.
            var members = group.Services
                .Select(name => result.Applications.FirstOrDefault(app =>
                    app.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    app.Split('/')[^1].Equals(name, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a is not null).Select(a => a!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (members.Count < 2) continue;

            string first = members[0];
            string firstName = first.Split('/')[^1];

            foreach (var other in members.Skip(1))
            {
                string otherName = other.Split('/')[^1];

                foreach (var env in envs)
                {
                    foreach (var file in group.SameFiles)
                    {
                        // missing / invalid files are already reported
                        if (!data.TryGetValue((first, file, env), out var a) || !data.TryGetValue((other, file, env), out var b)) continue;

                        var diff = Diff(a, b, cfg.IgnoreKeys);
                        if (diff.Count == 0) continue;

                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.GroupNotSame, file, other, null,
                            $"[{file}] {env}: must be the same as {firstName} but differs - {string.Join("; ", diff)}"));
                    }

                    foreach (var file in group.DifferentFiles)
                    {
                        if (!data.TryGetValue((first, file, env), out var a) || !data.TryGetValue((other, file, env), out var b)) continue;
                        if (Diff(a, b, ignore: null).Count > 0) continue;

                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.GroupNotDifferent, file, other, null,
                            $"[{file}] {env}: identical to {firstName} - it must be specific to {otherName}."));
                    }
                }
            }
        }
    }

    /// <summary>Key/value differences, described by key name only (values may be secrets). Empty = identical.</summary>
    private static List<string> Diff(ConfigFileData a, ConfigFileData b, List<string>? ignore)
    {
        const int Max = 10;
        static string Join(List<string> keys) =>
            string.Join(", ", keys.Take(Max)) + (keys.Count > Max ? $" (+{keys.Count - Max} more)" : "");

        bool Keep(string k) => !Glob.AnyMatch(ignore, k);

        var onlyA = a.Values.Keys.Where(k => Keep(k) && !b.Values.ContainsKey(k)).OrderBy(k => k).ToList();
        var onlyB = b.Values.Keys.Where(k => Keep(k) && !a.Values.ContainsKey(k)).OrderBy(k => k).ToList();
        var changed = a.Values.Keys
            .Where(k => Keep(k) && b.Values.TryGetValue(k, out var bv) && !string.Equals(a.Values[k], bv, StringComparison.Ordinal))
            .OrderBy(k => k).ToList();

        var parts = new List<string>();
        if (onlyA.Count > 0) parts.Add($"missing: {Join(onlyA)}");
        if (onlyB.Count > 0) parts.Add($"extra: {Join(onlyB)}");
        if (changed.Count > 0) parts.Add($"different value: {Join(changed)}");
        return parts;
    }

    private static void ApplySeverityOverrides(ConfigCountryResult result, Dictionary<string, string> overrides)
    {
        if (overrides.Count == 0) return;

        result.Findings.RemoveAll(f => overrides.TryGetValue(f.RuleId, out var v) && v.Equals("Off", StringComparison.OrdinalIgnoreCase));
        foreach (var f in result.Findings)
            if (overrides.TryGetValue(f.RuleId, out var v) && Enum.TryParse<Severity>(v, true, out var sev))
                f.Severity = sev;
    }

    private sealed class KeyComparer : IEqualityComparer<(string App, string File, string Env)>
    {
        public bool Equals((string App, string File, string Env) x, (string App, string File, string Env) y) =>
            string.Equals(x.App, y.App, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Env, y.Env, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string App, string File, string Env) k) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.App),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.File),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Env));
    }
}
