namespace ReleaseChecker.Checks.Config;

/// <summary>Rule IDs for the CONFIG check (usable in config.severityOverrides).</summary>
public static class ConfigRuleIds
{
    public const string EnvMissing = "CONFIG_ENV_MISSING";
    public const string ServiceMissing = "CONFIG_SERVICE_MISSING";
    public const string FileMissing = "CONFIG_FILE_MISSING";
    public const string InvalidFile = "CONFIG_INVALID_FILE";
    public const string DuplicateKey = "CONFIG_DUPLICATE_KEY";
    public const string KeyMissing = "CONFIG_KEY_MISSING";
    public const string SameAcrossEnv = "CONFIG_SAME_ACROSS_ENV";
    public const string GroupNotSame = "CONFIG_GROUP_NOT_SAME";
    public const string GroupNotDifferent = "CONFIG_GROUP_NOT_DIFFERENT";
}

/// <summary>The "config" section of rules.json. Every property has a default.</summary>
public sealed class ConfigRules
{
    /// <summary>Where a service's config folder is, relative to the repo. Must end with {service}.</summary>
    public string PathTemplate { get; set; } = "{country}/App/Config/{env}/Web/{service}";

    /// <summary>Environments compared against each other.</summary>
    public List<string> Environments { get; set; } = new() { "SIT", "UAT", "PROD" };

    /// <summary>Files every service must have in every environment. Format: json or xml (default from extension).</summary>
    public List<ConfigFileSpec> Files { get; set; } = new()
    {
        new() { Name = "appsettings.json", Format = "json" },
        new() { Name = "nlog.config", Format = "xml" },
        new() { Name = "web.config", Format = "xml" },
    };

    /// <summary>XML attributes that identify an element, e.g. &lt;add key="BaseUrl"&gt; or &lt;target name="file"&gt;.</summary>
    public List<string> XmlIdentityAttributes { get; set; } = new() { "key", "name", "id" };

    /// <summary>Keys left out of every comparison (wildcards). See the report for key names.</summary>
    public List<string> IgnoreKeys { get; set; } = new();

    /// <summary>Keys whose value must be different in every environment.</summary>
    public List<MustDifferRule> MustDiffer { get; set; } = new()
    {
        new() { Key = "*ConnectionString*", CompareAs = "ConnectionString" },
        new() { Key = "*BaseUrl*", CompareAs = "UrlHost" },
    };

    /// <summary>Services that share some files and must differ in others.</summary>
    public List<ServiceGroup> ServiceGroups { get; set; } = new()
    {
        new()
        {
            Services = new() { "RIMCoreOrderService", "RIMCoreAppInterface" },
            SameFiles = new() { "appsettings.json" },
            DifferentFiles = new() { "nlog.config" },
        },
        new()
        {
            Services = new() { "RIMCoreAccountService", "RIMCoreAccountInterface" },
            SameFiles = new() { "appsettings.json" },
            DifferentFiles = new() { "nlog.config" },
        },
    };

    /// <summary>RuleId -> "Error" | "Warning" | "Info" | "Off".</summary>
    public Dictionary<string, string> SeverityOverrides { get; set; } = new();
}

public sealed class ConfigFileSpec
{
    public string Name { get; set; } = "";
    public string? Format { get; set; }

    public bool IsJson => (Format ?? "").Equals("json", StringComparison.OrdinalIgnoreCase) ||
                          (Format is null && Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
}

public sealed class MustDifferRule
{
    /// <summary>Key pattern, e.g. "*ConnectionString*" or "*BaseUrl*".</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// ConnectionString = compare server + database (ignores timeouts, credentials, ...),
    /// UrlHost = compare host + port, Exact = compare the whole value.
    /// Values that don't parse as that type are skipped.
    /// </summary>
    public string CompareAs { get; set; } = "Exact";
}

public sealed class ServiceGroup
{
    public List<string> Services { get; set; } = new();

    /// <summary>Files that must be identical (same keys and values) across the group, per environment.</summary>
    public List<string> SameFiles { get; set; } = new();

    /// <summary>Files that must NOT be identical across the group, per environment.</summary>
    public List<string> DifferentFiles { get; set; } = new();
}
