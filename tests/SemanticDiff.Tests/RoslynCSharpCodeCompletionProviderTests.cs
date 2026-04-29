using SemanticDiff.Diff;
using SemanticDiff.Semantics.Roslyn;

namespace SemanticDiff.Tests;

public sealed class RoslynCSharpCodeCompletionProviderTests
{
    [Fact]
    public async Task GetCompletionsAsync_ReturnsCSharpKeywordSuggestions()
    {
        using var provider = new RoslynCSharpCodeCompletionProvider();
        const string text = "class C { void M() { ret";

        var result = await provider.GetCompletionsAsync(CodeCompletionRequest.FromText(
            text,
            "C#",
            "Sample.cs",
            0,
            text.Length,
            isExplicit: true));

        Assert.Contains(result.Items, item => item.DisplayText == "return" && item.Kind == CodeCompletionItemKind.Keyword);
        Assert.Equal("ret", result.FilterText);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsCSharpMemberSuggestions()
    {
        using var provider = new RoslynCSharpCodeCompletionProvider();
        const string text = "using System;\nclass C { void M() { Console.";

        var result = await provider.GetCompletionsAsync(CodeCompletionRequest.FromText(
            text,
            "C#",
            "Sample.cs",
            1,
            "class C { void M() { Console.".Length,
            isExplicit: true));

        Assert.Contains(result.Items, item => item.DisplayText == "WriteLine" && item.Kind == CodeCompletionItemKind.Function);
        Assert.Equal(string.Empty, result.FilterText);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsEmptyForNonCSharpDocuments()
    {
        using var provider = new RoslynCSharpCodeCompletionProvider();
        var result = await provider.GetCompletionsAsync(CodeCompletionRequest.FromText(
            "const value = Math.",
            "JavaScript",
            "sample.js",
            0,
            "const value = Math.".Length,
            isExplicit: true));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReturnsEmptyAfterDispose()
    {
        var provider = new RoslynCSharpCodeCompletionProvider();
        provider.Dispose();

        var result = await provider.GetCompletionsAsync(CodeCompletionRequest.FromText(
            "class C { void M() { ret",
            "C#",
            "Sample.cs",
            0,
            "class C { void M() { ret".Length,
            isExplicit: true));

        Assert.Empty(result.Items);
    }
}
