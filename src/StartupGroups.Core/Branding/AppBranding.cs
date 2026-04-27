using System.Reflection;

namespace StartupGroups.Core.Branding;

public static class AppBranding
{
    private static readonly Assembly SourceAssembly = typeof(AppBranding).Assembly;

    public static string AppName { get; } =
        SourceAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? "Startup Groups";

    public static string CompanyName { get; } =
        SourceAssembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
        ?? "Startup Groups";

    public static string Version { get; } =
        (SourceAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];

    public static string Copyright { get; } =
        SourceAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? string.Empty;

    public const string AppId = "startup-groups";
    public const string SupportUrl = "https://github.com/beelzer/startup-groups";
    public const string AboutUrl = "https://github.com/beelzer/startup-groups";
    public const string IssueUrl = "https://github.com/beelzer/startup-groups/issues";
}
