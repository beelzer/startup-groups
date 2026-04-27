namespace StartupGroups.Core.Models;

public readonly record struct ProcessMatcher
{
    public string? ExeName { get; init; }
    public string? Aumid { get; init; }

    public static ProcessMatcher ByExe(string name) => new() { ExeName = name };
    public static ProcessMatcher ByAumid(string aumid) => new() { Aumid = aumid };
}
