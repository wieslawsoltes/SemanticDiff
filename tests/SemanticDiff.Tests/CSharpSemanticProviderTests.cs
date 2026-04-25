using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Semantics.Roslyn;

namespace SemanticDiff.Tests;

public sealed class CSharpSemanticProviderTests
{
    [Fact]
    public async Task AnalyzeAsync_EmitsInheritanceAndReferenceEdgesAcrossChangedDocuments()
    {
        var documents = CreateDocuments(
            ("BaseViewModel.cs", """
             namespace Sample;

             public class BaseViewModel
             {
                 public string Title => "SemanticDiff";
             }
             """),
            ("ShellViewModel.cs", """
             namespace Sample;

             public sealed class ShellViewModel : BaseViewModel
             {
                 public BaseViewModel Create() => new BaseViewModel();
             }
             """));
        var provider = new CSharpSemanticProvider();

        var graph = await provider.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, documents), CancellationToken.None);

        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.BaseViewModel");
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Member && anchor.DisplayName == "Create");
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.TypeInheritance && edge.Label == "BaseViewModel");
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.SymbolReference && edge.Label == "BaseViewModel");
    }

    [Fact]
    public async Task AnalyzeAsync_FastSyntaxMode_DoesNotOpenMsBuildWorkspace()
    {
        var documents = CreateDocuments(
            ("ShellViewModel.cs", "namespace Sample; public sealed class ShellViewModel { public void Run() { } }"));
        var workspaceFactory = new TrackingWorkspaceFactory();
        var provider = new CSharpSemanticProvider(workspaceFactory);

        var graph = await provider.AnalyzeAsync(
            new SemanticAnalysisRequest("/repo", null, documents, SemanticAnalysisMode.FastSyntaxOnly),
            CancellationToken.None);

        Assert.False(workspaceFactory.WasCalled);
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.ShellViewModel");
    }

    [Fact]
    public async Task AnalyzeAsync_FastSyntaxMode_EmitsSymbolsForLargeChangedCSharpSet()
    {
        var sources = Enumerable.Range(0, 120)
            .Select(index => ($"src/Changed{index}.cs", $"namespace Sample; public sealed class Changed{index} {{ public void Run{index}() {{ }} }}"))
            .Append(("src/ColumnBase`1.cs", "namespace Sample; public class ColumnBase<T> { public T? Value { get; set; } }"))
            .ToArray();
        var documents = CreateDocuments(sources);
        var provider = new CSharpSemanticProvider();

        var graph = await provider.AnalyzeAsync(
            new SemanticAnalysisRequest("/repo", null, documents, SemanticAnalysisMode.FastSyntaxOnly),
            CancellationToken.None);

        Assert.True(graph.Anchors.Count(anchor => anchor.Kind == SemanticAnchorKind.Type) >= 121);
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.Changed119");
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.ColumnBase<T>");
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Member && anchor.DisplayName == "Run119");
    }

    [Fact]
    public async Task AnalyzeAsync_WorkspaceModeKeepsSyntaxCoverageForFilesOutsideLoadedProject()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"SemanticDiffTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Included.cs" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "Included.cs"), "namespace Sample; public sealed class Included { }");
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, "AddedOutsideProject.cs"), "namespace Sample; public sealed class AddedOutsideProject { }");

        try
        {
            var documents = CreateDocuments(
                ("Included.cs", "namespace Sample; public sealed class Included { }"),
                ("AddedOutsideProject.cs", "namespace Sample; public sealed class AddedOutsideProject { }"));
            var provider = new CSharpSemanticProvider();

            var graph = await provider.AnalyzeAsync(
                new SemanticAnalysisRequest(repositoryPath, null, documents, SemanticAnalysisMode.WorkspaceThenSyntax),
                CancellationToken.None);

            Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.Included");
            Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.AddedOutsideProject");
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(params (string Path, string Text)[] sources)
    {
        var factory = new DiffDocumentFactory();
        return sources
            .Select(source => factory.CreateFromText(
                new DiffDocumentMetadata(new DiffDocumentId(source.Path), source.Path, null, DiffFileStatus.Modified, "C#", 0, 0),
                source.Text))
            .ToImmutableArray();
    }

    private sealed class TrackingWorkspaceFactory : MSBuildWorkspaceFactory
    {
        public bool WasCalled { get; private set; }

        public override Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace CreateWorkspace()
        {
            WasCalled = true;
            throw new InvalidOperationException("MSBuildWorkspace should not be opened in fast syntax mode.");
        }
    }
}