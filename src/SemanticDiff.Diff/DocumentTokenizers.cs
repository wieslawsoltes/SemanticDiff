using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SemanticDiff.Core;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace SemanticDiff.Diff;

public sealed class PlainTextDocumentTokenizer : IDocumentTokenizer
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "async", "await", "class", "const", "enum", "event", "interface", "namespace",
        "new", "override", "partial", "private", "protected", "public", "record", "return", "sealed",
        "static", "struct", "using", "var", "void"
    };

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

        if (document.Metadata.Language.Equals("C#", StringComparison.OrdinalIgnoreCase))
        {
            return TokenizeCSharp(line.Text);
        }

        if (document.Metadata.Language.Contains("XAML", StringComparison.OrdinalIgnoreCase) ||
            document.Metadata.Language.Equals("XML", StringComparison.OrdinalIgnoreCase))
        {
            return TokenizeXml(line.Text);
        }

        return ImmutableArray<TokenSpan>.Empty;
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

    private static ImmutableArray<TokenSpan> TokenizeCSharp(string text)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var column = 0;

        while (column < text.Length)
        {
            if (!char.IsLetter(text[column]) && text[column] != '_')
            {
                column++;
                continue;
            }

            var startColumn = column;
            column++;

            while (column < text.Length && (char.IsLetterOrDigit(text[column]) || text[column] == '_'))
            {
                column++;
            }

            var token = text[startColumn..column];
            if (CSharpKeywords.Contains(token))
            {
                builder.Add(new TokenSpan(startColumn, column - startColumn, "keyword"));
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<TokenSpan> TokenizeXml(string text)
    {
        var builder = ImmutableArray.CreateBuilder<TokenSpan>();
        var openIndex = text.IndexOf('<', StringComparison.Ordinal);

        while (openIndex >= 0 && openIndex + 1 < text.Length)
        {
            var startColumn = openIndex + 1;
            if (text[startColumn] == '/')
            {
                startColumn++;
            }

            var column = startColumn;
            while (column < text.Length && (char.IsLetterOrDigit(text[column]) || text[column] is ':' or '_' or '.'))
            {
                column++;
            }

            if (column > startColumn)
            {
                builder.Add(new TokenSpan(startColumn, column - startColumn, "type"));
            }

            openIndex = text.IndexOf('<', openIndex + 1);
        }

        return builder.ToImmutable();
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

        var grammar = GetGrammar(document);
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
                var page = GetOrCreatePage(document, grammar, AlignPageStart(lineIndex), cancellationToken);
                var tokens = page.LineTokens[lineIndex - page.FirstLineIndex];
                builder.Add(document.Lines[lineIndex] with { Tokens = tokens });
            }

            return new ValueTask<ImmutableArray<DiffLine>>(builder.ToImmutable());
        }
    }

    private IGrammar? GetGrammar(DiffDocumentSnapshot document)
    {
        var scopeName = ResolveScopeName(document);
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

    private string? ResolveScopeName(DiffDocumentSnapshot document)
    {
        foreach (var languageId in GetLanguageIds(document))
        {
            var scopeName = TryGetScopeByLanguageId(languageId);
            if (!string.IsNullOrWhiteSpace(scopeName))
            {
                return scopeName;
            }
        }

        var extension = Path.GetExtension(document.Metadata.Path);
        return string.IsNullOrWhiteSpace(extension) ? null : TryGetScopeByExtension(extension);
    }

    private static IEnumerable<string> GetLanguageIds(DiffDocumentSnapshot document)
    {
        var extension = Path.GetExtension(document.Metadata.Path).ToLowerInvariant();
        switch (extension)
        {
            case ".cs":
                yield return "csharp";
                break;
            case ".xaml":
            case ".axaml":
            case ".xml":
                yield return "xml";
                break;
            case ".json":
                yield return "json";
                break;
            case ".md":
                yield return "markdown";
                break;
        }

        var language = document.Metadata.Language.ToLowerInvariant();
        if (language is "c#" or "cs")
        {
            yield return "csharp";
        }
        else if (language.Contains("xaml", StringComparison.Ordinal) || language == "xml")
        {
            yield return "xml";
        }
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
                cachedPage = TokenizePage(document, grammar, currentPageStart, state, cancellationToken);
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
            tokenLines.Add(ConvertTokens(line.Text, result.Tokens));
        }

        return new TokenPage(firstLineIndex, tokenLines.ToImmutable(), state);
    }

    private static ImmutableArray<TokenSpan> ConvertTokens(string text, IReadOnlyList<IToken> tokens)
    {
        if (text.Length == 0 || tokens.Count == 0)
        {
            return ImmutableArray<TokenSpan>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<TokenSpan>(tokens.Count);
        foreach (var token in tokens)
        {
            var styleId = MapScopesToStyle(token.Scopes);
            if (styleId == "text")
            {
                continue;
            }

            var startColumn = Math.Clamp(token.StartIndex, 0, text.Length);
            var length = Math.Clamp(token.Length, 0, text.Length - startColumn);
            if (length > 0)
            {
                builder.Add(new TokenSpan(startColumn, length, styleId));
            }
        }

        return builder.ToImmutable();
    }

    private static string MapScopesToStyle(IReadOnlyList<string> scopes)
    {
        for (var scopeIndex = scopes.Count - 1; scopeIndex >= 0; scopeIndex--)
        {
            var scope = scopes[scopeIndex];
            if (scope.Contains("invalid", StringComparison.Ordinal))
            {
                return "invalid";
            }

            if (scope.Contains("comment", StringComparison.Ordinal))
            {
                return "comment";
            }

            if (scope.Contains("string", StringComparison.Ordinal))
            {
                return "string";
            }

            if (scope.Contains("constant.numeric", StringComparison.Ordinal) || scope.Contains("constant.language", StringComparison.Ordinal))
            {
                return "number";
            }

            if (scope.Contains("keyword", StringComparison.Ordinal) || scope.Contains("storage", StringComparison.Ordinal))
            {
                return "keyword";
            }

            if (scope.Contains("entity.name.function", StringComparison.Ordinal) || scope.Contains("support.function", StringComparison.Ordinal))
            {
                return "function";
            }

            if (scope.Contains("entity.name.type", StringComparison.Ordinal) || scope.Contains("support.type", StringComparison.Ordinal))
            {
                return "type";
            }

            if (scope.Contains("entity.name.tag", StringComparison.Ordinal))
            {
                return "tag";
            }

            if (scope.Contains("entity.other.attribute-name", StringComparison.Ordinal) || scope.Contains("variable.parameter", StringComparison.Ordinal))
            {
                return "property";
            }
        }

        return "text";
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