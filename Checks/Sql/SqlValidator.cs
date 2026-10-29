using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReleaseChecker.Core;
using Severity = ReleaseChecker.Core.Severity;

namespace ReleaseChecker.Checks.Sql;

/// <summary>
/// Validates script CONTENT, fully offline:
///  1. Syntax   - parses every script with Microsoft's T-SQL parser (line/column errors).
///  2. Rules    - statement allowlist + risky patterns, configured in rules.json.
///  3. Ordering - an object used by a script that runs BEFORE the script creating it.
/// </summary>
public static class SqlValidator
{
    public static void Validate(ModuleResult module, List<(string Listed, string FullPath)> scripts)
    {
        var rules = SqlRules.Current;
        var analyses = new List<(string File, ScriptAnalyzer Analyzer)>();

        // Pass 1: parse + rules per file
        foreach (var (listed, fullPath) in scripts)
        {
            string text;
            using (var sr = new StreamReader(fullPath, detectEncodingFromByteOrderMarks: true))
                text = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(text))
            {
                module.Findings.Add(new(Severity.Warning, RuleIds.ScriptEmpty, "Syntax", listed, null, "Script is empty."));
                continue;
            }

            var parser = rules.CreateParser();
            TSqlFragment fragment;
            IList<ParseError> errors;
            using (var reader = new StringReader(text))
                fragment = parser.Parse(reader, out errors);

            if (errors.Count > 0)
            {
                foreach (var e in errors)
                    module.Findings.Add(new(Severity.Error, RuleIds.SyntaxError, "Syntax", listed, e.Line,
                        $"Col {e.Column}: {e.Message}"));
                continue; // tree may be incomplete, skip rules for this file
            }

            var analyzer = new ScriptAnalyzer(rules);
            fragment.Accept(analyzer);
            analyzer.Finish();

            foreach (var i in analyzer.Issues)
                module.Findings.Add(new(i.Severity, i.RuleId, "Rule", listed, i.Line > 0 ? i.Line : null, i.Message, i.Snippet)
                {
                    ClassName = i.ClassName
                });

            analyses.Add((listed, analyzer));
        }

        // Grants: new tables / procedures must be granted in the grant script
        GrantChecker.Check(module, analyses);

        // Pass 2: cross-script ordering, following impl_list order
        var createdAt = new Dictionary<string, (int Index, string File)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < analyses.Count; i++)
        {
            foreach (var obj in analyses[i].Analyzer.Created)
            {
                if (createdAt.TryGetValue(obj, out var first))
                    module.Findings.Add(new(Severity.Warning, RuleIds.CreatedTwice, "Ordering", analyses[i].File, null,
                        $"'{obj}' is also created in '{first.File}'. Make sure both are guarded or remove one.", obj));
                else
                    createdAt[obj] = (i, analyses[i].File);
            }
        }

        for (int i = 0; i < analyses.Count; i++)
        {
            var (file, analyzer) = analyses[i];
            foreach (var r in analyzer.References)
            {
                if (!createdAt.TryGetValue(r.Name, out var creator) || creator.Index <= i) continue;

                module.Findings.Add(r.InsideModule
                    ? new(Severity.Warning, RuleIds.UsedBeforeCreateInModule, "Ordering", file, r.Line,
                        $"'{r.Name}' is referenced inside a procedure/view/function but only created later in '{creator.File}'. " +
                        "Creation will succeed, but it fails at runtime if called before that script runs.", r.Name)
                    : new(Severity.Warning, RuleIds.UsedBeforeCreate, "Ordering", file, r.Line,
                        $"'{r.Name}' is used here but only created later in '{creator.File}'. Move '{creator.File}' above this script in impl_list.", r.Name));
            }
        }
    }
}

internal sealed record CreatedObject(string Name, string Type, int Line);

/// <summary>One permission granted to one principal. Target is schema.object, or the schema name when OnSchema.</summary>
internal sealed record GrantInfo(string Target, bool OnSchema, string Permission, string Principal, int Line);

internal sealed record Issue(Severity Severity, string RuleId, int Line, string Message, string? Snippet, string? ClassName = null);

/// <summary>Walks the parsed T-SQL tree of one script.</summary>
internal sealed class ScriptAnalyzer : TSqlFragmentVisitor
{
    private readonly RulesConfig _rules;
    public ScriptAnalyzer(RulesConfig rules) => _rules = rules;

    public List<Issue> Issues { get; } = new();
    public HashSet<string> Created { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, int Line, bool InsideModule)> References { get; } = new();

    /// <summary>Objects this script creates, by type (Table, Procedure, View, Function) - used by the grant check.</summary>
    public List<CreatedObject> CreatedObjects { get; } = new();

    /// <summary>GRANT statements in this script - used by the grant check.</summary>
    public List<GrantInfo> Grants { get; } = new();

    private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
    private int _ifDepth;      // inside IF ... (treat DDL as guarded / re-runnable)
    private int _moduleDepth;  // inside CREATE/ALTER PROCEDURE, VIEW, FUNCTION, TRIGGER body
    private int _beginTran, _commit;

