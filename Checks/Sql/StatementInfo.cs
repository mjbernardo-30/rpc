using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReleaseChecker.Core;
using Severity = ReleaseChecker.Core.Severity;

namespace ReleaseChecker.Checks.Sql;

/// <summary>Helpers for writing rules.json: which class name does a statement have, and is it allowed?</summary>
public static class StatementInfo
{
    /// <summary>--list-statements: every concrete ScriptDom statement class, with its status under the current rules.</summary>
    public static void ListAll()
    {
        var names = typeof(TSqlStatement).Assembly.GetTypes()
            .Where(t => typeof(TSqlStatement).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var name in names)
            Console.WriteLine($"{Status(name),-8} {name}");

        Console.WriteLine();
        Console.WriteLine($"{names.Count} statement types. ALLOWED / WARN / DENIED reflects the current rules.json.");
        Console.WriteLine("Tip: ReleaseChecker --list-statements | findstr /i drop");
    }

    /// <summary>--explain file.sql: class name and status of every statement in a script.</summary>
    public static int Explain(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 2;
        }

        string text;
        using (var sr = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
            text = sr.ReadToEnd();

        var fragment = SqlRules.Current.CreateParser().Parse(new StringReader(text), out IList<ParseError> errors);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
                Console.WriteLine($"Syntax error line {e.Line}, col {e.Column}: {e.Message}");
            return 1;
        }

        var collector = new StatementCollector();
        fragment.Accept(collector);

        Console.WriteLine($"{"Line",-6} {"Status",-8} {"Class name",-40} Statement");
        foreach (var node in collector.Statements)
        {
            string name = node.GetType().Name;
            Console.WriteLine($"{node.StartLine,-6} {Status(name),-8} {name,-40} {Preview(node)}");
        }
        return 0;
    }

    private static string Status(string name)
    {
        var rules = SqlRules.Current;
        if (name is "DropTableStatement") return "RULE";      // allowed only for droppable tables
        if (name is "TruncateTableStatement") return rules.AllowTruncateOnStaging ? "RULE" : "DENIED";
        if (rules.IsWarnStatement(name)) return "WARN";
        return rules.IsAllowedStatementType(name) ? "ALLOWED" : "DENIED";
    }

    private static string Preview(TSqlFragment node)
    {
        var tokens = node.ScriptTokenStream;
        if (tokens is null || node.FirstTokenIndex < 0) return "";

        var sb = new StringBuilder();
        for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex && i < tokens.Count && sb.Length < 80; i++)
            sb.Append(tokens[i].Text);

        string s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return s.Length > 70 ? s[..70] + "..." : s;
    }

    private sealed class StatementCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Statements { get; } = new();
        public override void Visit(TSqlStatement node) => Statements.Add(node);
    }
}
