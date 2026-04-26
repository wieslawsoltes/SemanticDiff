using System.Collections.Immutable;
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

    [Fact]
    public async Task AnalyzeAsync_DoesNotInferClassEdgesBetweenGenericXamlElements()
    {
        var factory = new DiffDocumentFactory();
        var first = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Views/FirstPage.xaml"), "Views/FirstPage.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid />
            </Page>
            """);
        var second = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Views/SecondPage.xaml"), "Views/SecondPage.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid />
            </Page>
            """);
        var orchestrator = new SemanticOrchestrator([new XamlSemanticProvider()]);

        var graph = await orchestrator.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, [first, second]), CancellationToken.None);

        Assert.DoesNotContain(graph.Edges, edge => edge.Kind is SemanticEdgeKind.XamlClass or SemanticEdgeKind.PartialClass);
    }

    [Fact]
    public async Task AnalyzeAsync_InfersResourceAndBindingEdgesAcrossChangedDocuments()
    {
        var factory = new DiffDocumentFactory();
        var resources = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Resources.xaml"), "Resources.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="PanelBrush" Color="Red" />
            </ResourceDictionary>
            """);
        var page = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("MainPage.xaml"), "MainPage.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page x:Class="Sample.MainPage"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid Background="{StaticResource PanelBrush}">
                <TextBlock Text="{Binding Title}" />
              </Grid>
            </Page>
            """);
        var viewModel = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("MainViewModel.cs"), "MainViewModel.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            namespace Sample;

            public sealed class MainViewModel
            {
                public string Title => "Hello";
            }
            """);
        var orchestrator = new SemanticOrchestrator([new CSharpSemanticProvider(), new XamlSemanticProvider()]);

        var graph = await orchestrator.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, [resources, page, viewModel]), CancellationToken.None);

        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.Resource && edge.Label == "resource");
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.Binding && edge.Label == "binding");
    }

    [Fact]
    public async Task AnalyzeAsync_InfersCompanionEdgesFromXamlCodeBehindPaths()
    {
        var factory = new DiffDocumentFactory();
        var view = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Views/MainView.xaml"), "Views/MainView.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
            """);
        var codeBehind = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("Views/MainView.xaml.cs"), "Views/MainView.xaml.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
            """
            namespace Sample.Views;

            public sealed partial class RenamedMainView
            {
            }
            """);
        var orchestrator = new SemanticOrchestrator([new CSharpSemanticProvider(), new XamlSemanticProvider()]);

        var graph = await orchestrator.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, [view, codeBehind]), CancellationToken.None);

        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.GeneratedFile && edge.Label == "companion");
    }

    [Fact]
    public async Task AnalyzeAsync_InfersRepositoryAreaCohesionEdges()
    {
        var factory = new DiffDocumentFactory();
        var documents = Enumerable.Range(0, 4)
            .Select(index => factory.CreateFromText(
                new DiffDocumentMetadata(new DiffDocumentId($"src/Dock.Uno/Changed{index}.cs"), $"src/Dock.Uno/Changed{index}.cs", null, DiffFileStatus.Modified, "C#", 0, 0),
                $"namespace Dock.Uno; public sealed class Changed{index} {{ }}"))
            .ToArray();
        var orchestrator = new SemanticOrchestrator([new CSharpSemanticProvider()]);

        var graph = await orchestrator.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, documents.ToImmutableArray()), CancellationToken.None);

        Assert.True(graph.Edges.Count(edge => edge.Kind == SemanticEdgeKind.ProjectReference && edge.Label == "area") >= 3);
    }
}