using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public sealed class CodeFoldingService
{
    public ImmutableArray<CodeFoldRegion> CreateFoldRegions(DiffDocumentSnapshot document)
    {
        if (document.Lines.IsDefaultOrEmpty)
        {
            return [];
        }

        var descriptor = LanguageServiceRegistry.Identify(document);
        var builder = ImmutableArray.CreateBuilder<CodeFoldRegion>();
        AddRegionDirectiveFoldings(document, builder);
        AddBraceFoldings(document, builder);

        if (descriptor.Definition?.SyntaxKind is LanguageSyntaxKind.Xml)
        {
            AddXmlFoldings(document, builder);
        }

        if (descriptor.Definition?.SyntaxKind is LanguageSyntaxKind.Yaml ||
            descriptor.Id is "python" or "ruby" or "shellscript")
        {
            AddIndentationFoldings(document, builder);
        }

        return builder
            .Where(region => region.EndLineIndex > region.StartLineIndex)
            .GroupBy(region => region.StartLineIndex)
            .Select(group => group.OrderByDescending(region => region.EndLineIndex).First())
            .OrderBy(region => region.StartLineIndex)
            .ThenByDescending(region => region.EndLineIndex)
            .ToImmutableArray();
    }

    private static void AddRegionDirectiveFoldings(DiffDocumentSnapshot document, ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        var stack = new Stack<(int LineIndex, string Title)>();
        foreach (var line in document.Lines)
        {
            var trimmed = line.Text.Trim();
            if (trimmed.StartsWith("#region", StringComparison.Ordinal))
            {
                var title = trimmed.Length > "#region".Length ? trimmed["#region".Length..].Trim() : "#region";
                stack.Push((line.Index, string.IsNullOrWhiteSpace(title) ? "#region" : title));
            }
            else if (trimmed.StartsWith("#endregion", StringComparison.Ordinal) && stack.TryPop(out var start))
            {
                builder.Add(new CodeFoldRegion(start.LineIndex, line.Index, start.Title));
            }
        }
    }

    private static void AddBraceFoldings(DiffDocumentSnapshot document, ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        var stack = new Stack<int>();
        foreach (var line in document.Lines)
        {
            var text = StripLineComment(line.Text);
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (character == '{')
                {
                    stack.Push(ResolveBraceFoldStartLine(document.Lines, line.Index, index));
                }
                else if (character == '}' && stack.TryPop(out var startLine) && line.Index > startLine)
                {
                    builder.Add(new CodeFoldRegion(startLine, line.Index, CreateTitle(document.Lines[startLine].Text)));
                }
            }
        }
    }

    private static int ResolveBraceFoldStartLine(ImmutableArray<DiffLine> lines, int braceLineIndex, int braceColumn)
    {
        var text = lines[braceLineIndex].Text;
        var prefix = text[..Math.Clamp(braceColumn, 0, text.Length)].Trim();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            return braceLineIndex;
        }

        for (var index = braceLineIndex - 1; index >= 0; index--)
        {
            if (!string.IsNullOrWhiteSpace(lines[index].Text))
            {
                return index;
            }
        }

        return braceLineIndex;
    }

    private static void AddXmlFoldings(DiffDocumentSnapshot document, ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        var stack = new Stack<(string Tag, int LineIndex)>();
        foreach (var line in document.Lines)
        {
            foreach (var tag in ReadXmlTags(line.Text))
            {
                if (tag.IsClosing)
                {
                    while (stack.TryPop(out var start))
                    {
                        if (string.Equals(start.Tag, tag.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (line.Index > start.LineIndex)
                            {
                                builder.Add(new CodeFoldRegion(start.LineIndex, line.Index, $"<{start.Tag}>"));
                            }

                            break;
                        }
                    }
                }
                else if (!tag.IsSelfClosing)
                {
                    stack.Push((tag.Name, line.Index));
                }
            }
        }
    }

    private static void AddIndentationFoldings(DiffDocumentSnapshot document, ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        var lines = document.Lines;
        for (var index = 0; index < lines.Length; index++)
        {
            var current = lines[index];
            if (string.IsNullOrWhiteSpace(current.Text))
            {
                continue;
            }

            var nextIndex = FindNextNonEmptyLine(lines, index + 1);
            if (nextIndex < 0)
            {
                continue;
            }

            var currentIndent = CountIndent(current.Text);
            var nextIndent = CountIndent(lines[nextIndex].Text);
            if (nextIndent <= currentIndent)
            {
                continue;
            }

            var endIndex = nextIndex;
            for (var candidate = nextIndex + 1; candidate < lines.Length; candidate++)
            {
                if (string.IsNullOrWhiteSpace(lines[candidate].Text))
                {
                    continue;
                }

                if (CountIndent(lines[candidate].Text) <= currentIndent)
                {
                    break;
                }

                endIndex = candidate;
            }

            if (endIndex > index)
            {
                builder.Add(new CodeFoldRegion(index, endIndex, CreateTitle(current.Text)));
            }
        }
    }

    private static int FindNextNonEmptyLine(ImmutableArray<DiffLine> lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index].Text))
            {
                return index;
            }
        }

        return -1;
    }

    private static int CountIndent(string text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                count++;
            }
            else if (character == '\t')
            {
                count += 4;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    private static string StripLineComment(string text)
    {
        var index = text.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? text[..index] : text;
    }

    private static IEnumerable<(string Name, bool IsClosing, bool IsSelfClosing)> ReadXmlTags(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('<', index);
            if (open < 0 || open + 1 >= text.Length)
            {
                yield break;
            }

            if (text[open + 1] is '!' or '?')
            {
                index = open + 2;
                continue;
            }

            var close = text.IndexOf('>', open + 1);
            if (close < 0)
            {
                yield break;
            }

            var content = text[(open + 1)..close].Trim();
            var isClosing = content.StartsWith("/", StringComparison.Ordinal);
            if (isClosing)
            {
                content = content[1..].TrimStart();
            }

            var isSelfClosing = content.EndsWith("/", StringComparison.Ordinal);
            if (isSelfClosing)
            {
                content = content[..^1].TrimEnd();
            }

            var nameLength = 0;
            while (nameLength < content.Length && !char.IsWhiteSpace(content[nameLength]) && content[nameLength] != '/')
            {
                nameLength++;
            }

            if (nameLength > 0)
            {
                yield return (content[..nameLength], isClosing, isSelfClosing);
            }

            index = close + 1;
        }
    }

    private static string CreateTitle(string text)
    {
        var title = text.Trim();
        return title.Length <= 80 ? title : $"{title[..77]}...";
    }
}
