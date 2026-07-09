using System.Reflection;

namespace Salvo.Core.Branding;

public static class AppBranding
{
    private static readonly Assembly SourceAssembly = typeof(AppBranding).Assembly;

    public static string AppName { get; } =
        SourceAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? "Salvo";

    public static string CompanyName { get; } =
        SourceAssembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
        ?? "Salvo";

    public static string Version { get; } =
        (SourceAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "1.0.0")
        .Split('+')[0];

    public static string Copyright { get; } =
        SourceAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? string.Empty;

    /// <summary>Default WPF-UI SymbolRegular glyph name for a group with no icon set.</summary>
    public const string DefaultGroupIcon = "Apps24";

    public const string AppId = "salvo";
    public const string SupportUrl = "https://github.com/beelzer/salvo";
    public const string AboutUrl = "https://github.com/beelzer/salvo";
    public const string IssueUrl = "https://github.com/beelzer/salvo/issues";
}
