using System.Text.Json.Serialization;

namespace StartupGroups.Core.Models;

public sealed class KnownAppsFile
{
    [JsonPropertyName("entries")]
    public List<KnownApp> Entries { get; set; } = [];
}

public sealed class KnownApp
{
    public string Name { get; set; } = string.Empty;
    public KnownAppMatch Match { get; set; } = new();
    public List<KnownArgument> Arguments { get; set; } = [];
}

public sealed class KnownAppMatch
{
    public string? ExeName { get; set; }
    public string? Aumid { get; set; }
    public string? ShellParseName { get; set; }
}

public sealed class KnownArgument
{
    public string Flag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool NeedsValue { get; set; }
}
