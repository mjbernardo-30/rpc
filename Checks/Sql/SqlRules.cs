using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReleaseChecker.Core;
using Severity = ReleaseChecker.Core.Severity;

namespace ReleaseChecker.Checks.Sql;

/// <summary>Rule IDs used in findings, rules.json severityOverrides and suppressions.</summary>
public static class RuleIds
{
    // impl_list
    public const string ImplMissingFile = "IMPL_MISSING_FILE";
    public const string ImplDuplicate = "IMPL_DUPLICATE";
    public const string ImplNotSql = "IMPL_NOT_SQL";
    public const string ImplEmpty = "IMPL_EMPTY";
    public const string ImplSqlFolderMissing = "IMPL_SQL_FOLDER_MISSING";

    // content
    public const string SyntaxError = "SYNTAX_ERROR";              // never overridable / suppressible
    public const string ScriptEmpty = "SCRIPT_EMPTY";
    public const string StatementNotAllowed = "STATEMENT_NOT_ALLOWED";
    public const string StatementUnverifiable = "STATEMENT_UNVERIFIABLE";
    public const string DropTableNotAllowed = "DROP_TABLE_NOT_ALLOWED";
    public const string DropTableNoIfExists = "DROP_TABLE_NO_IF_EXISTS";
    public const string TruncateNotAllowed = "TRUNCATE_NOT_ALLOWED";
    public const string UpdateNoWhere = "UPDATE_NO_WHERE";
    public const string DeleteNoWhere = "DELETE_NO_WHERE";
    public const string CreateTableNotGuarded = "CREATE_TABLE_NOT_GUARDED";
    public const string CreateIndexNotGuarded = "CREATE_INDEX_NOT_GUARDED";
    public const string AlterTableAddNotGuarded = "ALTER_TABLE_ADD_NOT_GUARDED";
    public const string AlterTableDropNotGuarded = "ALTER_TABLE_DROP_NOT_GUARDED";
    public const string TranNoCommit = "TRAN_NO_COMMIT";
    public const string TranCountMismatch = "TRAN_COUNT_MISMATCH";

    // ordering
    public const string UsedBeforeCreate = "ORDER_USED_BEFORE_CREATE";
    public const string UsedBeforeCreateInModule = "ORDER_USED_BEFORE_CREATE_IN_MODULE";
    public const string CreatedTwice = "ORDER_CREATED_TWICE";

    // grants
    public const string GrantMissing = "GRANT_MISSING";
    public const string GrantScriptNotListed = "GRANT_SCRIPT_NOT_LISTED";

    // suppressions
    public const string SuppressionUnused = "SUPPRESSION_UNUSED";
    public const string SuppressionExpired = "SUPPRESSION_EXPIRED";
    public const string SuppressionNoReason = "SUPPRESSION_NO_REASON";
    public const string SuppressionNotPermitted = "SUPPRESSION_NOT_PERMITTED";
    public const string SuppressionFileInvalid = "SUPPRESSION_FILE_INVALID";
}

/// <summary>The "sql" section of rules.json. Every property has a default, so the file can override only what it needs.</summary>
public sealed class RulesConfig
{
    /// <summary>160 = SQL 2022, 150 = 2019, 140 = 2017, 130 = 2016</summary>
    public int ParserVersion { get; set; } = 160;

    /// <summary>ScriptDom statement class names allowed in release scripts.</summary>
    public List<string> AllowedStatements { get; set; } = new()
    {
        "SelectStatement", "InsertStatement", "UpdateStatement", "DeleteStatement", "MergeStatement",
        "GrantStatement",
        "IfStatement", "WhileStatement", "BeginEndBlockStatement", "TryCatchStatement",
        "DeclareVariableStatement", "DeclareTableVariableStatement", "SetVariableStatement",
        "PredicateSetStatement", "SetIdentityInsertStatement", "SetTransactionIsolationLevelStatement",
        "PrintStatement", "ThrowStatement", "RaiseErrorStatement", "ReturnStatement",
        "BreakStatement", "ContinueStatement",
        "BeginTransactionStatement", "CommitTransactionStatement",
        "RollbackTransactionStatement", "SaveTransactionStatement",
        "DeclareCursorStatement", "OpenCursorStatement", "FetchCursorStatement",
        "CloseCursorStatement", "DeallocateCursorStatement",
    };

    /// <summary>When true, every Create*/Alter* statement is allowed except the denied prefixes.</summary>
    public bool AllowAllCreateAlter { get; set; } = true;

    public List<string> DeniedCreateAlterPrefixes { get; set; } = new()
    {
        "CreateDatabase", "AlterDatabase", "CreateLogin", "AlterLogin",
        "CreateServer", "AlterServer", "CreateEndpoint", "AlterEndpoint",
    };

