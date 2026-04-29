using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public enum CodeCompletionItemKind
{
    Text,
    Keyword,
    Type,
    Member,
    Function,
    Property,
    Variable,
    Snippet,
    Attribute,
    Element,
    Symbol
}

public sealed record CodeCompletionItem(
    string DisplayText,
    string InsertionText,
    CodeCompletionItemKind Kind,
    string? Description = null,
    string? FilterText = null,
    string? SortText = null,
    int Priority = 0,
    ImmutableArray<char> CommitCharacters = default)
{
    public string EffectiveFilterText => string.IsNullOrWhiteSpace(FilterText) ? DisplayText : FilterText!;

    public string EffectiveSortText => string.IsNullOrWhiteSpace(SortText) ? DisplayText : SortText!;

    public override string ToString() => string.IsNullOrWhiteSpace(Description)
        ? $"{DisplayText}  {Kind}"
        : $"{DisplayText}  {Kind}  {Description}";
}

public sealed record CodeCompletionRequest(
    DiffDocumentSnapshot Document,
    int LineIndex,
    int Column,
    bool IsExplicit = false,
    int MaxItems = 100,
    string? RepositoryPath = null)
{
    public static CodeCompletionRequest FromText(
        string text,
        string? language,
        string? path,
        int lineIndex,
        int column,
        bool isExplicit = false,
        int maxItems = 100,
        string? repositoryPath = null)
    {
        var documentPath = string.IsNullOrWhiteSpace(path) ? "untitled" : path!;
        var documentLanguage = string.IsNullOrWhiteSpace(language) ? Path.GetExtension(documentPath).TrimStart('.') : language!;
        var metadata = new DiffDocumentMetadata(
            new DiffDocumentId(documentPath),
            documentPath,
            null,
            DiffFileStatus.Modified,
            documentLanguage,
            0,
            0);
        return new CodeCompletionRequest(
            new DiffDocumentFactory().CreateFromText(metadata, text ?? string.Empty),
            lineIndex,
            column,
            isExplicit,
            maxItems,
            repositoryPath);
    }
}

public sealed record CodeCompletionResult(
    ImmutableArray<CodeCompletionItem> Items,
    int ReplacementStartColumn,
    int ReplacementLength,
    string FilterText,
    bool IsIncomplete = false)
{
    public static CodeCompletionResult Empty(int column = 0) => new([], column, 0, string.Empty);
}

public interface ICodeCompletionProvider
{
    ValueTask<CodeCompletionResult> GetCompletionsAsync(
        CodeCompletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CodeCompletionProviderChain : ICodeCompletionProvider
{
    private readonly ImmutableArray<ICodeCompletionProvider> providers;

    public CodeCompletionProviderChain(params ICodeCompletionProvider[] providers)
        : this((IEnumerable<ICodeCompletionProvider>)providers)
    {
    }

    public CodeCompletionProviderChain(IEnumerable<ICodeCompletionProvider> providers)
    {
        this.providers = providers
            .Where(provider => provider is not null)
            .ToImmutableArray();
    }

    public async ValueTask<CodeCompletionResult> GetCompletionsAsync(
        CodeCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        CodeCompletionResult? firstEmptyResult = null;
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await provider.GetCompletionsAsync(request, cancellationToken).ConfigureAwait(false);
                if (!result.Items.IsDefaultOrEmpty)
                {
                    return result;
                }

                firstEmptyResult ??= result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Completion must never break editing; unavailable language services fall through to the next provider.
            }
        }

        return firstEmptyResult ?? CodeCompletionResult.Empty(Math.Max(0, request.Column));
    }
}

public sealed class DocumentCodeCompletionProvider : ICodeCompletionProvider
{
    private const int MaxScannedLines = 5000;
    private const int MaxCandidatePool = 4000;

    private static readonly ImmutableArray<string> CommonXamlElements =
    [
        "Application", "Page", "UserControl", "Window", "Grid", "StackPanel", "Border", "Canvas", "ScrollViewer",
        "TextBlock", "TextBox", "Button", "ToggleButton", "CheckBox", "RadioButton", "ComboBox", "ListView",
        "ItemsControl", "ContentControl", "ContentPresenter", "DataTemplate", "Style", "Setter", "ResourceDictionary",
        "VisualStateManager", "VisualStateGroup", "VisualState", "Storyboard", "SolidColorBrush", "LinearGradientBrush"
    ];

