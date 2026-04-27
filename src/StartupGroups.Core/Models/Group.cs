namespace StartupGroups.Core.Models;

public sealed class Group
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Icon { get; set; } = "Apps24";

    public List<AppEntry> Apps { get; set; } = [];
}
