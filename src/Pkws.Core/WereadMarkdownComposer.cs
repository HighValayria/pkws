using System.Text;

namespace Pkws.Core;

public sealed class WereadMarkdownComposer
{
    public string Compose(SourceContext source, string comment)
    {
        var builder = new StringBuilder();
        builder.Append("**来源：微信读书**");

        var title = Inline(source.WorkTitle);
        var chapter = Inline(source.ChapterTitle);
        var url = SafeWereadUrl(source.Url);
        if (title.Length > 0)
        {
            builder.Append(" · ");
            if (url.Length > 0)
            {
                builder.Append('[').Append(title).Append("](").Append(url).Append(')');
            }
            else
            {
                builder.Append(title);
            }
        }
        else if (url.Length > 0)
        {
            builder.Append(" · [打开原文](").Append(url).Append(')');
        }

        if (chapter.Length > 0)
        {
            builder.Append(" · ").Append(chapter);
        }

        var authors = source.Authors.Select(Inline).Where(value => value.Length > 0).ToArray();
        if (authors.Length > 0)
        {
            builder.Append(" · ").AppendJoin("、", authors);
        }

        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(source.SelectedText))
        {
            builder.AppendLine();
            builder.AppendLine("> **原文**");
            builder.AppendLine(">");
            var normalized = source.SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            foreach (var line in normalized.Split('\n'))
            {
                builder.Append("> ").AppendLine(line);
            }
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            builder.AppendLine();
            builder.AppendLine("**我的笔记**");
            builder.AppendLine();
            builder.Append(comment.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string Inline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string SafeWereadUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(uri.Host, "weread.qq.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".weread.qq.com", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return uri.AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }
}
