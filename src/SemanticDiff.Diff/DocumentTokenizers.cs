using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SemanticDiff.Core;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace SemanticDiff.Diff;

public sealed class PlainTextDocumentTokenizer : IDocumentTokenizer
{
    public string Id => "plain-text";

    public ValueTask<ImmutableArray<TokenSpan>> TokenizeLineAsync(
        DiffDocumentSnapshot document,
        DiffLine line,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<ImmutableArray<TokenSpan>>(TokenizeLine(document, line));
    }

    private static ImmutableArray<TokenSpan> TokenizeLine(DiffDocumentSnapshot document, DiffLine line)
    {
        if (line.Kind is DiffLineKind.Metadata or DiffLineKind.Imaginary || line.Text.Length == 0)
        {
            return ImmutableArray<TokenSpan>.Empty;
        }

        var descriptor = LanguageServiceRegistry.Identify(document);
        return descriptor.Definition?.SyntaxKind switch
        {
            LanguageSyntaxKind.Xml => TokenizeXml(line.Text, descriptor.Id),
            LanguageSyntaxKind.Json => TokenizeJson(line.Text, descriptor.Id),
            LanguageSyntaxKind.Yaml => TokenizeYamlLike(line.Text, descriptor.Id, descriptor.Definition),
            LanguageSyntaxKind.Css => TokenizeCss(line.Text, descriptor.Id, descriptor.Definition),
            LanguageSyntaxKind.Markdown => TokenizeMarkdown(line.Text, descriptor.Id),
            _ => TokenizeCode(line.Text, descriptor.Id, descriptor.Definition)
        };
    }

    public ValueTask<ImmutableArray<DiffLine>> TokenizePageAsync(
        DiffDocumentSnapshot document,
        int firstLineIndex,
        int lineCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var firstLine = Math.Clamp(firstLineIndex, 0, document.LineCount);
        var lastLine = Math.Clamp(firstLine + Math.Max(0, lineCount), 0, document.LineCount);
        var builder = ImmutableArray.CreateBuilder<DiffLine>(lastLine - firstLine);

        for (var lineIndex = firstLine; lineIndex < lastLine; lineIndex++)
        {
            var line = document.Lines[lineIndex];
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = TokenizeLine(document, line);
            builder.Add(line with { Tokens = tokens });
        }

        return new ValueTask<ImmutableArray<DiffLine>>(builder.ToImmutable());
    }

    private static ImmutableArray<TokenSpan> TokenizeCode(string text, string languageId, LanguageDefinition? definition)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var column = 0;
        var keywords = definition?.Keywords ?? ImmutableHashSet<string>.Empty;
        var lineCommentPrefixes = definition?.LineCommentPrefixes ?? ImmutableArray.Create("//", "#");
        var blockComments = definition?.BlockComments ?? ImmutableArray.Create(("/*", "*/"));
        var stringDelimiters = definition?.StringDelimiters ?? ImmutableArray.Create("\"", "'", "`");

