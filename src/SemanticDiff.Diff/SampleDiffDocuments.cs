using System.Collections.Immutable;
using SemanticDiff.Core;

namespace SemanticDiff.Diff;

public static class SampleDiffDocuments
{
    public static ImmutableArray<DiffDocumentSnapshot> Create()
    {
        var factory = new DiffDocumentFactory();

        return
        [
            factory.CreateFromUnifiedDiff(
                new DiffDocumentMetadata(new DiffDocumentId("src/SemanticDiff.App/MainPage.xaml.cs"), "src/SemanticDiff.App/MainPage.xaml.cs", null, DiffFileStatus.Modified, "C#", 7, 1),
                """
                --- a/src/SemanticDiff.App/MainPage.xaml.cs
                +++ b/src/SemanticDiff.App/MainPage.xaml.cs
                @@ -1,6 +1,12 @@
                 namespace SemanticDiff.App;
                 
                 public sealed partial class MainPage : Page
                 {
                -    public MainPage() => InitializeComponent();
                +    public MainPage()
                +    {
                +        ViewModel = new MainViewModel();
                +        InitializeComponent();
                +        DiffCanvas.Scene = ViewModel.Scene;
                +    }
                 }
                """),
            factory.CreateFromUnifiedDiff(
                new DiffDocumentMetadata(new DiffDocumentId("src/SemanticDiff.App/MainPage.xaml"), "src/SemanticDiff.App/MainPage.xaml", null, DiffFileStatus.Modified, "XAML", 9, 2),
                """
                --- a/src/SemanticDiff.App/MainPage.xaml
                +++ b/src/SemanticDiff.App/MainPage.xaml
                @@ -1,5 +1,12 @@
                 <Page x:Class="SemanticDiff.App.MainPage"
                -      Background="{ThemeResource ApplicationPageBackgroundThemeBrush}">
                -  <TextBlock Text="Hello Uno Platform!" />
                +      xmlns:views="using:SemanticDiff.App.Views"
                +      Background="#101114">
                +  <Grid>
                +    <views:DiffCanvasControl x:Name="DiffCanvas" />
                +  </Grid>
                 </Page>
                """),
            factory.CreateFromUnifiedDiff(
                new DiffDocumentMetadata(new DiffDocumentId("src/SemanticDiff.Git/GitDiffService.cs"), "src/SemanticDiff.Git/GitDiffService.cs", null, DiffFileStatus.Added, "C#", 12, 0),
                """
                @@ -0,0 +1,12 @@
                +using SemanticDiff.Core;
                +
                +namespace SemanticDiff.Git;
                +
                +public sealed class GitDiffService : IGitDiffService
                +{
                +    public Task<GitDiffSnapshot> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
                +    {
                +        throw new NotImplementedException();
                +    }
                +}
                """)
        ];
    }
}