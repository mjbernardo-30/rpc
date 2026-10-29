using System.Text.Json;
using ReleaseChecker.Checks.Config;
using ReleaseChecker.Checks.Sql;

namespace ReleaseChecker.Core;

/// <summary>
/// Root of rules.json. Each check owns one section:
/// { "sql": { ... }, "config": { ... } }
/// Any section or property left out uses the built-in default.
/// </summary>
public sealed class ReleaseRules
{
    public RulesConfig Sql { get; set; } = new();
    public ConfigRules Config { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads rules.json (default: next to the exe). Returns null if the file is invalid.</summary>
    public static ReleaseRules? Load(string? explicitPath)
    {
        string path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "rules.json");

        if (!File.Exists(path))
        {
            if (explicitPath is not null)
            {
                Console.Error.WriteLine($"Rules file not found: {path}");
                return null;
            }
            Console.WriteLine("rules.json not found next to the exe - using built-in defaults.");
            return new ReleaseRules();
        }

        try
        {
            string json = File.ReadAllText(path);

            // Old SqlReleaseCheck format had the SQL settings at the root - they'd be silently ignored now.
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                bool hasSection = doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("sql", StringComparison.OrdinalIgnoreCase));
                bool looksOld = doc.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals("allowedStatements", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Equals("severityOverrides", StringComparison.OrdinalIgnoreCase));
                if (!hasSection && looksOld)
                {
                    Console.Error.WriteLine($"Rules file {path} uses the old flat format. Move the SQL settings inside a \"sql\": {{ ... }} section.");
                    return null;
                }
            }

            var rules = JsonSerializer.Deserialize<ReleaseRules>(json, JsonOptions) ?? new ReleaseRules();
            rules.Sql ??= new RulesConfig();
            rules.Config ??= new ConfigRules();
            rules.Sql.SeverityOverrides = new Dictionary<string, string>(
                rules.Sql.SeverityOverrides ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            rules.Sql.CreateParser(); // validates parserVersion

            Console.WriteLine($"Rules: {Path.GetFullPath(path)}");
            return rules;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Invalid rules file {path}: {ex.Message}");
            return null;
        }
    }
}
