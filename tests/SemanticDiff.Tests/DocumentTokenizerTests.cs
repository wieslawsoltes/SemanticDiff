using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DocumentTokenizerTests
{
    [Fact]
    public async Task TextMateDocumentTokenizer_TokenizePageAsync_UsesGrammarScopesForCSharpStrings()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            public sealed class Sample
            {
                private string Name = "Semantic";
            }
            """);
        var tokenizer = new TextMateDocumentTokenizer(pageSize: 2);

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);

        Assert.Equal(document.LineCount, lines.Length);
        Assert.Contains(lines.SelectMany(line => line.Tokens), token => token.StyleId == "keyword");
        Assert.Contains(lines.SelectMany(line => line.Tokens), token => token.StyleId == "string");
    }

    [Fact]
    public async Task TextMateDocumentTokenizer_TokenizePageAsync_ReturnsRequestedRangeAcrossPages()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            using System;
            public sealed class Sample
            {
                public void Run()
                {
                    Console.WriteLine("SemanticDiff");
                }
            }
            """);
        var tokenizer = new TextMateDocumentTokenizer(pageSize: 2);

        var lines = await tokenizer.TokenizePageAsync(document, 1, 4, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Equal([1, 2, 3, 4], lines.Select(line => line.Index));
        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.False(string.IsNullOrWhiteSpace(token.StyleId)));
    }

    [Fact]
    public async Task PlainTextDocumentTokenizer_TokenizePageAsync_TokenizesXmlWithoutBuilderCapacityFailure()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("View.axaml"), "View.axaml", null, DiffFileStatus.Modified, "AXAML", 0, 0),
            "<StackPanel><TextBlock Text=\"SemanticDiff\" /></StackPanel>");
        var tokenizer = new PlainTextDocumentTokenizer();

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);

        Assert.Contains(lines.SelectMany(line => line.Tokens), token => token.StyleId == "type");
    }
}