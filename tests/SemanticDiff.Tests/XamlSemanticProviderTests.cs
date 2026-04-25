using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Semantics.Xaml;

namespace SemanticDiff.Tests;

public sealed class XamlSemanticProviderTests
{
    [Fact]
    public async Task AnalyzeAsync_EmitsClassBindingResourceAndResolvedTypeAnchors()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("MainPage.xaml"), "MainPage.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page x:Class="Sample.MainPage"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:local="clr-namespace:Sample.Views">
              <Grid x:Name="Root" Background="{StaticResource PanelBrush}">
                <TextBlock Text="{Binding Title}" />
                <local:Widget />
              </Grid>
            </Page>
            """);
        var provider = new XamlSemanticProvider();

        var graph = await provider.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, [document]), CancellationToken.None);

        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.MainPage");
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.Views.Widget");
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.XamlName && anchor.DisplayName == "Root");
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.XamlClass && edge.Label == "x:Class");
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.Binding && edge.Label == "Text");
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.Resource && edge.Label == "Background");
    }

    [Fact]
    public async Task AnalyzeAsync_UsesClassFallbackForMalformedXaml()
    {
        var factory = new DiffDocumentFactory();
        var document = factory.CreateFromText(
            new DiffDocumentMetadata(new DiffDocumentId("BrokenPage.xaml"), "BrokenPage.xaml", null, DiffFileStatus.Modified, "XAML", 0, 0),
            """
            <Page x:Class="Sample.BrokenPage"
                  xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid>
            """);
        var provider = new XamlSemanticProvider();

        var graph = await provider.AnalyzeAsync(new SemanticAnalysisRequest(string.Empty, null, [document]), CancellationToken.None);

        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Type && anchor.DisplayName == "Sample.BrokenPage");
        Assert.Contains(graph.Anchors, anchor => anchor.Kind == SemanticAnchorKind.Unknown && anchor.DisplayName.StartsWith("XML parse error:", StringComparison.Ordinal));
        Assert.Contains(graph.Edges, edge => edge.Kind == SemanticEdgeKind.XamlClass && edge.Label == "x:Class");
    }

    [Fact]
    public void XmlParserRoslynXamlParser_ReturnsDiagnosticsWithLineAndColumn()
    {
        var parser = new XmlParserRoslynXamlParser();

        var document = parser.Parse("<Page>\n  <Grid>\n</Page>");

        Assert.True(document.HasErrors);
        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.True(diagnostic.Line >= 2);
        Assert.True(diagnostic.Column >= 1);
    }
}