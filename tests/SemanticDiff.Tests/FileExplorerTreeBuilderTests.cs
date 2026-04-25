using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Tests;

public sealed class FileExplorerTreeBuilderTests
{
    [Fact]
    public void Build_CreatesSortedFolderHierarchyWithFiles()
    {
        var files = new[]
        {
            new FileExplorerFile("src/App/MainPage.xaml", DiffFileStatus.Modified, "XAML"),
            new FileExplorerFile("src/App/MainPage.xaml.cs", DiffFileStatus.Added, "C#"),
            new FileExplorerFile("README.md", DiffFileStatus.Modified, "Markdown")
        };

        var tree = FileExplorerTreeBuilder.Build(files);

        Assert.Equal(["src", "README.md"], tree.Select(node => node.Name).ToArray());
        var src = tree[0];
        var app = Assert.Single(src.Children);
        Assert.Equal("App", app.Name);
        Assert.Equal(["MainPage.xaml", "MainPage.xaml.cs"], app.Children.Select(node => node.Name).ToArray());
        Assert.Equal(FileExplorerIconKind.Xaml, app.Children[0].IconKind);
        Assert.Equal(FileExplorerIconKind.CSharp, app.Children[1].IconKind);
    }

    [Fact]
    public void Build_AggregatesFolderStatusByMostImportantChildStatus()
    {
        var files = new[]
        {
            new FileExplorerFile("src/App/MainPage.xaml", DiffFileStatus.Modified, "XAML"),
            new FileExplorerFile("src/App/Conflict.cs", DiffFileStatus.Conflicted, "C#"),
            new FileExplorerFile("src/App/Old.cs", DiffFileStatus.Deleted, "C#")
        };

        var tree = FileExplorerTreeBuilder.Build(files);

        var src = Assert.Single(tree);
        var app = Assert.Single(src.Children);
        Assert.Equal(DiffFileStatus.Conflicted, src.Status);
        Assert.Equal(DiffFileStatus.Conflicted, app.Status);
    }

    [Theory]
    [InlineData("Directory.Build.props", "XML", FileExplorerIconKind.Project)]
    [InlineData("SemanticDiff.slnx", "Text", FileExplorerIconKind.Solution)]
    [InlineData(".gitignore", "Text", FileExplorerIconKind.Git)]
    [InlineData("appsettings.json", "JSON", FileExplorerIconKind.Json)]
    [InlineData("docs/overview.md", "Markdown", FileExplorerIconKind.Markdown)]
    [InlineData("assets/logo.png", "Image", FileExplorerIconKind.Image)]
    public void GetIconKind_ClassifiesCommonRepositoryFileTypes(string path, string language, FileExplorerIconKind expected)
    {
        Assert.Equal(expected, FileExplorerTreeBuilder.GetIconKind(path, language));
    }

    [Fact]
    public void Build_CanConsumeDiffDocumentsDirectly()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("src/App/MainPage.xaml.cs"), "src/App/MainPage.xaml.cs", null, DiffFileStatus.Modified, "C#", 1, 1),
            "public partial class MainPage { }");

        var tree = FileExplorerTreeBuilder.Build([document]);

        var src = Assert.Single(tree);
        var app = Assert.Single(src.Children);
        var file = Assert.Single(app.Children);
        Assert.Equal("src/App/MainPage.xaml.cs", file.DocumentId);
        Assert.Equal(FileExplorerIconKind.CSharp, file.IconKind);
    }
}