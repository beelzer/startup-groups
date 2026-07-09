using Salvo.Core.Services;

namespace Salvo.Core.Tests;

/// <summary>
/// Covers AppOrchestrator.ParseDirect, which splits a "direct" RunCommand into
/// an exe + arguments — including the quoted-path, no-arg, and unbalanced-quote
/// inputs that are otherwise only exercised indirectly.
/// </summary>
public sealed class ParseDirectTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_FallsBackToCmd(string command)
    {
        var (fileName, args) = AppOrchestrator.ParseDirect(command);
        fileName.Should().Be("cmd.exe");
        args.Should().Be("/c ");
    }

    [Fact]
    public void QuotedPath_WithArgs_SplitsExeFromArgs()
    {
        var (fileName, args) = AppOrchestrator.ParseDirect("\"C:\\Program Files\\app.exe\" --flag value");
        fileName.Should().Be(@"C:\Program Files\app.exe");
        args.Should().Be("--flag value");
    }

    [Fact]
    public void QuotedPath_NoArgs_HasEmptyArgs()
    {
        var (fileName, args) = AppOrchestrator.ParseDirect("\"C:\\tools\\app.exe\"");
        fileName.Should().Be(@"C:\tools\app.exe");
        args.Should().BeEmpty();
    }

    [Fact]
    public void UnquotedPath_WithSpace_SplitsAtFirstSpace()
    {
        var (fileName, args) = AppOrchestrator.ParseDirect("app.exe --flag x");
        fileName.Should().Be("app.exe");
        args.Should().Be("--flag x");
    }

    [Fact]
    public void UnquotedPath_NoSpace_HasEmptyArgs()
    {
        var (fileName, args) = AppOrchestrator.ParseDirect("app.exe");
        fileName.Should().Be("app.exe");
        args.Should().BeEmpty();
    }

    [Fact]
    public void UnbalancedQuote_FallsBackToSpaceSplit()
    {
        // No closing quote → the quoted-path branch is skipped and the plain
        // space split runs, so the opening quote stays on the file name.
        var (fileName, args) = AppOrchestrator.ParseDirect("\"app.exe --flag");
        fileName.Should().Be("\"app.exe");
        args.Should().Be("--flag");
    }
}
