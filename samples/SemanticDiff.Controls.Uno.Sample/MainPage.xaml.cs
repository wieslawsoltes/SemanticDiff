using SemanticDiff.Diff;
using SemanticDiff.Rendering;

namespace SemanticDiff.Controls.Uno.Sample;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();

        var documents = SampleDiffDocuments.Create();
        DiffCanvas.Scene = DiffCanvasScene.FromDocuments(documents);
        CodeViewer.Lines = documents.Length > 0 ? documents[0].Lines : [];
    }
}
