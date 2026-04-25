using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class DiffDocumentFactoryTests
{
    [Fact]
    public void CreateFromUnifiedDiff_PreservesLineKinds()
    {
        var factory = new DiffDocumentFactory();
        var metadata = new DiffDocumentMetadata(new DiffDocumentId("sample.cs"), "sample.cs", null, DiffFileStatus.Modified, "C#", 1, 1);

        var document = factory.CreateFromUnifiedDiff(metadata, """
            @@ -1,2 +1,2 @@
             keep
            -old
            +new
            """);

        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Deleted && line.Text == "old");
        Assert.Contains(document.Lines, line => line.Kind == DiffLineKind.Added && line.Text == "new");
    }
}