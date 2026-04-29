using System.Text.RegularExpressions;

namespace StartupGroups.App.Controls;

/// <summary>
/// Rewrites bare GitHub-style references in a release-notes markdown blob
/// into normal <c>[text](url)</c> markdown links so the downstream renderer
/// can display them the way github.com does:
/// <list type="bullet">
///   <item><c>https://github.com/owner/repo/pull/NN</c> → <c>#NN</c></item>
///   <item><c>https://github.com/owner/repo/issues/NN</c> → <c>#NN</c></item>
///   <item><c>https://github.com/owner/repo/compare/A...B</c> → <c>A...B</c></item>
///   <item><c>@user</c> in plain prose → <c>[@user](https://github.com/user)</c></item>
///   <item>bare <c>http(s)://…</c> URLs → autolinked</item>
/// </list>
/// All four URL-shape patterns use a <c>(?&lt;!\()</c> lookbehind so URLs
/// already inside a markdown link's <c>(url)</c> segment are left alone.
/// Order matters: URL-shape patterns must run before bare-URL autolink so
/// they get to assign nicer display text first.
/// </summary>
public static class GitHubMarkdownPreprocessor
{
    private static readonly Regex GhPrIssueUrl = new(
        @"(?<!\()https://github\.com/[^/\s)]+/[^/\s)]+/(?:pull|issues)/(\d+)",
        RegexOptions.Compiled);

    private static readonly Regex GhCompareUrl = new(
        @"(?<!\()https://github\.com/[^/\s)]+/[^/\s)]+/compare/([^\s)\]]+)",
        RegexOptions.Compiled);

    // GitHub usernames: alphanumeric, hyphens allowed but not at the ends and
    // not consecutive. The negative lookbehind keeps email addresses and
    // existing markdown link text from triggering.
    private static readonly Regex AtMention = new(
        @"(?<![\w/\[])@([a-zA-Z0-9](?:[a-zA-Z0-9]|-(?=[a-zA-Z0-9])){0,38})",
        RegexOptions.Compiled);

    private static readonly Regex BareUrl = new(
        @"(?<![\(\]])https?://[^\s\)\]]+",
        RegexOptions.Compiled);

    public static string Rewrite(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        markdown = GhPrIssueUrl.Replace(markdown, m => $"[#{m.Groups[1].Value}]({m.Value})");
        markdown = GhCompareUrl.Replace(markdown, m => $"[{m.Groups[1].Value}]({m.Value})");
        markdown = AtMention.Replace(markdown, m => $"[@{m.Groups[1].Value}](https://github.com/{m.Groups[1].Value})");
        markdown = BareUrl.Replace(markdown, m => $"[{m.Value}]({m.Value})");
        return markdown;
    }
}
