namespace StartupGroups.Core.Models;

public sealed class Configuration
{
    public int Version { get; set; } = 1;

    public List<Group> Groups { get; set; } = [];
}