        while (column < text.Length)
        {
            var lineCommentPrefix = FindPrefix(text, column, lineCommentPrefixes);
            if (lineCommentPrefix is not null)
            {
                AddToken(builder, column, text.Length - column, "comment", "comment", languageId);
                break;
            }

            var blockComment = FindBlockComment(text, column, blockComments);
            if (blockComment is not null)
            {
                var end = text.IndexOf(blockComment.Value.Close, column + blockComment.Value.Open.Length, StringComparison.Ordinal);
                var length = end < 0 ? text.Length - column : end + blockComment.Value.Close.Length - column;
                AddToken(builder, column, length, "comment", "comment", languageId);
                column += Math.Max(1, length);
                continue;
            }

            var stringDelimiter = FindPrefix(text, column, stringDelimiters);
            if (stringDelimiter is not null)
            {
                var length = ReadStringLength(text, column, stringDelimiter);
                AddToken(builder, column, length, "string", "string", languageId);
                column += length;
                continue;
            }

            if (IsNumberStart(text, column))
            {
                var length = ReadNumberLength(text, column);
                AddToken(builder, column, length, "number", "number", languageId);
                column += length;
                continue;
            }

            if (IsIdentifierStart(text[column]))
            {
                var startColumn = column;
                column++;

                while (column < text.Length && IsIdentifierPart(text[column]))
                {
                    column++;
                }

                var token = text[startColumn..column];
                var normalizedToken = languageId == "sql" ? token.ToUpperInvariant() : token;
                if (keywords.Contains(normalizedToken))
                {
                    AddToken(builder, startColumn, column - startColumn, "keyword", "keyword", languageId);
                }
                else if (NextNonWhitespaceIs(text, column, '('))
                {
                    AddToken(builder, startColumn, column - startColumn, "function", "function", languageId);
                }
                else if (PreviousNonWhitespaceIs(text, startColumn, '.'))
                {
                    AddToken(builder, startColumn, column - startColumn, "property", "property", languageId);
                }

                continue;
            }

            if (IsOperator(text[column]))
            {
                var startColumn = column;
                column++;
                while (column < text.Length && IsOperator(text[column]))
                {
                    column++;
                }

                AddToken(builder, startColumn, column - startColumn, "operator", "operator", languageId);
                continue;
            }

            column++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<TokenSpan> TokenizeXml(string text, string languageId)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var column = 0;

        while (column < text.Length)
        {
            if (text.AsSpan(column).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = text.IndexOf("-->", column + 4, StringComparison.Ordinal);
                var length = end < 0 ? text.Length - column : end + 3 - column;
                AddToken(builder, column, length, "comment", "comment", languageId);
                column += Math.Max(1, length);
                continue;
            }

            if (text[column] != '<')
            {
                column++;
                continue;
            }

            AddToken(builder, column, 1, "punctuation", "operator", languageId);
            column++;
            if (column < text.Length && text[column] == '/')
            {
                AddToken(builder, column, 1, "punctuation", "operator", languageId);
                column++;
            }

            column = SkipWhitespace(text, column);
            var tagStart = column;
            while (column < text.Length && IsXmlNameCharacter(text[column]))
            {
                column++;
            }

            if (column > tagStart)
            {
                AddToken(builder, tagStart, column - tagStart, "tag", "type", languageId);
            }

            while (column < text.Length && text[column] != '>')
            {
                if (text.AsSpan(column).StartsWith("/>", StringComparison.Ordinal))
                {
                    AddToken(builder, column, 2, "punctuation", "operator", languageId);
                    column += 2;
                    break;
                }

                if (text[column] is '"' or '\'')
                {
                    var length = ReadStringLength(text, column, text[column].ToString());
                    AddToken(builder, column, length, "string", "string", languageId);
                    column += length;
                    continue;
                }

                if (text[column] == '=')
                {
                    AddToken(builder, column, 1, "operator", "operator", languageId);
                    column++;
                    continue;
                }

                if (IsXmlNameStartCharacter(text[column]))
                {
                    var attributeStart = column;
                    column++;
                    while (column < text.Length && IsXmlNameCharacter(text[column]))
                    {
                        column++;
                    }

                    AddToken(builder, attributeStart, column - attributeStart, "property", "property", languageId);
                    continue;
                }

                column++;
            }

            if (column < text.Length && text[column] == '>')
            {
                AddToken(builder, column, 1, "punctuation", "operator", languageId);
                column++;
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<TokenSpan> TokenizeJson(string text, string languageId)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var column = 0;

        while (column < text.Length)
        {
            if (text.AsSpan(column).StartsWith("//", StringComparison.Ordinal))
            {
                AddToken(builder, column, text.Length - column, "comment", "comment", languageId);
                break;
            }

            if (text.AsSpan(column).StartsWith("/*", StringComparison.Ordinal))
            {
                var end = text.IndexOf("*/", column + 2, StringComparison.Ordinal);
                var length = end < 0 ? text.Length - column : end + 2 - column;
                AddToken(builder, column, length, "comment", "comment", languageId);
                column += Math.Max(1, length);
                continue;
            }

            if (text[column] == '"')
            {
                var length = ReadStringLength(text, column, "\"");
                var style = NextNonWhitespaceIs(text, column + length, ':') ? "property" : "string";
                var tokenType = style == "property" ? "property" : "string";
                AddToken(builder, column, length, style, tokenType, languageId);
                column += length;
                continue;
            }

            if (IsNumberStart(text, column))
            {
                var length = ReadNumberLength(text, column);
                AddToken(builder, column, length, "number", "number", languageId);
                column += length;
                continue;
            }

            if (IsIdentifierStart(text[column]))
            {
                var startColumn = column;
                column++;
                while (column < text.Length && IsIdentifierPart(text[column]))
                {
                    column++;
                }

                var token = text[startColumn..column];
                if (token is "true" or "false" or "null")
                {
                    AddToken(builder, startColumn, column - startColumn, "keyword", "keyword", languageId);
                }

                continue;
            }

            if (IsJsonPunctuation(text[column]))
            {
                AddToken(builder, column, 1, "punctuation", "operator", languageId);
            }

            column++;
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<TokenSpan> TokenizeYamlLike(string text, string languageId, LanguageDefinition definition)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var trimmedStart = text.Length - text.TrimStart().Length;
        if (trimmedStart < text.Length && text[trimmedStart] is '[' or ']')
        {
            AddToken(builder, trimmedStart, 1, "punctuation", "operator", languageId);
        }

        var commentIndex = FindUnquotedComment(text, '#');
        if (commentIndex >= 0)
        {
            AddToken(builder, commentIndex, text.Length - commentIndex, "comment", "comment", languageId);
        }

        var limit = commentIndex >= 0 ? commentIndex : text.Length;
        var colonIndex = text.IndexOf(':', 0, limit);
        if (colonIndex > 0)
        {
            var keyStart = SkipWhitespace(text, 0);
            if (keyStart < colonIndex)
            {
                AddToken(builder, keyStart, colonIndex - keyStart, "property", "property", languageId);
            }

            AddToken(builder, colonIndex, 1, "operator", "operator", languageId);
        }

        AddStringAndNumberTokens(builder, text, languageId, definition, limit);
        return builder.ToImmutable();
    }

    private static ImmutableArray<TokenSpan> TokenizeCss(string text, string languageId, LanguageDefinition definition)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var commentIndex = text.IndexOf("/*", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            var end = text.IndexOf("*/", commentIndex + 2, StringComparison.Ordinal);
            var length = end < 0 ? text.Length - commentIndex : end + 2 - commentIndex;
            AddToken(builder, commentIndex, length, "comment", "comment", languageId);
        }

        var limit = commentIndex >= 0 ? commentIndex : text.Length;
        var colonIndex = text.IndexOf(':', 0, limit);
        if (colonIndex > 0)
        {
            var propertyStart = SkipWhitespace(text, 0);
            if (propertyStart < colonIndex && text[propertyStart] != '@')
            {
                AddToken(builder, propertyStart, colonIndex - propertyStart, "property", "property", languageId);
            }
        }

        AddStringAndNumberTokens(builder, text, languageId, definition, limit);
        return builder.ToImmutable();
    }

    private static ImmutableArray<TokenSpan> TokenizeMarkdown(string text, string languageId)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var trimmedStart = text.Length - text.TrimStart().Length;
        if (trimmedStart < text.Length && text[trimmedStart] == '#')
        {
            var headingLength = 0;
            while (trimmedStart + headingLength < text.Length && text[trimmedStart + headingLength] == '#')
            {
                headingLength++;
            }

            AddToken(builder, trimmedStart, headingLength, "keyword", "keyword", languageId);
        }

        if (text.AsSpan(trimmedStart).StartsWith("```", StringComparison.Ordinal))
        {
            AddToken(builder, trimmedStart, text.Length - trimmedStart, "tag", "label", languageId);
        }

        var inlineCodeStart = text.IndexOf('`', StringComparison.Ordinal);
        if (inlineCodeStart >= 0)
        {
            var inlineCodeEnd = text.IndexOf('`', inlineCodeStart + 1);
            if (inlineCodeEnd > inlineCodeStart)
            {
                AddToken(builder, inlineCodeStart, inlineCodeEnd - inlineCodeStart + 1, "string", "string", languageId);
            }
        }

        return builder.ToImmutable();
    }

    private static void AddStringAndNumberTokens(
        ImmutableArray<TokenSpan>.Builder builder,
        string text,
        string languageId,
        LanguageDefinition definition,
        int limit)
    {
        var column = 0;
        while (column < limit)
        {
            var delimiter = FindPrefix(text, column, definition.StringDelimiters);
            if (delimiter is not null)
            {
                var length = Math.Min(ReadStringLength(text, column, delimiter), limit - column);
                AddToken(builder, column, length, "string", "string", languageId);
                column += length;
                continue;
            }

            if (IsNumberStart(text, column))
            {
                var length = Math.Min(ReadNumberLength(text, column), limit - column);
                AddToken(builder, column, length, "number", "number", languageId);
                column += length;
                continue;
            }

            column++;
        }
    }

    private static void AddToken(
        ImmutableArray<TokenSpan>.Builder builder,
        int startColumn,
        int length,
        string styleId,
        string tokenType,
        string languageId)
    {
        if (length <= 0)
        {
            return;
        }

        builder.Add(TokenClassification.Create(startColumn, length, styleId, tokenType, languageId, TokenClassification.FallbackSource));
    }

    private static string? FindPrefix(string text, int column, ImmutableArray<string> prefixes)
    {
        foreach (var prefix in prefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (prefix.Length > 0 && text.AsSpan(column).StartsWith(prefix, StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return null;
    }

    private static (string Open, string Close)? FindBlockComment(string text, int column, ImmutableArray<(string Open, string Close)> blockComments)
    {
        foreach (var blockComment in blockComments.OrderByDescending(comment => comment.Open.Length))
        {
            if (blockComment.Open.Length > 0 && text.AsSpan(column).StartsWith(blockComment.Open, StringComparison.Ordinal))
            {
                return blockComment;
            }
        }

        return null;
    }

    private static int ReadStringLength(string text, int startColumn, string delimiter)
    {
        var contentStart = startColumn + delimiter.Length;
        var column = contentStart;
        while (column < text.Length)
        {
            if (text.AsSpan(column).StartsWith(delimiter, StringComparison.Ordinal))
            {
                return column + delimiter.Length - startColumn;
            }

            column += text[column] == '\\' && column + 1 < text.Length ? 2 : 1;
        }

        return text.Length - startColumn;
    }

    private static int ReadNumberLength(string text, int startColumn)
    {
        var column = startColumn;
        if (text[column] is '+' or '-')
        {
            column++;
        }

        while (column < text.Length && (char.IsLetterOrDigit(text[column]) || text[column] is '.' or '_' or 'x' or 'X'))
        {
            column++;
        }

        return Math.Max(1, column - startColumn);
    }

    private static int SkipWhitespace(string text, int column)
    {
        while (column < text.Length && char.IsWhiteSpace(text[column]))
        {
            column++;
        }

        return column;
    }

    private static bool NextNonWhitespaceIs(string text, int column, char expected)
    {
        column = SkipWhitespace(text, column);
        return column < text.Length && text[column] == expected;
    }

    private static bool PreviousNonWhitespaceIs(string text, int column, char expected)
    {
        column--;
        while (column >= 0 && char.IsWhiteSpace(text[column]))
        {
            column--;
        }

        return column >= 0 && text[column] == expected;
    }

    private static bool IsNumberStart(string text, int column)
    {
        return char.IsDigit(text[column]) ||
            text[column] is '+' or '-' && column + 1 < text.Length && char.IsDigit(text[column + 1]);
    }

    private static bool IsIdentifierStart(char character) => char.IsLetter(character) || character is '_' or '$' or '@';

    private static bool IsIdentifierPart(char character) => char.IsLetterOrDigit(character) || character is '_' or '$' or '-' or '@';

    private static bool IsOperator(char character) => character is
        '+' or '-' or '*' or '/' or '%' or '=' or '!' or '<' or '>' or '&' or '|' or '^' or '~' or '?' or ':' or '.';

    private static bool IsJsonPunctuation(char character) => character is '{' or '}' or '[' or ']' or ',' or ':';

    private static bool IsXmlNameStartCharacter(char character) => char.IsLetter(character) || character is '_' or ':';

    private static bool IsXmlNameCharacter(char character) => IsXmlNameStartCharacter(character) || char.IsDigit(character) || character is '-' or '.';

    private static int FindUnquotedComment(string text, char commentMarker)
    {
        var quote = '\0';
        for (var column = 0; column < text.Length; column++)
        {
            if (quote != '\0')
            {
                if (text[column] == '\\' && column + 1 < text.Length)
                {
                    column++;
                    continue;
                }

                if (text[column] == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (text[column] is '"' or '\'')
            {
                quote = text[column];
                continue;
            }

            if (text[column] == commentMarker)
            {
                return column;
            }
        }

        return -1;
    }
}

public sealed class TextMateDocumentTokenizer : IDocumentTokenizer
{
    private const int DefaultPageSize = 128;
    private static readonly TimeSpan TokenizeLineTimeout = TimeSpan.FromMilliseconds(50);

    private readonly object gate = new();
    private readonly PlainTextDocumentTokenizer fallback = new();
    private readonly RegistryOptions registryOptions;
    private readonly Registry registry;
    private readonly Dictionary<string, IGrammar?> grammars = new(StringComparer.Ordinal);
    private readonly Dictionary<DiffDocumentSnapshot, DocumentTokenCache> documentCaches = new(DocumentReferenceComparer.Instance);
    private readonly int pageSize;

    public TextMateDocumentTokenizer()
        : this(DefaultPageSize, ThemeName.DarkPlus)
    {
    }

    public TextMateDocumentTokenizer(int pageSize, ThemeName themeName = ThemeName.DarkPlus)
    {
        this.pageSize = Math.Max(16, pageSize);
        registryOptions = new RegistryOptions(themeName);
        registry = new Registry(registryOptions);
    }

    public string Id => "textmate-sharp";

    public async ValueTask<ImmutableArray<TokenSpan>> TokenizeLineAsync(
        DiffDocumentSnapshot document,
        DiffLine line,
        CancellationToken cancellationToken)
    {
        var lines = await TokenizePageAsync(document, line.Index, 1, cancellationToken).ConfigureAwait(false);
        return lines.Length > 0 ? lines[0].Tokens : ImmutableArray<TokenSpan>.Empty;
    }

    public ValueTask<ImmutableArray<DiffLine>> TokenizePageAsync(
        DiffDocumentSnapshot document,
        int firstLineIndex,
        int lineCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var language = LanguageServiceRegistry.Identify(document);
        var grammar = GetGrammar(document, language);
        if (grammar is null)
        {
            return fallback.TokenizePageAsync(document, firstLineIndex, lineCount, cancellationToken);
        }

        var firstLine = Math.Clamp(firstLineIndex, 0, document.LineCount);
        var lastLine = Math.Clamp(firstLine + Math.Max(0, lineCount), 0, document.LineCount);
        if (firstLine >= lastLine)
        {
            return new ValueTask<ImmutableArray<DiffLine>>(ImmutableArray<DiffLine>.Empty);
        }

        lock (gate)
        {
            var builder = ImmutableArray.CreateBuilder<DiffLine>(lastLine - firstLine);
            for (var lineIndex = firstLine; lineIndex < lastLine; lineIndex++)
            {
                var page = GetOrCreatePage(document, grammar, language.Id, AlignPageStart(lineIndex), cancellationToken);
                var tokens = page.LineTokens[lineIndex - page.FirstLineIndex];
                builder.Add(document.Lines[lineIndex] with { Tokens = tokens });
            }

            return new ValueTask<ImmutableArray<DiffLine>>(builder.ToImmutable());
        }
    }

    private IGrammar? GetGrammar(DiffDocumentSnapshot document, LanguageDescriptor language)
    {
        var scopeName = ResolveScopeName(document, language);
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            return null;
        }

        lock (gate)
        {
            if (grammars.TryGetValue(scopeName, out var cachedGrammar))
            {
                return cachedGrammar;
            }

            try
            {
                var grammar = registry.LoadGrammar(scopeName);
                grammars[scopeName] = grammar;
                return grammar;
            }
            catch
            {
                grammars[scopeName] = null;
                return null;
            }
        }
    }

    private string? ResolveScopeName(DiffDocumentSnapshot document, LanguageDescriptor language)
    {
        foreach (var languageId in LanguageServiceRegistry.GetLanguageIdCandidates(document))
        {
            var scopeName = TryGetScopeByLanguageId(languageId);
            if (!string.IsNullOrWhiteSpace(scopeName))
            {
                return scopeName;
            }
        }

        return string.IsNullOrWhiteSpace(language.Extension) ? null : TryGetScopeByExtension(language.Extension);
    }

    private string? TryGetScopeByLanguageId(string languageId)
    {
        try
        {
            return registryOptions.GetScopeByLanguageId(languageId);
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetScopeByExtension(string extension)
    {
        try
        {
            return registryOptions.GetScopeByExtension(extension);
        }
        catch
        {
            return null;
        }
    }

    private int AlignPageStart(int lineIndex) => lineIndex / pageSize * pageSize;

    private TokenPage GetOrCreatePage(
        DiffDocumentSnapshot document,
        IGrammar grammar,
        string languageId,
        int pageStart,
        CancellationToken cancellationToken)
    {
        if (!documentCaches.TryGetValue(document, out var documentCache))
        {
            documentCache = new DocumentTokenCache();
            documentCaches.Add(document, documentCache);
        }

        if (documentCache.Pages.TryGetValue(pageStart, out var cachedPage))
        {
            return cachedPage;
        }

        IStateStack? state = null;
        var currentPageStart = 0;
        foreach (var candidate in documentCache.Pages.Values.Where(page => page.FirstLineIndex < pageStart).OrderByDescending(page => page.FirstLineIndex))
        {
            state = candidate.EndState;
            currentPageStart = candidate.FirstLineIndex + candidate.LineTokens.Length;
            break;
        }

        while (currentPageStart <= pageStart)
        {
            if (!documentCache.Pages.TryGetValue(currentPageStart, out cachedPage))
            {
                cachedPage = TokenizePage(document, grammar, languageId, currentPageStart, state, cancellationToken);
                documentCache.Pages.Add(currentPageStart, cachedPage);
            }

            state = cachedPage.EndState;
            if (currentPageStart == pageStart)
            {
                return cachedPage;
            }

            currentPageStart += cachedPage.LineTokens.Length;
        }

        return documentCache.Pages[pageStart];
    }

    private TokenPage TokenizePage(
        DiffDocumentSnapshot document,
        IGrammar grammar,
        string languageId,
        int firstLineIndex,
        IStateStack? initialState,
        CancellationToken cancellationToken)
    {
        var lineCount = Math.Min(pageSize, document.LineCount - firstLineIndex);
        var tokenLines = ImmutableArray.CreateBuilder<ImmutableArray<TokenSpan>>(lineCount);
        var state = initialState;

        for (var lineOffset = 0; lineOffset < lineCount; lineOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = document.Lines[firstLineIndex + lineOffset];
            if (line.Kind == DiffLineKind.Metadata || line.Kind == DiffLineKind.Imaginary)
            {
                tokenLines.Add(ImmutableArray<TokenSpan>.Empty);
                continue;
            }

            var result = grammar.TokenizeLine(new LineText(line.Text), state, TokenizeLineTimeout);
            state = result.RuleStack;
            tokenLines.Add(ConvertTokens(line.Text, result.Tokens, languageId));
        }

        return new TokenPage(firstLineIndex, tokenLines.ToImmutable(), state);
    }

    private static ImmutableArray<TokenSpan> ConvertTokens(string text, IReadOnlyList<IToken> tokens, string languageId)
    {
        if (text.Length == 0 || tokens.Count == 0)
        {
            return ImmutableArray<TokenSpan>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<TokenSpan>(tokens.Count);
        foreach (var token in tokens)
        {
            var (styleId, tokenType, modifiers) = TokenClassification.FromScopes(token.Scopes);
            if (styleId == "text")
            {
                continue;
            }

            var startColumn = Math.Clamp(token.StartIndex, 0, text.Length);
            var length = Math.Clamp(token.Length, 0, text.Length - startColumn);
            if (length > 0)
            {
                builder.Add(TokenClassification.Create(
                    startColumn,
                    length,
                    styleId,
                    tokenType,
                    languageId,
                    TokenClassification.TextMateSource,
                    modifiers,
                    token.Scopes.ToImmutableArray()));
            }
        }

        return builder.ToImmutable();
    }

    private sealed class DocumentTokenCache
    {
        public Dictionary<int, TokenPage> Pages { get; } = [];
    }

    private sealed record TokenPage(int FirstLineIndex, ImmutableArray<ImmutableArray<TokenSpan>> LineTokens, IStateStack? EndState);

    private sealed class DocumentReferenceComparer : IEqualityComparer<DiffDocumentSnapshot>
    {
        public static DocumentReferenceComparer Instance { get; } = new();

        public bool Equals(DiffDocumentSnapshot? x, DiffDocumentSnapshot? y) => ReferenceEquals(x, y);

        public int GetHashCode(DiffDocumentSnapshot obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
