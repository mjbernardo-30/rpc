namespace ReleaseChecker.Core;

/// <summary>Which checks to run. First positional argument: All | SQL | Config.</summary>
public enum CheckMode { All, Sql, Config }

public sealed class CliOptions
{
    public CheckMode Mode { get; init; } = CheckMode.All;
    public string ModeName => Mode == CheckMode.Sql ? "SQL" : Mode.ToString();
    public string RepoRoot { get; init; } = "";
    public string? Country { get; init; }
    public string? Module { get; init; }
    public string ReportPath { get; init; } = "release-check.txt";
    public bool FailOnWarning { get; init; }
    public string? RulesPath { get; init; }

    // SQL check options
    public string ImplListFileName { get; init; } = "impl_list.txt";
    public string? SuppressionsPath { get; init; }

    // Config check options
    public string? ConfigPath { get; init; }

    // SQL helper commands (no repo needed)
    public bool ListStatements { get; init; }
    public string? ExplainFile { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        var mode = CheckMode.All;
        string? repo = null, country = null, module = null, report = null, implName = null, rules = null, suppressions = null, explain = null, configPath = null;
        bool failOnWarning = false, listStatements = false;

        int start = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            if (!TryParseMode(args[0], out mode))
            {
                Console.Error.WriteLine($"Unknown check type '{args[0]}'. Use All, SQL or Config.");
                return null;
            }
            start = 1;
        }

        for (int i = start; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a.ToLowerInvariant())
            {
                case "--repo": repo = Next(); break;
                case "--country": country = Next(); break;
                case "--module": module = Next(); break;
                case "--report": report = Next(); break;
                case "--rules": rules = Next(); break;
                case "--fail-on-warning": failOnWarning = true; break;
                case "--impl-list": implName = Next(); break;
                case "--suppressions": suppressions = Next(); break;
                case "--config-path": configPath = Next(); break;
                case "--list-statements": listStatements = true; break;
                case "--explain": explain = Next(); break;
                case "-h":
                case "--help":
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {a}");
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(repo) && !listStatements && explain is null) return null;

        return new CliOptions
        {
            Mode = mode,
            RepoRoot = repo is null ? "" : Path.GetFullPath(repo),
            Country = country,
            Module = module,
            ReportPath = report ?? "release-check.txt",
            FailOnWarning = failOnWarning,
            RulesPath = rules,
            ImplListFileName = implName ?? "impl_list.txt",
            SuppressionsPath = suppressions,
            ConfigPath = configPath,
            ListStatements = listStatements,
            ExplainFile = explain
        };
    }

    private static bool TryParseMode(string value, out CheckMode mode) =>
        Enum.TryParse(value, ignoreCase: true, out mode) && Enum.IsDefined(mode);

    public const string Usage = """
        ReleaseChecker - pre-release checks.

        Usage:
          ReleaseChecker [All|SQL|Config] --repo <path-to-oms> [options]

        Check type (first argument, default All):
          All      run every check
          SQL      SQL scripts listed in impl_list.txt
          Config   appsettings.json / nlog.config / web.config across SIT, UAT, PROD

        Options:
          --repo <path>          repo root (required)
          --country SG           limit to one country (SQL and Config)
          --module DRIM          limit to one SQL module
          --report <file>        report file (default release-check.txt)
          --rules <file>         rules file (default rules.json next to the exe)
          --fail-on-warning      exit 1 on warnings too

        SQL options:
          --impl-list <name>     impl list file name (default impl_list.txt)
          --suppressions <file>  one suppression file for all modules
          --list-statements      list ScriptDom statement class names
          --explain <file.sql>   show the class name of each statement in a script

        Config options:
          --config-path <tpl>    config folder pattern relative to --repo (overrides rules.json pathTemplate)
                                 tokens {country}, {env}, {service}; must end with {service}
                                 e.g. --config-path "{country}/App/Config/{env}/Web/{service}"

        SQL layout:
          <repo>\<Country>\Database\scripts\<Module>\impl_list.txt
          <repo>\<Country>\Database\sql\<Module>\...\*.sql

        Config layout (config.pathTemplate in rules.json):
          <repo>\<Country>\App\Config\<Env>\Web\<Service>\appsettings.json, nlog.config, web.config

        Exit code: 0 = pass, 1 = errors (or warnings with --fail-on-warning), 2 = bad arguments / layout / config.
        """;
}
