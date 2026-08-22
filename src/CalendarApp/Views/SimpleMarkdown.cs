using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CalendarApp.Views;

/// <summary>
/// Minimal markdown → WPF visual renderer used by the item editor preview.
/// Supports headings, bold/italic/inline code, ordered/unordered lists,
/// blockquotes, horizontal rules and fenced code blocks.
/// </summary>
public static partial class SimpleMarkdown
{
    [GeneratedRegex(@"^#{1,6}\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItemRegex();

    public static void Render(string markdown, StackPanel target)
    {
        target.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            target.Children.Add(Placeholder("（空内容）"));
            return;
        }

        var inCodeFence = false;
        var codeLines = new StringBuilder();

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();

            // Fenced code block
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeFence)
                {
                    target.Children.Add(CodeBlock(codeLines.ToString()));
                    codeLines.Clear();
                }
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                codeLines.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                target.Children.Add(Heading(heading.Groups[1].Value, heading.Value.Length - heading.Groups[1].Value.Length - 1));
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                target.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 6, 0, 6),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
                });
                continue;
            }

            if (line.TrimStart().StartsWith(">"))
            {
                target.Children.Add(BlockQuote(line.TrimStart()[1..].TrimStart()));
                continue;
            }

            var listItem = ListItemRegex().Match(line);
            if (listItem.Success)
            {
                target.Children.Add(ListItem(listItem.Groups[3].Value, listItem.Groups[2].Value));
                continue;
            }

            target.Children.Add(Paragraph(line));
        }

        if (inCodeFence && codeLines.Length > 0)
        {
            target.Children.Add(CodeBlock(codeLines.ToString()));
        }
    }

    private static TextBlock Placeholder(string text) =>
        new() { Text = text, FontSize = 12, Foreground = HexBrush("#94A3B8") };

    private static TextBlock Heading(string text, int level)
    {
        var fontSize = level switch
        {
            1 => 18.0,
            2 => 16.0,
            3 => 14.0,
            _ => 13.0,
        };
        var headingBlock = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = HexBrush("#1A2333"),
            Margin = new Thickness(0, 3, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var inline in BuildInlines(text, 13.0, false))
        {
            headingBlock.Inlines.Add(inline);
        }
        return headingBlock;
    }

    private static TextBlock Paragraph(string text)
    {
        var block = new TextBlock
        {
            FontSize = 13,
            Foreground = HexBrush("#1A2333"),
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var inline in BuildInlines(text, 13.0, false))
        {
            block.Inlines.Add(inline);
        }
        return block;
    }

    private static TextBlock ListItem(string text, string marker)
    {
        var block = new TextBlock
        {
            FontSize = 13,
            Foreground = HexBrush("#1A2333"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 1, 0, 1),
        };
        foreach (var inline in BuildInlines($"{marker} {text}", 13.0, false))
        {
            block.Inlines.Add(inline);
        }
        return block;
    }

    private static TextBlock BlockQuote(string text)
    {
        var block = new TextBlock
        {
            FontSize = 12,
            Foreground = HexBrush("#475569"),
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 2, 0, 2),
        };
        foreach (var inline in BuildInlines(text, 12.0, true))
        {
            block.Inlines.Add(inline);
        }
        return block;
    }

    private static Border CodeBlock(string code) =>
        new()
        {
            Background = HexBrush("#0F172A"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = code.TrimEnd(),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 12,
                Foreground = HexBrush("#E2E8F0"),
                TextWrapping = TextWrapping.Wrap,
            },
        };

    /// <summary>Parses **bold**, *italic*, `code` and ~~strikethrough~~ spans.</summary>
    private static IEnumerable<Inline> BuildInlines(string text, double fontSize, bool italic)
    {
        var baseBrush = HexBrush("#1A2333");
        var parts = Regex.Split(text, @"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|~~[^~]+~~)");
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                yield return new Run(part[2..^2])
                {
                    FontWeight = FontWeights.Bold,
                    FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                    FontSize = fontSize,
                    Foreground = baseBrush,
                };
            }
            else if (part.StartsWith("*") && part.EndsWith("*") && part.Length > 2)
            {
                yield return new Run(part[1..^1])
                {
                    FontStyle = FontStyles.Italic,
                    FontSize = fontSize,
                    Foreground = baseBrush,
                };
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
            {
                yield return new Run(part[1..^1])
                {
                    FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                    FontSize = fontSize,
                    Foreground = HexBrush("#C7254E"),
                };
            }
            else if (part.StartsWith("~~") && part.EndsWith("~~") && part.Length > 4)
            {
                yield return new Run(part[2..^2])
                {
                    TextDecorations = TextDecorations.Strikethrough,
                    FontSize = fontSize,
                    Foreground = baseBrush,
                };
            }
            else
            {
                yield return new Run(part)
                {
                    FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                    FontSize = fontSize,
                    Foreground = baseBrush,
                };
            }
        }
    }

    private static SolidColorBrush HexBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