    // ---------- context tracking ----------
    public override void ExplicitVisit(IfStatement node) { _ifDepth++; base.ExplicitVisit(node); _ifDepth--; }

    public override void ExplicitVisit(CreateProcedureStatement node) { Record(node.ProcedureReference?.Name, "Procedure", node); _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(CreateOrAlterProcedureStatement node) { Record(node.ProcedureReference?.Name, "Procedure", node); _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(AlterProcedureStatement node) { _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(CreateViewStatement node) { Record(node.SchemaObjectName, "View", node); _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(CreateOrAlterViewStatement node) { Record(node.SchemaObjectName, "View", node); _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(CreateFunctionStatement node) { Record(node.Name, "Function", node); _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(CreateOrAlterFunctionStatement node) { Record(node.Name, "Function", node); _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }
    public override void ExplicitVisit(CreateTriggerStatement node) { _moduleDepth++; base.ExplicitVisit(node); _moduleDepth--; }

    public override void Visit(CommonTableExpression node)
    {
        if (node.ExpressionName?.Value is { } name) _cteNames.Add(name);
    }

    // ---------- statement allowlist (rules.json) ----------
    // Fires for EVERY statement, including ones nested in IF / BEGIN..END / procedure bodies.
    // Most recent statement visited (pre-order), so clause-level findings such as
    // UPDATE ... without WHERE (an UpdateSpecification) report their statement's class (UpdateStatement).
    private TSqlStatement? _currentStatement;

    public override void Visit(TSqlStatement node)
    {
        _currentStatement = node;

        string type = node.GetType().Name;

        if (node is DropTableStatement or TruncateTableStatement) return; // own rules below

        if (_rules.IsWarnStatement(type))
        {
            Add(Severity.Warning, RuleIds.StatementUnverifiable, node,
                $"{Describe(type)} ({type}) - allowed, but its content can't be verified. Review manually.");
            return;
        }

        if (!_rules.IsAllowedStatementType(type))
            Add(Severity.Error, RuleIds.StatementNotAllowed, node, $"{Describe(type)} is not allowed in release scripts. (To allow it, add \"{type}\" to allowedStatements in rules.json)");
    }

    public override void Visit(DropTableStatement node)
    {
        foreach (var obj in node.Objects)
        {
            string? table = obj.BaseIdentifier?.Value;
            if (!_rules.IsDroppable(table))
            {
                Add(Severity.Error, RuleIds.DropTableNotAllowed, node,
                    $"DROP TABLE {Key(obj)} is not allowed. Only tables ending with {string.Join(" / ", _rules.DroppableTableSuffixes)}" +
                    $"{(_rules.AllowDropTempTables ? " or temp (#) tables" : "")} may be dropped.");
            }
            else if (!node.IsIfExists && _ifDepth == 0 && !table!.StartsWith('#'))
            {
                Add(Severity.Warning, RuleIds.DropTableNoIfExists, node, $"DROP TABLE {Key(obj)} without IF EXISTS - fails if the table is missing.");
            }
        }
    }

    public override void Visit(TruncateTableStatement node)
    {
        Ref(node.TableName, node);
        string? table = node.TableName?.BaseIdentifier?.Value;

        if (!(_rules.AllowTruncateOnStaging && _rules.IsDroppable(table)))
            Add(Severity.Error, RuleIds.TruncateNotAllowed, node, $"TRUNCATE TABLE {Key(node.TableName)} is not allowed in release scripts.");
    }

    // ---------- DDL re-runnability ----------
    public override void Visit(CreateTableStatement node)
    {
        string? name = Key(node.SchemaObjectName);
        if (name is null || name.Contains('#')) return; // temp table

        Created.Add(name);
        CreatedObjects.Add(new(name, "Table", node.StartLine));
        if (_ifDepth == 0)
            Add(Severity.Warning, RuleIds.CreateTableNotGuarded, node, $"CREATE TABLE {name} has no IF NOT EXISTS guard - script is not re-runnable.");
    }

    public override void Visit(AlterTableAddTableElementStatement node)
    {
        Ref(node.SchemaObjectName, node);
        if (_ifDepth == 0)
            Add(Severity.Warning, RuleIds.AlterTableAddNotGuarded, node,
                $"ALTER TABLE {Key(node.SchemaObjectName)} ADD without IF NOT EXISTS (COL_LENGTH / sys.columns) - not re-runnable.");
    }

    public override void Visit(AlterTableDropTableElementStatement node)
    {
        Ref(node.SchemaObjectName, node);
        if (_ifDepth == 0)
            Add(Severity.Warning, RuleIds.AlterTableDropNotGuarded, node, $"ALTER TABLE {Key(node.SchemaObjectName)} DROP without guard - not re-runnable.");
    }

    public override void Visit(CreateIndexStatement node)
    {
        Ref(node.OnName, node);
        if (_ifDepth == 0)
            Add(Severity.Warning, RuleIds.CreateIndexNotGuarded, node, $"CREATE INDEX {node.Name?.Value} has no IF NOT EXISTS guard - not re-runnable.");
    }

    // ---------- DML ----------
    public override void Visit(DeleteSpecification node)
    {
        if (node.WhereClause is null)
            Add(Severity.Warning, RuleIds.DeleteNoWhere, node, "DELETE without WHERE - deletes every row. Confirm this is intended.");
    }

    public override void Visit(UpdateSpecification node)
    {
        if (node.WhereClause is null)
            Add(Severity.Warning, RuleIds.UpdateNoWhere, node, "UPDATE without WHERE - updates every row. Confirm this is intended.");
    }

    public override void Visit(NamedTableReference node) => Ref(node.SchemaObject, node);

    // ---------- security ----------
    public override void Visit(GrantStatement node)
    {
        var ids = node.SecurityTargetObject?.ObjectName?.MultiPartIdentifier?.Identifiers;
        if (ids is null || ids.Count == 0) return;

        // GRANT ... ON SCHEMA::dbo TO x  -> covers every object in the schema
        bool onSchema = node.SecurityTargetObject!.ObjectKind == SecurityObjectKind.Schema;

        string target;
        if (onSchema)
        {
            target = ids[^1].Value;
        }
        else
        {
            string baseName = ids[^1].Value;
            string schema = ids.Count >= 2 ? ids[^2].Value : "dbo";
            target = $"{schema}.{baseName}";
            References.Add((target, node.StartLine, _moduleDepth > 0));
        }

        var permissions = node.Permissions
            .Select(p => string.Join(" ", p.Identifiers.Select(i => i.Value)).ToUpperInvariant())
            .ToList();

        foreach (var principal in node.Principals)
        {
            string? name = principal.Identifier?.Value
                           ?? (principal.PrincipalType == PrincipalType.Public ? "public" : null);
            if (name is null) continue;

            foreach (var permission in permissions)
                Grants.Add(new(target, onSchema, permission, name, node.StartLine));
        }
    }

    // ---------- transactions ----------
    public override void Visit(BeginTransactionStatement node) { if (_moduleDepth == 0) _beginTran++; }
    public override void Visit(CommitTransactionStatement node) { if (_moduleDepth == 0) _commit++; }

    public void Finish()
    {
        if (_beginTran > 0 && _commit == 0)
            Issues.Add(new(Severity.Warning, RuleIds.TranNoCommit, 0, $"BEGIN TRAN found ({_beginTran}x) but no COMMIT - transaction would be left open.", null));
        else if (_beginTran > _commit)
            Issues.Add(new(Severity.Warning, RuleIds.TranCountMismatch, 0, $"BEGIN TRAN ({_beginTran}) and COMMIT ({_commit}) counts differ - check every path commits or rolls back.", null));

        // CTE names are not real tables
        References.RemoveAll(r => r.Name.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase) && _cteNames.Contains(r.Name[4..]));
    }

    // ---------- helpers ----------
    private void Add(Severity s, string ruleId, TSqlFragment node, string msg)
    {
        string? className = node is TSqlStatement ? node.GetType().Name : _currentStatement?.GetType().Name;
        Issues.Add(new(s, ruleId, node.StartLine, msg, Snippet(node), className));
    }

    /// <summary>The statement's own SQL text (whitespace collapsed, max ~300 chars) - used by suppression "contains".</summary>
    private static string? Snippet(TSqlFragment node)
    {
        var tokens = node.ScriptTokenStream;
        if (tokens is null || node.FirstTokenIndex < 0 || node.LastTokenIndex < node.FirstTokenIndex) return null;

        var sb = new StringBuilder();
        for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex && i < tokens.Count && sb.Length < 300; i++)
            sb.Append(tokens[i].Text);

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>"DropDatabaseStatement" -> "DROP DATABASE"</summary>
    private static string Describe(string typeName)
    {
        string name = typeName.EndsWith("Statement") ? typeName[..^"Statement".Length] : typeName;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }

    private void Record(SchemaObjectName? name, string objectType, TSqlFragment node)
    {
        if (Key(name) is not { } k) return;
        Created.Add(k);
        CreatedObjects.Add(new(k, objectType, node.StartLine));
    }

    private void Ref(SchemaObjectName? name, TSqlFragment node)
    {
        string? k = Key(name);
        if (k is null || k.Contains('#')) return; // temp tables
        References.Add((k, node.StartLine, _moduleDepth > 0));
    }

    /// <summary>Normalises to schema.object (default schema dbo), ignoring server/database parts.</summary>
    private static string? Key(SchemaObjectName? name)
    {
        string? baseName = name?.BaseIdentifier?.Value;
        if (string.IsNullOrWhiteSpace(baseName)) return null;
        string schema = name!.SchemaIdentifier?.Value ?? "dbo";
        return $"{schema}.{baseName}";
    }
}
