using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pkws.Desktop;

public static partial class MarkdownDocumentRenderer
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(43, 39, 36));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(112, 102, 94));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(154, 91, 50));
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromRgb(245, 241, 236));

    public static FlowDocument CreateDocument(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 16,
            Foreground = Ink,
            LineHeight = double.NaN
        };

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                index = AddCodeBlock(document, lines, index);
                continue;
            }

            if (TryGetHeading(line, out var level, out var heading))
            {
                document.Blocks.Add(CreateParagraph(heading, level switch
                {
                    1 => 25,
                    2 => 21,
                    _ => 18
                }, FontWeights.SemiBold, new Thickness(0, 4, 0, 10)));
                index++;
                continue;
            }

            if (IsHorizontalRule(line))
            {
                document.Blocks.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 10, 0, 12),
                    Background = new SolidColorBrush(Color.FromRgb(226, 217, 208))
                }));
                index++;
                continue;
            }

            if (TryGetListItem(line, out var ordered, out _))
            {
                index = AddList(document, lines, index, ordered);
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")
            {
                var quote = new List<string>();
                while (index < lines.Length && (lines[index].StartsWith("> ", StringComparison.Ordinal) || lines[index] == ">"))
                {
                    quote.Add(lines[index].Length > 1 ? lines[index][2..] : string.Empty);
                    index++;
                }

                var paragraph = CreateParagraph(string.Join('\n', quote), 15, FontWeights.Normal, new Thickness(0, 4, 0, 12));
                paragraph.Foreground = Muted;
                paragraph.BorderBrush = Accent;
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.Padding = new Thickness(12, 2, 0, 2);
                document.Blocks.Add(paragraph);
                continue;
            }

            var paragraphLines = new List<string>();
            while (index < lines.Length &&
                   !string.IsNullOrWhiteSpace(lines[index]) &&
                   !lines[index].StartsWith("```", StringComparison.Ordinal) &&
                   !TryGetHeading(lines[index], out _, out _) &&
                   !TryGetListItem(lines[index], out _, out _) &&
                   !lines[index].StartsWith("> ", StringComparison.Ordinal) &&
                   lines[index] != ">" &&
                   !IsHorizontalRule(lines[index]))
            {
                paragraphLines.Add(lines[index]);
                index++;
            }

            document.Blocks.Add(CreateParagraph(string.Join('\n', paragraphLines), 16, FontWeights.Normal, new Thickness(0, 0, 0, 12)));
        }

        if (document.Blocks.Count == 0)
        {
            var empty = new Paragraph(new Run("在左侧编辑模式输入 Markdown，这里会显示预览。"))
            {
                Foreground = Muted,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0)
            };
            document.Blocks.Add(empty);
        }

        return document;
    }

    public static FlowDocumentScrollViewer CreateViewer(string markdown) => new()
    {
        Document = CreateDocument(markdown),
        Margin = new Thickness(0, 7, 0, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        IsToolBarVisible = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private static int AddCodeBlock(FlowDocument document, IReadOnlyList<string> lines, int start)
    {
        var code = new List<string>();
        var index = start + 1;
        while (index < lines.Count && !lines[index].StartsWith("```", StringComparison.Ordinal))
        {
            code.Add(lines[index]);
            index++;
        }

        var paragraph = new Paragraph(new Run(string.Join('\n', code)))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 14,
            Background = CodeBackground,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 4, 0, 12)
        };
        document.Blocks.Add(paragraph);
        return index < lines.Count ? index + 1 : index;
    }

    private static int AddList(FlowDocument document, IReadOnlyList<string> lines, int start, bool ordered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(20, 2, 0, 12),
            Padding = new Thickness(0)
        };
        var index = start;
        while (index < lines.Count && TryGetListItem(lines[index], out var currentOrdered, out var content) && currentOrdered == ordered)
        {
            list.ListItems.Add(new ListItem(CreateParagraph(content, 16, FontWeights.Normal, new Thickness(0, 1, 0, 3))));
            index++;
        }

        document.Blocks.Add(list);
        return index;
    }

    private static Paragraph CreateParagraph(string text, double fontSize, FontWeight weight, Thickness margin)
    {
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = weight,
            Margin = margin
        };
        AddInlineContent(paragraph.Inlines, text);
        return paragraph;
    }

    private static void AddInlineContent(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match match in InlineTokenRegex().Matches(text))
        {
            if (match.Index > position)
            {
                AddRunWithLineBreaks(inlines, text[position..match.Index]);
            }

            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
            {
                inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeights.Bold });
            }
            else if (token.StartsWith('`'))
            {
                inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 14,
                    Background = CodeBackground
                });
            }
            else if (token.StartsWith('*'))
            {
                inlines.Add(new Run(token[1..^1]) { FontStyle = FontStyles.Italic });
            }
            else
            {
                var closingBracket = token.IndexOf("](", StringComparison.Ordinal);
                var label = token[1..closingBracket];
                var target = token[(closingBracket + 2)..^1];
                inlines.Add(new Run(label)
                {
                    Foreground = Accent,
                    TextDecorations = TextDecorations.Underline,
                    ToolTip = target
                });
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            AddRunWithLineBreaks(inlines, text[position..]);
        }
    }

    private static void AddRunWithLineBreaks(InlineCollection inlines, string text)
    {
        var segments = text.Split('\n');
        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (segments[index].Length > 0)
            {
                inlines.Add(new Run(segments[index]));
            }
        }
    }

    private static bool TryGetHeading(string line, out int level, out string content)
    {
        level = 0;
        while (level < line.Length && level < 3 && line[level] == '#')
        {
            level++;
        }

        if (level > 0 && line.Length > level && line[level] == ' ')
        {
            content = line[(level + 1)..];
            return true;
        }

        content = string.Empty;
        return false;
    }

    private static bool TryGetListItem(string line, out bool ordered, out string content)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("* ", StringComparison.Ordinal) ||
            line.StartsWith("+ ", StringComparison.Ordinal))
        {
            ordered = false;
            content = line[2..];
            return true;
        }

        var match = OrderedListRegex().Match(line);
        if (match.Success)
        {
            ordered = true;
            content = match.Groups[1].Value;
            return true;
        }

        ordered = false;
        content = string.Empty;
        return false;
    }

    private static bool IsHorizontalRule(string line)
    {
        var trimmed = line.Trim();
        return trimmed is "---" or "***" or "___";
    }

    [GeneratedRegex(@"(\*\*[^\r\n*]+\*\*|`[^\r\n`]+`|\*[^\r\n*]+\*|\[[^\]\r\n]+\]\([^\)\r\n]+\))")]
    private static partial Regex InlineTokenRegex();

    [GeneratedRegex(@"^\d+\.\s+(.+)$")]
    private static partial Regex OrderedListRegex();
}
