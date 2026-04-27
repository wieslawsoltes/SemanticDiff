using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class CodeFoldingServiceTests
{
    [Fact]
    public void CreateFoldRegions_DetectsBraceAndRegionFoldings()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Demo.cs"), "Demo.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            #region demo
            public class Demo
            {
                public void Run()
                {
                }
            }
            #endregion
            """);
        var service = new CodeFoldingService();

        var regions = service.CreateFoldRegions(document);

        Assert.Contains(regions, region => region.StartLineIndex == 0 && region.EndLineIndex == 7 && region.Title == "demo");
        Assert.Contains(regions, region => region.StartLineIndex == 2 && region.EndLineIndex == 6);
        Assert.Contains(regions, region => region.StartLineIndex == 4 && region.EndLineIndex == 5);
    }

    [Fact]
    public void CreateFoldRegions_DetectsXmlTagFoldings()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("View.xaml"), "View.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Grid>
              <StackPanel>
                <TextBlock Text="Hello" />
              </StackPanel>
            </Grid>
            """);
        var service = new CodeFoldingService();

        var regions = service.CreateFoldRegions(document);

        Assert.Contains(regions, region => region.StartLineIndex == 0 && region.EndLineIndex == 4 && region.Title == "<Grid>");
        Assert.Contains(regions, region => region.StartLineIndex == 1 && region.EndLineIndex == 3 && region.Title == "<StackPanel>");
    }

    [Fact]
    public async Task TextMateDocumentTokenizer_ProducesFullFileTokensForViewerLines()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Demo.cs"), "Demo.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            public sealed class Demo
            {
                public string Name => "SemanticDiff";
            }
            """);
        var tokenizer = new TextMateDocumentTokenizer(pageSize: 2);

        var lines = await tokenizer.TokenizePageAsync(document, 0, document.LineCount, CancellationToken.None);

        Assert.Contains(lines.SelectMany(line => line.Tokens), token => token.Source == "textmate");
        Assert.Contains(lines[2].Tokens, token => token.StyleId == "string" || token.TokenType == "string");
    }
}
