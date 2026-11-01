namespace ReleaseChecker.Checks.Config;

/// <summary>Rule IDs for the CONFIG check (usable in config.severityOverrides).</summary>
public static class ConfigRuleIds
{
    public const string EnvMissing = "CONFIG_ENV_MISSING";
    public const string AppMissing = "CONFIG_APP_MISSING";
    public const string FileMissing = "CONFIG_FILE_MISSING";
    public const string InvalidFile = "CONFIG_INVALID_FILE";
    public const string DuplicateKey = "CONFIG_DUPLICATE_KEY";
    public const string KeyMissing = "CONFIG_KEY_MISSING";
    public const string RequiredKeyMissing = "CONFIG_REQUIRED_KEY_MISSING";
    public const string RequiredKeyEmpty = "CONFIG_REQUIRED_KEY_EMPTY";
    public const string SameAcrossEnv = "CONFIG_SAME_ACROSS_ENV";
    public const string GroupNotSame = "CONFIG_GROUP_NOT_SAME";
    public const string GroupNotDifferent = "CONFIG_GROUP_NOT_DIFFERENT";
}

/// <summary>The "config" section of rules.json. Every property has a default.</summary>
public sealed class ConfigRules
{
    /// <summary>
    /// Environment root, relative to --repo or absolute. Overridden by --config-path.
    /// Every folder below it that holds one of the config files is an application, at any depth,
    /// e.g. Online/Web/V2/RIMCoreOrderService or Batch/Programs/RIMSInternal/RIMS.Programs.V2.
    /// {country} is optional; {env} is required.
    /// </summary>
    public string PathTemplate { get; set; } = "{country}/App/Config/{env}";

    /// <summary>Environments compared against each other.</summary>
    public List<string> Environments { get; set; } = new() { "SIT", "UAT", "PROD" };

    /// <summary>
    /// Config files compared across environments. A file present in one environment must exist in all of them.
    /// requiredIn = application path patterns that must always have it.
    /// </summary>
    public List<ConfigFileSpec> Files { get; set; } = new()
    {
        new() { Name = "appsettings.json", Format = "json", RequiredIn = new() { "*" } },
        new() { Name = "nlog.config", Format = "xml", RequiredIn = new() { "*" } },
        new() { Name = "web.config", Format = "xml", RequiredIn = new() { "*Web*" } },
    };

    /// <summary>Application paths to skip (wildcards), e.g. "Online/Web/V1/*".</summary>
    public List<string> ExcludeApplications { get; set; } = new();

    /// <summary>XML attributes that identify an element, e.g. &lt;add key="BaseUrl"&gt; or &lt;target name="file"&gt;.</summary>
    public List<string> XmlIdentityAttributes { get; set; } = new() { "key", "name", "id" };

    /// <summary>Keys left out of every comparison (wildcards). See the report for key names.</summary>
    public List<string> IgnoreKeys { get; set; } = new();

    /// <summary>Keys that must exist with a non-empty value, e.g. the WMQConfiguration block.</summary>
    public List<RequiredKeysRule> RequiredKeys { get; set; } = new()
    {
        new()
        {
            Name = "WMQConfiguration",
            File = "appsettings.json",
            WhenSectionExists = "WMQConfiguration",
            Keys = new()
            {
                "WMQConfiguration:ActiveMQManager:Hostname", "WMQConfiguration:ActiveMQManager:PortNo",
                "WMQConfiguration:ActiveMQManager:QueueManager", "WMQConfiguration:ActiveMQManager:Channel",
                "WMQConfiguration:PassiveMQManager:Hostname", "WMQConfiguration:PassiveMQManager:PortNo",
                "WMQConfiguration:PassiveMQManager:QueueManager", "WMQConfiguration:PassiveMQManager:Channel",
                "WMQConfiguration:EnquiryQueues:SendQueueName", "WMQConfiguration:EnquiryQueues:ReceiveQueueName",
                "WMQConfiguration:EnquiryQueues:TimeOutPeriod",
                "WMQConfiguration:TransactionQueues:SendQueueName", "WMQConfiguration:TransactionQueues:ReceiveQueueName",
                "WMQConfiguration:TransactionQueues:TimeOutPeriod",
                "WMQConfiguration:CompositeQueues:SendQueueName", "WMQConfiguration:CompositeQueues:ReceiveQueueName",
                "WMQConfiguration:CompositeQueues:TimeOutPeriod",
                "WMQConfiguration:UserId", "WMQConfiguration:ChannelId",
            },
        },
    };

    /// <summary>Keys whose value must be different in every environment.</summary>
    public List<MustDifferRule> MustDiffer { get; set; } = new()
    {
        new() { Key = "*ConnectionString*", CompareAs = "ConnectionString" },
        new() { Key = "*BaseUrl*", CompareAs = "UrlHost" },
        new() { Key = "WMQConfiguration:*MQManager:Hostname", CompareAs = "Host" },
    };

    /// <summary>Applications that share some files and must differ in others.</summary>
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

    /// <summary>Application path patterns that must have this file. Empty = only required when another environment has it.</summary>
    public List<string> RequiredIn { get; set; } = new();

    public bool IsJson => (Format ?? "").Equals("json", StringComparison.OrdinalIgnoreCase) ||
                          (Format is null && Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
}

public sealed class RequiredKeysRule
{
    /// <summary>Shown in the report, e.g. "WMQConfiguration".</summary>
    public string Name { get; set; } = "";

    public string File { get; set; } = "appsettings.json";

    /// <summary>Application path patterns this rule applies to (default all).</summary>
    public List<string> Applications { get; set; } = new() { "*" };

    /// <summary>
    /// Only check applications that use this section (in any environment), e.g. "WMQConfiguration".
    /// Null = the keys are always required.
    /// </summary>
    public string? WhenSectionExists { get; set; }

    /// <summary>Full keys, e.g. "WMQConfiguration:ActiveMQManager:Hostname".</summary>
    public List<string> Keys { get; set; } = new();

    public bool AllowEmpty { get; set; } = false;
}

public sealed class MustDifferRule
{
    /// <summary>Key pattern, e.g. "*ConnectionString*" or "*BaseUrl*".</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// ConnectionString = compare server + database (ignores timeouts, credentials, ...),
    /// UrlHost = compare URL host + port, Host = compare a host name value (shown in the report),
    /// Exact = compare the whole value (never shown). Values that don't parse as that type are skipped.
    /// </summary>
    public string CompareAs { get; set; } = "Exact";
}

public sealed class ServiceGroup
{
    /// <summary>Application folder names (e.g. RIMCoreOrderService) or full application paths.</summary>
    public List<string> Services { get; set; } = new();

    /// <summary>Files that must be identical (same keys and values) across the group, per environment.</summary>
    public List<string> SameFiles { get; set; } = new();

    /// <summary>Files that must NOT be identical across the group, per environment.</summary>
    public List<string> DifferentFiles { get; set; } = new();
}
