using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public sealed class CodeFoldingService
{
    private const string RegionDirective = "#region";
    private const string EndRegionDirective = "#endregion";

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

        return NormalizeFoldRegions(builder);
    }

    private static void AddRegionDirectiveFoldings(DiffDocumentSnapshot document, ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        var stack = new Stack<(int LineIndex, string Title)>();
        foreach (var line in document.Lines)
        {
            var trimmed = line.Text.AsSpan().Trim();
            if (trimmed.StartsWith(RegionDirective, StringComparison.Ordinal))
            {
                var title = trimmed.Length > RegionDirective.Length
                    ? trimmed[RegionDirective.Length..].Trim()
                    : ReadOnlySpan<char>.Empty;
                stack.Push((line.Index, title.IsEmpty ? RegionDirective : title.ToString()));
            }
            else if (trimmed.StartsWith(EndRegionDirective, StringComparison.Ordinal) && stack.TryPop(out var start))
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
            var text = line.Text;
            var scanLength = FindLineCommentStart(text);
            if (scanLength < 0)
            {
                scanLength = text.Length;
            }

            for (var index = 0; index < scanLength; index++)
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
        var prefixEnd = Math.Clamp(braceColumn, 0, text.Length);
        if (ContainsNonWhiteSpace(text.AsSpan(0, prefixEnd)))
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

    private static bool ContainsNonWhiteSpace(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindLineCommentStart(string text) =>
        text.IndexOf("//", StringComparison.Ordinal);

    private static ImmutableArray<CodeFoldRegion> NormalizeFoldRegions(ImmutableArray<CodeFoldRegion>.Builder builder)
    {
        if (builder.Count == 0)
        {
            return [];
        }

        var bestByStart = new Dictionary<int, CodeFoldRegion>(builder.Count);
        for (var index = 0; index < builder.Count; index++)
        {
            var region = builder[index];
            if (region.EndLineIndex <= region.StartLineIndex)
            {
                continue;
            }

            if (!bestByStart.TryGetValue(region.StartLineIndex, out var existing) ||
                region.EndLineIndex > existing.EndLineIndex)
            {
                bestByStart[region.StartLineIndex] = region;
            }
        }

        if (bestByStart.Count == 0)
        {
            return [];
        }

        var regions = new CodeFoldRegion[bestByStart.Count];
        var regionIndex = 0;
        foreach (var region in bestByStart.Values)
        {
            regions[regionIndex++] = region;
        }

        Array.Sort(regions, static (left, right) =>
        {
            var startComparison = left.StartLineIndex.CompareTo(right.StartLineIndex);
            return startComparison != 0
                ? startComparison
                : right.EndLineIndex.CompareTo(left.EndLineIndex);
        });

        return ImmutableArray.Create(regions);
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

            var contentStart = open + 1;
            var contentEnd = close;
            TrimXmlContent(text, ref contentStart, ref contentEnd);
            var isClosing = contentStart < contentEnd && text[contentStart] == '/';
            if (isClosing)
            {
                contentStart++;
                TrimXmlContentStart(text, ref contentStart, contentEnd);
            }

            var isSelfClosing = contentStart < contentEnd && text[contentEnd - 1] == '/';
            if (isSelfClosing)
            {
                contentEnd--;
                TrimXmlContentEnd(text, contentStart, ref contentEnd);
            }

            var nameEnd = contentStart;
            while (nameEnd < contentEnd && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != '/')
            {
                nameEnd++;
            }

            if (nameEnd > contentStart)
            {
                yield return (text[contentStart..nameEnd], isClosing, isSelfClosing);
            }

            index = close + 1;
        }
    }

    private static void TrimXmlContent(string text, ref int start, ref int end)
    {
        TrimXmlContentStart(text, ref start, end);
        TrimXmlContentEnd(text, start, ref end);
    }

    private static void TrimXmlContentStart(string text, ref int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
    }

    private static void TrimXmlContentEnd(string text, int start, ref int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
    }

    private static string CreateTitle(string text)
    {
        var title = text.AsSpan().Trim();
        return title.Length <= 80
            ? title.ToString()
            : string.Concat(title[..77], "...");
    }
}
