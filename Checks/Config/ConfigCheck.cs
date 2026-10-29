using System.Data.Common;
using ReleaseChecker.Core;

namespace ReleaseChecker.Checks.Config;

/// <summary>
/// CONFIG check, per country and service (folder layout from config.pathTemplate):
///  1. every environment has the service and every configured file (appsettings.json, nlog.config, web.config)
///  2. key parity: the same keys exist in SIT, UAT and PROD
///  3. database / base URL values are different in every environment (config.mustDiffer)
///  4. service groups: e.g. RIMCoreOrderService and RIMCoreAppInterface have the same appsettings.json
///     but a different nlog.config, per environment (config.serviceGroups)
/// Values are never printed except server/database names and URL hosts.
/// </summary>
public sealed class ConfigCheck : IReleaseCheck
{
    public CheckMode Mode => CheckMode.Config;
    public string Title => "CONFIG";

    public CheckSection Run(CliOptions options, ReleaseRules rules)
    {
        var cfg = rules.Config;
        // --config-path overrides rules.json, so a folder move only needs a new argument
        string pathTemplate = options.ConfigPath ?? cfg.PathTemplate;
        string template = pathTemplate.Replace('\\', '/');
        int serviceAt = template.IndexOf("{service}", StringComparison.OrdinalIgnoreCase);
        if (serviceAt < 0)
            return new SkippedSection { Title = Title, Reason = $"Config path '{pathTemplate}' must contain {{service}}." };

        string servicesTemplate = template[..serviceAt].TrimEnd('/');
        bool hasCountry = servicesTemplate.Contains("{country}", StringComparison.OrdinalIgnoreCase);

        string ServicesRoot(string country, string env) =>
            Path.Combine(options.RepoRoot,
                servicesTemplate.Replace("{country}", country, StringComparison.OrdinalIgnoreCase)
                                .Replace("{env}", env, StringComparison.OrdinalIgnoreCase)
                                .Replace('/', Path.DirectorySeparatorChar));

        var countries = !hasCountry ? new List<string> { "" }
            : options.Country is not null ? new List<string> { options.Country }
            : Directory.GetDirectories(options.RepoRoot)
                .Select(Path.GetFileName)
                .Where(c => c is not null && cfg.Environments.Any(env => Directory.Exists(ServicesRoot(c, env))))
                .Select(c => c!)
                .OrderBy(c => c)
                .ToList();

        if (countries.Count == 0)
        {
            return new SkippedSection
            {
                Title = Title,
                Reason = $"No config folders found for pattern {pathTemplate} under {options.RepoRoot}."
            };
        }

        var overrides = new Dictionary<string, string>(cfg.SeverityOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        var results = new List<ConfigCountryResult>();

        foreach (var country in countries)
        {
            var result = CheckCountry(country, cfg, env => ServicesRoot(country, env));
            ApplySeverityOverrides(result, overrides);
            results.Add(result);
        }

        return new ConfigSection { Title = Title, Countries = results, Environments = cfg.Environments, PathTemplate = pathTemplate };
    }

    private static ConfigCountryResult CheckCountry(string country, ConfigRules cfg, Func<string, string> servicesRoot)
    {
        var result = new ConfigCountryResult { Country = country.Length == 0 ? "(all)" : country };

        // ---- environments ----
        var envs = new List<string>();
        foreach (var env in cfg.Environments)
        {
            string root = servicesRoot(env);
            if (Directory.Exists(root)) envs.Add(env);
            else result.Findings.Add(new(Severity.Warning, ConfigRuleIds.EnvMissing, "Config", null, null,
                $"Environment {env} folder not found ({root}) - {env} is left out of the comparison."));
        }
        if (envs.Count == 0) return result;

        // ---- services = every service folder found in any environment ----
        result.Services.AddRange(envs
            .SelectMany(env => Directory.GetDirectories(servicesRoot(env)).Select(Path.GetFileName))
            .Where(s => s is not null).Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        // ---- load every file once: (service, file, env) -> data ----
        var data = new Dictionary<(string Service, string File, string Env), ConfigFileData>(new KeyComparer());

        foreach (var service in result.Services)
        {
            // folders don't tally across environments -> warning, and compare the environments that do have it
            var missingIn = envs.Where(e => !Directory.Exists(Path.Combine(servicesRoot(e), service))).ToList();
            if (missingIn.Count > 0)
            {
                var presentIn = envs.Except(missingIn);
                result.Findings.Add(new(Severity.Warning, ConfigRuleIds.ServiceMissing, "Config", service, null,
                    $"Service folder missing in {string.Join(", ", missingIn)} (present in {string.Join(", ", presentIn)})."));
            }

            foreach (var env in envs.Except(missingIn))
            {
                string dir = Path.Combine(servicesRoot(env), service);

                foreach (var spec in cfg.Files)
                {
                    string path = Path.Combine(dir, spec.Name);
                    if (!File.Exists(path))
                    {
                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.FileMissing, spec.Name, service, null,
                            $"[{spec.Name}] missing in {env}."));
                        continue;
                    }

                    var file = ConfigFlattener.Load(path, spec.IsJson, cfg.XmlIdentityAttributes);
                    if (file.Error is not null)
                    {
                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.InvalidFile, spec.Name, service, null,
                            $"[{spec.Name}] {env}: can't be parsed - {file.Error}"));
                        continue;
                    }

                    foreach (var dup in file.DuplicateKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.DuplicateKey, spec.Name, service, null,
                            $"[{spec.Name}] {env}: '{dup}' is declared more than once (the last one silently wins).", dup));

                    data[(service, spec.Name, env)] = file;
                }
            }

            foreach (var spec in cfg.Files)
            {
                var loaded = envs
                    .Where(e => data.ContainsKey((service, spec.Name, e)))
                    .Select(e => (Env: e, File: data[(service, spec.Name, e)]))
                    .ToList();

                if (loaded.Count < 2) continue;

                CheckKeyParity(result, cfg, service, spec.Name, loaded);
                CheckMustDiffer(result, cfg, service, spec.Name, loaded);
            }
        }

        CheckServiceGroups(result, cfg, envs, data);
        return result;
    }

    // ---------- 2. same keys in every environment ----------
    private static void CheckKeyParity(ConfigCountryResult result, ConfigRules cfg, string service, string file,
                                       List<(string Env, ConfigFileData File)> loaded)
    {
        var allKeys = loaded.SelectMany(l => l.File.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !Glob.AnyMatch(cfg.IgnoreKeys, k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var key in allKeys)
        {
            var missingIn = loaded.Where(l => !l.File.Values.ContainsKey(key)).Select(l => l.Env).ToList();
            if (missingIn.Count == 0) continue;

            var presentIn = loaded.Where(l => l.File.Values.ContainsKey(key)).Select(l => l.Env);
            result.Findings.Add(new(Severity.Error, ConfigRuleIds.KeyMissing, file, service, null,
                $"[{file}] '{key}' missing in {string.Join(", ", missingIn)} (present in {string.Join(", ", presentIn)}).", key));
        }
    }

    // ---------- 3. database / base URL must differ per environment ----------
    private static void CheckMustDiffer(ConfigCountryResult result, ConfigRules cfg, string service, string file,
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
                string what = rule.CompareAs.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase) ? "database"
                            : rule.CompareAs.Equals("UrlHost", StringComparison.OrdinalIgnoreCase) ? "host"
                            : "value";
                string shown = what == "value" ? "" : $" ({same.Key})"; // never print raw values - may be secrets

                result.Findings.Add(new(Severity.Error, ConfigRuleIds.SameAcrossEnv, file, service, null,
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

    // ---------- 4. service groups: same appsettings, different nlog ----------
    private static void CheckServiceGroups(ConfigCountryResult result, ConfigRules cfg, List<string> envs,
                                           Dictionary<(string Service, string File, string Env), ConfigFileData> data)
    {
        foreach (var group in cfg.ServiceGroups)
        {
            // members that exist in this country, in configured order (e.g. MY may not have the interface)
            var members = group.Services
                .Select(s => result.Services.FirstOrDefault(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)))
                .Where(s => s is not null).Select(s => s!)
                .ToList();
            if (members.Count < 2) continue;

            string first = members[0];
            foreach (var other in members.Skip(1))
            {
                foreach (var env in envs)
                {
                    foreach (var file in group.SameFiles)
                    {
                        // missing / invalid files are already reported
                        if (!data.TryGetValue((first, file, env), out var a) || !data.TryGetValue((other, file, env), out var b)) continue;

                        var diff = Diff(a, b, cfg.IgnoreKeys);
                        if (diff.Count == 0) continue;

                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.GroupNotSame, file, other, null,
                            $"[{file}] {env}: must be the same as {first} but differs - {string.Join("; ", diff)}"));
                    }

                    foreach (var file in group.DifferentFiles)
                    {
                        if (!data.TryGetValue((first, file, env), out var a) || !data.TryGetValue((other, file, env), out var b)) continue;
                        if (Diff(a, b, ignore: null).Count > 0) continue;

                        result.Findings.Add(new(Severity.Error, ConfigRuleIds.GroupNotDifferent, file, other, null,
                            $"[{file}] {env}: identical to {first} - it must be specific to {other}."));
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

    private sealed class KeyComparer : IEqualityComparer<(string Service, string File, string Env)>
    {
        public bool Equals((string Service, string File, string Env) x, (string Service, string File, string Env) y) =>
            string.Equals(x.Service, y.Service, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Env, y.Env, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Service, string File, string Env) k) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Service),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.File),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Env));
    }
}
