namespace Salvo.Core.Services;

/// <summary>
/// Shared GitHub REST access constants, previously copy-pasted across the two
/// update services and the Velopack release source. Only the host / media-type
/// strings and the releases-URL shape are shared here — the HttpClient handler
/// config and DTOs deliberately stay per-caller (one path retires with
/// Velopack).
/// </summary>
public static class GitHubApi
{
    public const string Host = "https://api.github.com";
    public const string AcceptMediaType = "application/vnd.github.v3+json";

    /// <summary>
    /// Builds a releases endpoint for <paramref name="repoPath"/> ("owner/repo"),
    /// with an optional trailing <paramref name="suffix"/> such as "/latest" or
    /// "/tags/v1.2.3".
    /// </summary>
    public static string ReleasesUrl(string repoPath, string suffix = "") =>
        $"{Host}/repos/{repoPath}/releases{suffix}";
}
