using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class CodeCompletionProviderTests
{
    private readonly DocumentCodeCompletionProvider provider = new();

    [Fact]
    public async Task GetCompletionsAsync_ReturnsLanguageKeywordsForPrefix()
    {
        var document = CreateDocument("Sample.cs", "C#", "publ");
        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 0, 4));

        Assert.Contains(result.Items, item => item.DisplayText == "public" && item.Kind == CodeCompletionItemKind.Keyword);
        Assert.Equal(0, result.ReplacementStartColumn);
        Assert.Equal(4, result.ReplacementLength);
        Assert.Equal("publ", result.FilterText);
    }

    [Fact]
    public async Task GetCompletionsAsync_RanksNearbyDocumentSymbols()
    {
        var document = CreateDocument(
            "Sample.cs",
            "C#",
            "class NearbyWidget { }\nclass DistantWidget { }\nNear");

        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 2, 4));

        var nearby = Assert.Single(result.Items.Where(item => item.DisplayText == "NearbyWidget"));
        Assert.Equal(CodeCompletionItemKind.Type, nearby.Kind);
        Assert.Contains(result.Items.Take(3), item => item.DisplayText == "NearbyWidget");
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsMemberSuggestionsAfterDot()
    {
        var document = CreateDocument("Sample.cs", "C#", "viewModel.RefreshState();\nviewModel.");
        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 1, "viewModel.".Length));

        Assert.Contains(result.Items, item => item.DisplayText == "RefreshState");
        Assert.Equal("", result.FilterText);
        Assert.Equal("viewModel.".Length, result.ReplacementStartColumn);
    }

    [Fact]
    public async Task GetCompletionsAsync_DoesNotReturnImplicitWhitespaceSuggestionsForCode()
    {
        var document = CreateDocument("Sample.cs", "C#", "public ");
        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 0, "public ".Length));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsXamlElementSuggestionsAfterOpenBracket()
    {
        var document = CreateDocument("View.xaml", "XAML", "<G");
        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 0, 2));

        Assert.Contains(result.Items, item => item.DisplayText == "Grid" && item.Kind == CodeCompletionItemKind.Element);
        Assert.Equal(1, result.ReplacementStartColumn);
        Assert.Equal(1, result.ReplacementLength);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsXamlAttributeSuggestionsAfterTagWhitespace()
    {
        var document = CreateDocument("View.xaml", "XAML", "<Grid ");
        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 0, "<Grid ".Length));

        Assert.Contains(result.Items, item => item.DisplayText == "Grid.Row" && item.Kind == CodeCompletionItemKind.Attribute);
        Assert.Equal("", result.FilterText);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsXamlAttributeSuggestionsInsideTag()
    {
        var document = CreateDocument("View.xaml", "XAML", "<Grid Grid.R");
        var result = await provider.GetCompletionsAsync(new CodeCompletionRequest(document, 0, "<Grid Grid.R".Length));

        Assert.Contains(result.Items, item => item.DisplayText == "Grid.Row" && item.Kind == CodeCompletionItemKind.Attribute);
        Assert.Equal("Grid.R", result.FilterText);
    }

    [Fact]
    public async Task ProviderChain_FallsBackWhenFirstProviderReturnsNoItems()
    {
        var document = CreateDocument("Sample.cs", "C#", "publ");
        var chain = new CodeCompletionProviderChain(
            new EmptyCompletionProvider(),
            provider);

        var result = await chain.GetCompletionsAsync(new CodeCompletionRequest(document, 0, 4));

        Assert.Contains(result.Items, item => item.DisplayText == "public");
    }

    [Fact]
    public async Task ProviderChain_FallsBackWhenFirstProviderThrows()
    {
        var document = CreateDocument("Sample.cs", "C#", "publ");
        var chain = new CodeCompletionProviderChain(
            new ThrowingCompletionProvider(),
            provider);

        var result = await chain.GetCompletionsAsync(new CodeCompletionRequest(document, 0, 4));

        Assert.Contains(result.Items, item => item.DisplayText == "public");
    }

    private static DiffDocumentSnapshot CreateDocument(string path, string language, string text)
    {
        var metadata = new DiffDocumentMetadata(new DiffDocumentId(path), path, null, DiffFileStatus.Modified, language, 0, 0);
        return new DiffDocumentFactory().CreateFromText(metadata, text);
    }

    private sealed class EmptyCompletionProvider : ICodeCompletionProvider
    {
        public ValueTask<CodeCompletionResult> GetCompletionsAsync(
            CodeCompletionRequest request,
            CancellationToken cancellationToken = default) =>
            new(CodeCompletionResult.Empty(request.Column));
    }

    private sealed class ThrowingCompletionProvider : ICodeCompletionProvider
    {
        public ValueTask<CodeCompletionResult> GetCompletionsAsync(
            CodeCompletionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("language service unavailable");
    }
}