    private static readonly ImmutableArray<string> CommonXmlAttributes =
    [
        "x:Name", "Name", "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "Width", "Height",
        "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin", "Padding", "HorizontalAlignment",
        "VerticalAlignment", "Visibility", "Background", "Foreground", "BorderBrush", "BorderThickness",
        "CornerRadius", "FontSize", "FontWeight", "Text", "Content", "ItemsSource", "SelectedItem", "Command",
        "Style", "Template", "DataContext", "Click", "Tapped", "PointerPressed", "KeyDown"
    ];

    public ValueTask<CodeCompletionResult> GetCompletionsAsync(
        CodeCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Document.Lines.IsDefaultOrEmpty || request.LineIndex < 0 || request.LineIndex >= request.Document.Lines.Length)
        {
            return new ValueTask<CodeCompletionResult>(CodeCompletionResult.Empty(Math.Max(0, request.Column)));
        }

        var descriptor = LanguageServiceRegistry.Identify(request.Document);
        var lineText = request.Document.Lines[request.LineIndex].Text ?? string.Empty;
        var column = Math.Clamp(request.Column, 0, lineText.Length);
        var replacement = FindReplacementRange(lineText, column, descriptor.Definition?.SyntaxKind);
        var filterText = lineText[replacement.Start..column];
        var trigger = GetTriggerContext(lineText, column, descriptor.Definition?.SyntaxKind);
        var candidates = new Dictionary<string, CodeCompletionItem>(StringComparer.OrdinalIgnoreCase);

        AddLanguageKeywords(candidates, descriptor.Definition, descriptor.Id);
        AddDocumentIdentifiers(candidates, request.Document, request.LineIndex, descriptor.Id, cancellationToken);
        AddXmlCandidates(candidates, request.Document, descriptor, trigger, cancellationToken);

        var allowEmptyFilter = request.IsExplicit || trigger is CompletionTriggerContext.XmlElement or CompletionTriggerContext.XmlAttribute or CompletionTriggerContext.MemberAccess;
        var maxItems = Math.Clamp(request.MaxItems, 1, 500);
        var items = candidates.Values
            .Where(item => MatchesFilter(item, filterText, allowEmptyFilter))
            .OrderByDescending(item => StartsWith(item, filterText))
            .ThenByDescending(item => EqualsFilter(item, filterText))
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.EffectiveSortText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToImmutableArray();

