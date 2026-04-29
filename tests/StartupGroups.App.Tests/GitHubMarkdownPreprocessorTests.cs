using StartupGroups.App.Controls;

namespace StartupGroups.App.Tests;

/// <summary>
/// Locks down the rewrite rules used by the Update available flyout's
/// release-notes panel. The auto-generated GitHub release body comes in
/// with bare URLs and @-mentions; these tests pin the transformations so
/// a future regex tweak doesn't quietly fall back to printing raw URLs.
/// </summary>
public sealed class GitHubMarkdownPreprocessorTests
{
    [Fact]
    public void Rewrite_PrUrl_BecomesShortHashLink()
    {
        var input = "fix: thing by @beelzer in https://github.com/beelzer/startup-groups/pull/53";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Contain("[#53](https://github.com/beelzer/startup-groups/pull/53)");
    }

    [Fact]
    public void Rewrite_IssueUrl_BecomesShortHashLink()
    {
        var input = "closes https://github.com/beelzer/startup-groups/issues/42";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Contain("[#42](https://github.com/beelzer/startup-groups/issues/42)");
    }

    [Fact]
    public void Rewrite_CompareUrl_BecomesTagSpanLink()
    {
        var input = "**Full Changelog**: https://github.com/beelzer/startup-groups/compare/v0.2.14-canary.10...v0.2.14-canary.11";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Contain("[v0.2.14-canary.10...v0.2.14-canary.11](https://github.com/beelzer/startup-groups/compare/v0.2.14-canary.10...v0.2.14-canary.11)");
    }

    [Fact]
    public void Rewrite_AtMention_InProse_BecomesUserLink()
    {
        var input = "thanks @octocat for the report";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Contain("[@octocat](https://github.com/octocat)");
    }

    [Fact]
    public void Rewrite_AtMention_InEmail_IsLeftAlone()
    {
        // Email-style @ inside a word boundary should not be mistaken for
        // a username mention. The lookbehind disallows preceding word chars.
        var input = "ping noreply@anthropic.com if blocked";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Be(input);
    }

    [Fact]
    public void Rewrite_BareHttpUrl_IsAutolinked()
    {
        var input = "see https://example.com/docs for details";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Contain("[https://example.com/docs](https://example.com/docs)");
    }

    [Fact]
    public void Rewrite_UrlAlreadyInsideMarkdownLink_IsLeftAlone()
    {
        // A URL inside an existing (url) segment must not be re-wrapped —
        // would produce double brackets and break rendering.
        var input = "see [docs](https://example.com/docs) for details";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Be(input);
    }

    [Fact]
    public void Rewrite_PrUrlAlreadyInsideMarkdownLink_IsLeftAlone()
    {
        var input = "review the [original PR](https://github.com/beelzer/startup-groups/pull/53)";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Be(input);
    }

    [Fact]
    public void Rewrite_AtMentionAlreadyInsideMarkdownLink_IsLeftAlone()
    {
        // The lookbehind also excludes [ so that an @ at the start of an
        // existing link text doesn't get re-wrapped into nested brackets.
        var input = "[@octocat](https://github.com/octocat)";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);
        output.Should().Be(input);
    }

    [Fact]
    public void Rewrite_RealReleaseBody_ProducesExpectedLinks()
    {
        // The literal shape of an auto-generated GitHub release body, which
        // is what we're trying to format the way github.com renders it.
        var input = "## What's Changed\n* fix: thing by @beelzer in https://github.com/beelzer/startup-groups/pull/53\n\n**Full Changelog**: https://github.com/beelzer/startup-groups/compare/v0.2.14-canary.10...v0.2.14-canary.11";
        var output = GitHubMarkdownPreprocessor.Rewrite(input);

        output.Should().Contain("[@beelzer](https://github.com/beelzer)");
        output.Should().Contain("[#53](https://github.com/beelzer/startup-groups/pull/53)");
        output.Should().Contain("[v0.2.14-canary.10...v0.2.14-canary.11]");
        // No leftover bare PR/compare URL outside a markdown-link parens segment.
        output.Should().NotMatchRegex(@"(?<!\()https://github\.com/beelzer/startup-groups/(pull|compare)/");
    }

    [Fact]
    public void Rewrite_EmptyInput_ReturnsEmpty()
    {
        GitHubMarkdownPreprocessor.Rewrite(string.Empty).Should().Be(string.Empty);
    }
}
