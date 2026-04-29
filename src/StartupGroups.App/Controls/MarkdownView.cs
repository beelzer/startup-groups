using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace StartupGroups.App.Controls;

/// <summary>
/// Minimal markdown → FlowDocument renderer scoped to GitHub release notes:
/// # h1 / ## h2 / ### h3, bullet & numbered lists, **bold**, _italic_,
/// `inline code`, ```fenced code```, and [text](url) links. Anything else
/// degrades to plain text. Inherits FlowDocumentScrollViewer so it can be
/// dropped into a XAML tree directly without needing a Generic.xaml.
///
/// Pre-processor rewrites raw GitHub-style references the same way GitHub
/// renders them: bare PR / issue URLs become "#NNN", compare URLs become
/// "tag1...tag2", "@user" mentions and bare http(s) URLs become clickable
/// links. Everything else flows through unchanged.
/// </summary>
public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownView),
            new FrameworkPropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownView()
    {
        Loaded += (_, _) => Render();
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MarkdownView)d).Render();
    }

    private void Render()
    {
        var document = MarkdownToFlowDocument.Convert(Markdown ?? string.Empty);
        document.FontFamily = TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
        document.FontSize = 13;
        document.Foreground = (Brush?)TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Black;
        document.PagePadding = new Thickness(0);
        Document = document;
    }
}

internal static class MarkdownToFlowDocument
{
    private static readonly Regex InlineCode = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*([^*]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<![*_\w])[*_]([^*_\n]+)[*_](?![*_\w])", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.Compiled);

    public static FlowDocument Convert(string markdown)
    {
        var document = new FlowDocument();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return document;
        }

        markdown = GitHubMarkdownPreprocessor.Rewrite(markdown);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                document.Blocks.Add(BuildCodeBlock(string.Join('\n', codeLines)));
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                document.Blocks.Add(BuildHeading(line[4..], 14, FontWeights.SemiBold));
                i++;
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                document.Blocks.Add(BuildHeading(line[3..], 16, FontWeights.SemiBold));
                i++;
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                document.Blocks.Add(BuildHeading(line[2..], 18, FontWeights.SemiBold));
                i++;
                continue;
            }

            if (Bullet.IsMatch(line) || Numbered.IsMatch(line))
            {
                var (block, consumed) = BuildList(lines, i);
                document.Blocks.Add(block);
                i += consumed;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var paraLines = new List<string> { line };
            i++;
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].StartsWith("#", StringComparison.Ordinal)
                   && !lines[i].StartsWith("```", StringComparison.Ordinal)
                   && !Bullet.IsMatch(lines[i])
                   && !Numbered.IsMatch(lines[i]))
            {
                paraLines.Add(lines[i]);
                i++;
            }
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AppendInlines(para.Inlines, string.Join(' ', paraLines));
            document.Blocks.Add(para);
        }

        return document;
    }

    private static Paragraph BuildHeading(string text, double fontSize, FontWeight weight)
    {
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = weight,
            Margin = new Thickness(0, 12, 0, 6),
        };
        AppendInlines(paragraph.Inlines, text.Trim());
        return paragraph;
    }

    private static Block BuildCodeBlock(string code)
    {
        var paragraph = new Paragraph
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(10, 8, 10, 8),
            TextAlignment = TextAlignment.Left,
        };
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        paragraph.Inlines.Add(new Run(code));
        return paragraph;
    }

    private static (Block block, int consumed) BuildList(string[] lines, int start)
    {
        var list = new List
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(0),
            MarkerOffset = 4,
        };

        var i = start;
        var firstNumbered = Numbered.IsMatch(lines[i]);
        list.MarkerStyle = firstNumbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;

        while (i < lines.Length)
        {
            var line = lines[i];
            string? itemText = null;

            var bulletMatch = Bullet.Match(line);
            var numberedMatch = Numbered.Match(line);
            if (bulletMatch.Success && !firstNumbered)
            {
                itemText = bulletMatch.Groups[1].Value;
            }
            else if (numberedMatch.Success && firstNumbered)
            {
                itemText = numberedMatch.Groups[2].Value;
            }
            else
            {
                break;
            }

            var item = new ListItem();
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            AppendInlines(paragraph.Inlines, itemText);
            item.Blocks.Add(paragraph);
            list.ListItems.Add(item);
            i++;
        }

        return (list, i - start);
    }

    private static void AppendInlines(InlineCollection target, string text)
    {
        var segments = new List<(string Kind, string Text, string? Href)>
        {
            ("text", text, null),
        };

        segments = ApplyPattern(segments, Link, m => ("link", m.Groups[1].Value, m.Groups[2].Value));
        segments = ApplyPattern(segments, InlineCode, m => ("code", m.Groups[1].Value, null));
        segments = ApplyPattern(segments, Bold, m => ("bold", m.Groups[1].Value, null));
        segments = ApplyPattern(segments, Italic, m => ("italic", m.Groups[1].Value, null));

        foreach (var (kind, segText, href) in segments)
        {
            switch (kind)
            {
                case "link":
                    var hyperlink = new Hyperlink(new Run(segText));
                    if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += OnHyperlinkNavigate;
                    }
                    target.Add(hyperlink);
                    break;
                case "code":
                    var code = new Run(segText)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                        FontSize = 12,
                    };
                    code.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
                    target.Add(code);
                    break;
                case "bold":
                    target.Add(new Bold(new Run(segText)));
                    break;
                case "italic":
                    target.Add(new Italic(new Run(segText)));
                    break;
                default:
                    target.Add(new Run(segText));
                    break;
            }
        }
    }

    private static List<(string Kind, string Text, string? Href)> ApplyPattern(
        List<(string Kind, string Text, string? Href)> input,
        Regex pattern,
        Func<Match, (string Kind, string Text, string? Href)> map)
    {
        var output = new List<(string Kind, string Text, string? Href)>(input.Count);
        foreach (var seg in input)
        {
            if (seg.Kind != "text")
            {
                output.Add(seg);
                continue;
            }

            var src = seg.Text;
            var lastEnd = 0;
            foreach (Match match in pattern.Matches(src))
            {
                if (match.Index > lastEnd)
                {
                    output.Add(("text", src[lastEnd..match.Index], null));
                }
                output.Add(map(match));
                lastEnd = match.Index + match.Length;
            }
            if (lastEnd < src.Length)
            {
                output.Add(("text", src[lastEnd..], null));
            }
        }
        return output;
    }

    private static void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch
        {
            // Best-effort; ignore failure.
        }
    }
}
