using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ReleaseChecker.Checks.Config;

/// <summary>A config file flattened to key -> value, so files can be compared regardless of formatting or order.</summary>
public sealed class ConfigFileData
{
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DuplicateKeys { get; } = new();
    public string? Error { get; init; }

    /// <summary>Last one wins, like .NET configuration.</summary>
    public void Set(string key, string? value) => Values[key] = value;
}

/// <summary>
/// appsettings.json -> "Logging:LogLevel:Default" (same keys as .NET IConfiguration, case-insensitive).
/// XML (nlog.config, web.config) -> "configuration:appSettings:add[BaseUrl]:@value",
/// using identity attributes (key/name/id) so element order doesn't matter.
/// </summary>
public static class ConfigFlattener
{
    public static ConfigFileData Load(string path, bool isJson, IReadOnlyCollection<string> identityAttributes)
    {
        try
        {
            return isJson ? LoadJson(path) : LoadXml(path, identityAttributes);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or IOException or UnauthorizedAccessException)
        {
            return new ConfigFileData { Error = ex.Message };
        }
    }

    // ---------- JSON ----------
    private static ConfigFileData LoadJson(string path)
    {
        var data = new ConfigFileData();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
        WalkJson(doc.RootElement, "", data);
        return data;
    }

    private static void WalkJson(JsonElement e, string prefix, ConfigFileData data, bool quiet = false)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool anyProperty = false;
                foreach (var p in e.EnumerateObject())
                {
                    anyProperty = true;
                    string key = prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}";

                    // same property declared twice in one object: report once, don't repeat for its children
                    bool duplicate = !seen.Add(p.Name);
                    if (duplicate && !quiet) data.DuplicateKeys.Add(key);

                    WalkJson(p.Value, key, data, quiet || duplicate);
                }
                if (!anyProperty && prefix.Length > 0) data.Set(prefix, ""); // keep empty sections for key parity
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in e.EnumerateArray())
                    WalkJson(item, $"{prefix}:{i++}", data, quiet);
                if (i == 0 && prefix.Length > 0) data.Set(prefix, "");
                break;

            case JsonValueKind.String:
                data.Set(prefix, e.GetString());
                break;

            case JsonValueKind.Null:
                data.Set(prefix, null);
                break;

            default: // number, true, false
                data.Set(prefix, e.GetRawText());
                break;
        }
    }

    // ---------- XML ----------
    private static ConfigFileData LoadXml(string path, IReadOnlyCollection<string> identityAttributes)
    {
        var data = new ConfigFileData();
        var doc = XDocument.Load(path, LoadOptions.None);
        if (doc.Root is not null)
            WalkXml(doc.Root, doc.Root.Name.LocalName, data, identityAttributes);
        return data;
    }

    private static void WalkXml(XElement el, string path, ConfigFileData data, IReadOnlyCollection<string> ids)
    {
        var identity = Identity(el, ids);
        bool hasContent = false;

        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration || attr == identity) continue;
            data.Set($"{path}:@{attr.Name.LocalName}", attr.Value);
            hasContent = true;
        }

        if (!el.HasElements && !string.IsNullOrWhiteSpace(el.Value))
        {
            data.Set(path, el.Value.Trim());
            hasContent = true;
        }

        // child segment = element name, plus [identity] when it has one; [n] when siblings would collide
        var children = el.Elements()
            .Select(c => (Element: c, Segment: Segment(c, ids)))
            .ToList();

        foreach (var group in children.GroupBy(c => c.Segment, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            bool identified = Identity(items[0].Element, ids) is not null;

            if (items.Count > 1 && identified)
                data.DuplicateKeys.Add($"{path}:{group.Key}"); // e.g. two <add key="BaseUrl"> in appSettings

            for (int i = 0; i < items.Count; i++)
            {
                string segment = items.Count > 1 ? $"{group.Key}[{i}]" : group.Key;
                WalkXml(items[i].Element, $"{path}:{segment}", data, ids);
                hasContent = true;
            }
        }

        if (!hasContent)
            data.Set(path, ""); // keep empty elements (e.g. <target name="x" />) for key parity
    }

    private static XAttribute? Identity(XElement el, IReadOnlyCollection<string> ids) =>
        el.Attributes().FirstOrDefault(a =>
            !a.IsNamespaceDeclaration && ids.Contains(a.Name.LocalName, StringComparer.OrdinalIgnoreCase));

    private static string Segment(XElement el, IReadOnlyCollection<string> ids) =>
        Identity(el, ids) is { } id ? $"{el.Name.LocalName}[{id.Value}]" : el.Name.LocalName;
}
