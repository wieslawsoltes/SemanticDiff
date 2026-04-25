using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class PerformanceSmokeTests
{
    [Fact]
    public void DiffDocumentSnapshot_ReturnsVisibleWindowFromLargeDocument()
    {
        var factory = new DiffDocumentFactory();
        var text = string.Join('\n', Enumerable.Range(1, 50_000).Select(index => $"line {index}"));
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Large.cs"), "Large.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            text);

        var visibleLines = document.GetVisibleLines(49_990, 5).ToArray();

        Assert.Equal(50_000, document.LineCount);
        Assert.Equal([49_990, 49_991, 49_992, 49_993, 49_994], visibleLines.Select(line => line.Index));
        Assert.Equal("line 49991", visibleLines[0].Text);
    }
}