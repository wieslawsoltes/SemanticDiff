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
        Assert.Contains(lines.SelectMany(line => line.Tokens), token =>
            token.HasRichMetadata &&
            token.LanguageId == "csharp" &&
            token.Source == "textmate" &&
            !token.Scopes.IsDefaultOrEmpty);
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

        Assert.Contains(lines.SelectMany(line => line.Tokens), token => token.StyleId is "tag" or "type");
    }

    [Fact]
    public async Task TextMateDocumentTokenizer_TokenizePageAsync_ColorsNonSemanticJavaScriptFiles()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/app.js"), "src/app.js", null, DiffFileStatus.Modified, "JavaScript", 0, 0),
            """
            export function run(value) {
                return `hello ${value}`;
            }
            """);
        var tokenizer = new TextMateDocumentTokenizer(pageSize: 2);

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Contains(tokens, token => token.StyleId == "keyword" && token.LanguageId == "javascript");
        Assert.Contains(tokens, token => token.StyleId is "function" or "string" && token.HasRichMetadata);
    }

    [Fact]
    public async Task AdaptiveDocumentTokenizer_TokenizePageAsync_KeepsTextMateForPreciseSmallCSharpFiles()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Sample.cs"), "Sample.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            public sealed class Sample
            {
                public string Name => "Semantic";
            }
            """);
        var tokenizer = new AdaptiveDocumentTokenizer(pageSize: 2);

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Contains(tokens, token => token.Source == "textmate" && token.LanguageId == "csharp");
    }

    [Fact]
    public async Task AdaptiveDocumentTokenizer_TokenizePageAsync_RoutesCppToFastFallback()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("native.cpp"), "native.cpp", null, DiffFileStatus.Modified, "C++", 0, 0),
            """
            #include <vector>
            class NativeRenderer final
            {
            public:
                void Draw(int frame) const;
            };
            """);
        var tokenizer = new AdaptiveDocumentTokenizer(pageSize: 2);

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Contains(tokens, token => token.StyleId == "keyword" && token.LanguageId == "cpp" && token.Source == "fallback");
        Assert.DoesNotContain(tokens, token => token.Source == "textmate");
    }

    [Fact]
    public async Task AdaptiveDocumentTokenizer_TokenizePageAsync_RoutesLargeUnknownFilesToFastFallback()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("generated.dsl"), "generated.dsl", null, DiffFileStatus.Modified, "CustomDsl", 0, 0),
            """
            step One()
            step Two()
            step Three()
            """);
        var options = new AdaptiveTokenizationOptions(LargeDocumentLineThreshold: 2);
        var tokenizer = new AdaptiveDocumentTokenizer(pageSize: 2, options: options);

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Contains(tokens, token => token.StyleId == "function" && token.Source == "fallback");
        Assert.DoesNotContain(tokens, token => token.Source == "textmate");
    }

    [Fact]
    public async Task PlainTextDocumentTokenizer_TokenizePageAsync_FallbackAddsRichTokensForUnsupportedSemanticLanguage()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("tools/build.py"), "tools/build.py", null, DiffFileStatus.Modified, "Python", 0, 0),
            """
            def build(target):
                print("building", target) # compile package
            """);
        var tokenizer = new PlainTextDocumentTokenizer();

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Contains(tokens, token => token.StyleId == "keyword" && token.TokenType == "keyword" && token.Source == "fallback");
        Assert.Contains(tokens, token => token.StyleId == "function" && token.TokenType == "function" && token.LanguageId == "python");
        Assert.Contains(tokens, token => token.StyleId == "string" && token.TokenType == "string");
        Assert.Contains(tokens, token => token.StyleId == "comment" && token.TokenType == "comment");
    }

    [Fact]
    public async Task PlainTextDocumentTokenizer_TokenizePageAsync_FallbackClassifiesJsonProperties()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("appsettings.custom"), "appsettings.custom", null, DiffFileStatus.Modified, "JSON", 0, 0),
            """
            {
              "enabled": true,
              "retryCount": 3
            }
            """);
        var tokenizer = new PlainTextDocumentTokenizer();

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);
        var tokens = lines.SelectMany(line => line.Tokens).ToArray();

        Assert.Contains(tokens, token => token.StyleId == "property" && token.TokenType == "property");
        Assert.Contains(tokens, token => token.StyleId == "keyword" && token.TokenType == "keyword");
        Assert.Contains(tokens, token => token.StyleId == "number" && token.TokenType == "number");
    }
}