    /// <summary>Allowed, but reported as a warning (content can't be verified).</summary>
    public List<string> WarnStatements { get; set; } = new() { "ExecuteStatement" };

    /// <summary>DROP TABLE allowed only for tables ending with one of these (case-insensitive).</summary>
    public List<string> DroppableTableSuffixes { get; set; } = new() { "Stg" };

    public bool AllowDropTempTables { get; set; } = true;

    /// <summary>Allow TRUNCATE TABLE on droppable (staging / temp) tables.</summary>
    public bool AllowTruncateOnStaging { get; set; } = false;

    /// <summary>RuleId -> "Error" | "Warning" | "Info" | "Off". SYNTAX_ERROR can't be changed.</summary>
    public Dictionary<string, string> SeverityOverrides { get; set; } = new();

    /// <summary>If false, suppressions can only accept warnings - errors (disallowed operations) always block.</summary>
    public bool AllowSuppressingErrors { get; set; } = false;

    /// <summary>New tables / procedures must be granted in the grant script.</summary>
    public GrantCheckConfig GrantCheck { get; set; } = new();

    /// <summary>Per-module suppression file name, looked up next to impl_list.txt.</summary>
    public string SuppressionFileName { get; set; } = "sqlcheck.suppressions.json";

    // ---------- helpers ----------
    public bool IsAllowedStatementType(string typeName) =>
        AllowedStatements.Contains(typeName, StringComparer.Ordinal) ||
        (AllowAllCreateAlter &&
         (typeName.StartsWith("Create", StringComparison.Ordinal) || typeName.StartsWith("Alter", StringComparison.Ordinal)) &&
         !DeniedCreateAlterPrefixes.Any(p => typeName.StartsWith(p, StringComparison.Ordinal)));

    public bool IsWarnStatement(string typeName) => WarnStatements.Contains(typeName, StringComparer.Ordinal);

    public bool IsDroppable(string? tableName) =>
        tableName is not null &&
        ((AllowDropTempTables && tableName.StartsWith('#')) ||
         DroppableTableSuffixes.Any(s => tableName.EndsWith(s, StringComparison.OrdinalIgnoreCase)));

    public TSqlParser CreateParser() => ParserVersion switch
    {
        160 => new TSql160Parser(initialQuotedIdentifiers: true),
        150 => new TSql150Parser(initialQuotedIdentifiers: true),
        140 => new TSql140Parser(initialQuotedIdentifiers: true),
        130 => new TSql130Parser(initialQuotedIdentifiers: true),
        _ => throw new InvalidOperationException($"Unsupported parserVersion {ParserVersion}. Use 160, 150, 140 or 130.")
    };
}

public static class SqlRules
{
    public static RulesConfig Current { get; private set; } = new();

    public static JsonSerializerOptions JsonOptions => ReleaseRules.JsonOptions;

    /// <summary>Activates the "sql" section of rules.json for this run and reports typos in it.</summary>
    public static void Use(RulesConfig cfg)
    {
        Current = cfg;
        WarnUnknownNames(cfg);
    }

    /// <summary>Applies severityOverrides: changes severity, or removes findings set to "Off".</summary>
    public static void ApplySeverityOverrides(ModuleResult module)
    {
        var overrides = Current.SeverityOverrides;
        if (overrides.Count == 0) return;

        module.Findings.RemoveAll(f =>
            f.RuleId != RuleIds.SyntaxError &&
            overrides.TryGetValue(f.RuleId, out var v) &&
            v.Equals("Off", StringComparison.OrdinalIgnoreCase));

        foreach (var f in module.Findings)
        {
            if (f.RuleId == RuleIds.SyntaxError) continue;
            if (overrides.TryGetValue(f.RuleId, out var v) && Enum.TryParse<Severity>(v, ignoreCase: true, out var sev))
                f.Severity = sev;
        }
    }

    /// <summary>Catches typos like "UpdateStatment" - an unknown name would silently never match.</summary>
    private static void WarnUnknownNames(RulesConfig cfg)
    {
        var known = typeof(TSqlStatement).Assembly.GetTypes()
            .Where(t => typeof(TSqlStatement).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in cfg.AllowedStatements.Concat(cfg.WarnStatements).Where(n => !known.Contains(n)))
            Console.WriteLine($"  rules.json: '{name}' is not a known ScriptDom statement type (typo?) - it will never match.");

        foreach (var (key, value) in cfg.SeverityOverrides)
        {
            if (!value.Equals("Off", StringComparison.OrdinalIgnoreCase) && !Enum.TryParse<Severity>(value, true, out _))
                Console.WriteLine($"  rules.json: severityOverrides[{key}] = '{value}' is invalid (use Error, Warning, Info or Off).");
            if (key.Equals(RuleIds.SyntaxError, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("  rules.json: SYNTAX_ERROR can't be overridden - ignored.");
        }
    }
}
