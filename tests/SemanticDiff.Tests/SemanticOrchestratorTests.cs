using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Semantics;
using SemanticDiff.Semantics.Roslyn;
using SemanticDiff.Semantics.Xaml;

namespace SemanticDiff.Tests;

public sealed class SemanticOrchestratorTests
{
    [Fact]
    public async Task AnalyzeAsync_InfersXamlClassEdgeBetweenXamlAndCodeBehind()
    {
        var factory = new DiffDocumentFactory();
        var csharpDocument = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("MainPage.xaml.cs"), "MainPage.xaml.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            namespace Sample;

            public sealed partial class MainPage
            {
            }
            """);
        var xamlDocument = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("MainPage.xaml"), "MainPage.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page x:Class="Sample.MainPage"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
            """);
        var orchestrator = new SemanticOrchestrator([new CSharpSemanticProvider(), new XamlSemanticProvider()]);

        var graph = await orchestrator.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, [csharpDocument, xamlDocument]), CancellationToken.None);

        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.XamlClass && edge.Label == "x:Class");
    }
}