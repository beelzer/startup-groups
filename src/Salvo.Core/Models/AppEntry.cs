namespace Salvo.Core.Models;

public sealed class AppEntry
{
    public string Name { get; set; } = string.Empty;

    // Serialized as a string via ConfigurationJsonContext's context-wide
    // UseStringEnumConverter; no per-property converter needed.
    public AppKind Kind { get; set; } = AppKind.Executable;

    public string? Path { get; set; }

    public string? Service { get; set; }

    public string? Args { get; set; }

    public string? WorkingDirectory { get; set; }

    public int DelayAfterSeconds { get; set; }

    public bool Enabled { get; set; } = true;
}