        return new ValueTask<CodeCompletionResult>(new CodeCompletionResult(
            items,
            replacement.Start,
            Math.Max(0, replacement.End - replacement.Start),
            filterText,
            candidates.Count > items.Length));
    }

    private static void AddLanguageKeywords(Dictionary<string, CodeCompletionItem> candidates, LanguageDefinition? definition, string languageId)
    {
        if (definition is null || definition.Keywords.Count == 0)
        {
            return;
        }

        foreach (var keyword in definition.Keywords)
        {
            AddCandidate(candidates, new CodeCompletionItem(
                keyword,
                keyword,
                CodeCompletionItemKind.Keyword,
                $"{languageId} keyword",
                Priority: 40));
        }
    }

    private static void AddDocumentIdentifiers(
        Dictionary<string, CodeCompletionItem> candidates,
        DiffDocumentSnapshot document,
        int currentLineIndex,
        string languageId,
        CancellationToken cancellationToken)
    {
        var firstLine = Math.Max(0, currentLineIndex - MaxScannedLines / 2);
        var lastLine = Math.Min(document.Lines.Length - 1, firstLine + MaxScannedLines - 1);
        if (lastLine - firstLine + 1 < Math.Min(document.Lines.Length, MaxScannedLines))
        {
            firstLine = Math.Max(0, lastLine - MaxScannedLines + 1);
        }

        var added = 0;
        for (var lineIndex = firstLine; lineIndex <= lastLine; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = document.Lines[lineIndex];
            if (line.Kind is DiffLineKind.Metadata or DiffLineKind.Imaginary || string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            foreach (var identifier in ExtractIdentifiers(line.Text))
            {
                var kind = ClassifyIdentifier(line.Text, identifier.Start, identifier.End);
                var priority = 20 - Math.Min(15, Math.Abs(lineIndex - currentLineIndex) / 20);
                if (kind is CodeCompletionItemKind.Type or CodeCompletionItemKind.Function)
                {
                    priority += 8;
                }

                AddCandidate(candidates, new CodeCompletionItem(
                    identifier.Text,
                    identifier.Text,
                    kind,
                    $"from {document.Metadata.Path}:{lineIndex + 1}",
                    Priority: priority));

                added++;
                if (added >= MaxCandidatePool)
                {
                    return;
                }
            }
        }
    }

    private static void AddXmlCandidates(
        Dictionary<string, CodeCompletionItem> candidates,
        DiffDocumentSnapshot document,
        LanguageDescriptor descriptor,
        CompletionTriggerContext trigger,
        CancellationToken cancellationToken)
    {
        if (descriptor.Definition?.SyntaxKind != LanguageSyntaxKind.Xml && trigger is not CompletionTriggerContext.XmlElement and not CompletionTriggerContext.XmlAttribute)
        {
            return;
        }

        if (trigger == CompletionTriggerContext.XmlElement)
        {
            foreach (var element in CommonXamlElements)
            {
                AddCandidate(candidates, new CodeCompletionItem(element, element, CodeCompletionItemKind.Element, "common XAML element", Priority: 60));
            }
        }
        else if (trigger == CompletionTriggerContext.XmlAttribute)
        {
            foreach (var attribute in CommonXmlAttributes)
            {
                AddCandidate(candidates, new CodeCompletionItem(attribute, attribute, CodeCompletionItemKind.Attribute, "common XAML attribute", Priority: 60));
            }
        }

        foreach (var line in document.Lines.Take(MaxScannedLines))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trigger == CompletionTriggerContext.XmlElement)
            {
                foreach (var name in ExtractXmlElementNames(line.Text))
                {
                    AddCandidate(candidates, new CodeCompletionItem(name, name, CodeCompletionItemKind.Element, "element in document", Priority: 50));
                }
            }
            else if (trigger == CompletionTriggerContext.XmlAttribute)
            {
                foreach (var name in ExtractXmlAttributeNames(line.Text))
                {
                    AddCandidate(candidates, new CodeCompletionItem(name, name, CodeCompletionItemKind.Attribute, "attribute in document", Priority: 50));
                }
            }
        }
    }

    private static (int Start, int End) FindReplacementRange(string text, int column, LanguageSyntaxKind? syntaxKind)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        column = Math.Clamp(column, 0, text.Length);
        if (syntaxKind == LanguageSyntaxKind.Xml && TryFindXmlReplacementRange(text, column, out var xmlRange))
        {
            return xmlRange;
        }

        if (column > 0 && text[column - 1] == '.')
        {
            return (column, column);
        }

        var start = column;
        while (start > 0 && IsCompletionWordPart(text[start - 1]))
        {
            start--;
        }

        var end = column;
        while (end < text.Length && IsCompletionWordPart(text[end]))
        {
            end++;
        }

        return (start, end);
    }

    private static bool TryFindXmlReplacementRange(string text, int column, out (int Start, int End) range)
    {
        range = default;
        var tagStart = text.LastIndexOf('<', Math.Max(0, column - 1));
        if (tagStart < 0 || text.LastIndexOf('>', Math.Max(0, column - 1)) > tagStart)
        {
            return false;
        }

        var start = column;
        while (start > tagStart + 1 && IsXmlNamePart(text[start - 1]))
        {
            start--;
        }

        if (start > tagStart + 1 && text[start - 1] == '/')
        {
            start++;
        }

        var end = column;
        while (end < text.Length && IsXmlNamePart(text[end]))
        {
            end++;
        }

        range = (start, end);
        return true;
    }

    private static CompletionTriggerContext GetTriggerContext(string text, int column, LanguageSyntaxKind? syntaxKind)
    {
        column = Math.Clamp(column, 0, text.Length);
        if (column > 0 && text[column - 1] == '.')
        {
            return CompletionTriggerContext.MemberAccess;
        }

        if (syntaxKind != LanguageSyntaxKind.Xml)
        {
            return CompletionTriggerContext.Word;
        }

        var tagStart = text.LastIndexOf('<', Math.Max(0, column - 1));
        if (tagStart < 0 || text.LastIndexOf('>', Math.Max(0, column - 1)) > tagStart || IsInsideXmlString(text, tagStart, column))
        {
            return CompletionTriggerContext.Word;
        }

        var afterOpen = tagStart + 1;
        if (afterOpen < text.Length && text[afterOpen] == '/')
        {
            afterOpen++;
        }

        var hasWhitespaceBeforeCaret = false;
        for (var index = afterOpen; index < column; index++)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                hasWhitespaceBeforeCaret = true;
                break;
            }
        }

        return hasWhitespaceBeforeCaret ? CompletionTriggerContext.XmlAttribute : CompletionTriggerContext.XmlElement;
    }

    private static bool IsInsideXmlString(string text, int start, int column)
    {
        var quote = '\0';
        for (var index = start; index < column; index++)
        {
            var character = text[index];
            if (quote == '\0')
            {
                if (character is '"' or '\'')
                {
                    quote = character;
                }
            }
            else if (character == quote)
            {
                quote = '\0';
            }
        }

        return quote != '\0';
    }

    private static IEnumerable<(string Text, int Start, int End)> ExtractIdentifiers(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (!IsIdentifierStart(text[index]))
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < text.Length && IsCompletionWordPart(text[index]))
            {
                index++;
            }

            var token = text[start..index];
            if (token.Length > 1 && !IsAllDigits(token))
            {
                yield return (token, start, index);
            }
        }
    }

    private static IEnumerable<string> ExtractXmlElementNames(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '<' || index + 1 >= text.Length || text[index + 1] is '/' or '!' or '?')
            {
                continue;
            }

            var start = index + 1;
            var end = start;
            while (end < text.Length && IsXmlNamePart(text[end]))
            {
                end++;
            }

            if (end > start)
            {
                yield return text[start..end];
            }
        }
    }

    private static IEnumerable<string> ExtractXmlAttributeNames(string text)
    {
        var tagStart = text.IndexOf('<', StringComparison.Ordinal);
        while (tagStart >= 0 && tagStart < text.Length)
        {
            var tagEnd = text.IndexOf('>', tagStart + 1);
            if (tagEnd < 0)
            {
                tagEnd = text.Length;
            }

            var index = tagStart + 1;
            while (index < tagEnd && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            while (index < tagEnd)
            {
                while (index < tagEnd && !IsXmlNameStart(text[index]))
                {
                    index++;
                }

                var start = index;
                while (index < tagEnd && IsXmlNamePart(text[index]))
                {
                    index++;
                }

                if (index > start)
                {
                    yield return text[start..index];
                }
            }

            tagStart = text.IndexOf('<', Math.Min(tagEnd + 1, text.Length));
        }
    }

    private static CodeCompletionItemKind ClassifyIdentifier(string text, int start, int end)
    {
        if (end < text.Length && text[end..].TrimStart().StartsWith("(", StringComparison.Ordinal))
        {
            return CodeCompletionItemKind.Function;
        }

        if (start > 0 && PreviousNonWhitespaceIs(text, start, '.'))
        {
            return CodeCompletionItemKind.Member;
        }

        var value = text[start..end];
        if (value.Length > 0 && char.IsUpper(value[0]))
        {
            return CodeCompletionItemKind.Type;
        }

        return CodeCompletionItemKind.Symbol;
    }

    private static bool PreviousNonWhitespaceIs(string text, int start, char expected)
    {
        for (var index = start - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            return text[index] == expected;
        }

        return false;
    }

    private static void AddCandidate(Dictionary<string, CodeCompletionItem> candidates, CodeCompletionItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DisplayText) || string.IsNullOrWhiteSpace(item.InsertionText))
        {
            return;
        }

        var key = item.DisplayText;
        if (!candidates.TryGetValue(key, out var existing) || item.Priority > existing.Priority)
        {
            candidates[key] = item;
        }
    }

    private static bool MatchesFilter(CodeCompletionItem item, string filterText, bool allowEmptyFilter)
    {
        if (string.IsNullOrEmpty(filterText))
        {
            return allowEmptyFilter;
        }

        var filter = item.EffectiveFilterText;
        return filter.StartsWith(filterText, StringComparison.OrdinalIgnoreCase) ||
            filter.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
            item.DisplayText.Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWith(CodeCompletionItem item, string filterText) =>
        !string.IsNullOrEmpty(filterText) &&
        (item.EffectiveFilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase) ||
            item.DisplayText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase));

    private static bool EqualsFilter(CodeCompletionItem item, string filterText) =>
        !string.IsNullOrEmpty(filterText) &&
        (string.Equals(item.EffectiveFilterText, filterText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.DisplayText, filterText, StringComparison.OrdinalIgnoreCase));

    private static bool IsCompletionWordPart(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '@' or '$';

    private static bool IsIdentifierStart(char character) =>
        char.IsLetter(character) || character is '_' or '@' or '$';

    private static bool IsXmlNameStart(char character) =>
        char.IsLetter(character) || character is '_' or ':';

    private static bool IsXmlNamePart(char character) =>
        IsXmlNameStart(character) || char.IsDigit(character) || character is '-' or '.';

    private static bool IsAllDigits(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private enum CompletionTriggerContext
    {
        Word,
        MemberAccess,
        XmlElement,
        XmlAttribute
    }
}
