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

    // SQL check: database root pattern (overrides rules.json sql.dbPath)
    public string? DbPath { get; init; }

    // SQL helper commands (no repo needed)
    public bool ListStatements { get; init; }
    public string? ExplainFile { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        var mode = CheckMode.All;
        string? repo = null, country = null, module = null, report = null, implName = null, rules = null, suppressions = null, explain = null, configPath = null, dbPath = null;
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
                case "--db-path": dbPath = Next(); break;
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
            DbPath = dbPath,
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
          --country SG           limit to one country (when the path has {country})
          --module DRIM          limit to one SQL module
          --report <file>        report file (default release-check.txt)
          --rules <file>         rules file (default rules.json next to the exe)
          --fail-on-warning      exit 1 on warnings too

        SQL options:
          --db-path <pattern>    database root, relative to --repo or absolute (default from rules.json:
                                 "{country}/Database"); {country} is optional, e.g. --db-path "Database"
          --impl-list <name>     impl list file name (default impl_list.txt)
          --suppressions <file>  one suppression file for all modules
          --list-statements      list ScriptDom statement class names
          --explain <file.sql>   show the class name of each statement in a script

        Config options:
          --config-path <pattern> environment root, relative to --repo or absolute (default from rules.json:
                                 "{country}/App/Config/{env}"); {env} required, {country} optional.
                                 Every folder below it with appsettings.json / nlog.config / web.config is checked,
                                 e.g. --config-path "{country}/Web/Config/{env}"

        SQL layout:
          <db path>\scripts\<Module>\impl_list.txt
          <db path>\sql\<Module>\...\*.sql

        Config layout:
          <config path>\Online\Web\V2\RIMCoreOrderService\appsettings.json, nlog.config, web.config
          <config path>\Batch\Programs\RIMSInternal\RIMS.Programs.V2\appsettings.json, nlog.config

        Exit code: 0 = pass, 1 = errors (or warnings with --fail-on-warning), 2 = bad arguments / layout / config.
        """;
}
