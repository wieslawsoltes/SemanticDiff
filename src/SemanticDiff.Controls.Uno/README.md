# SemanticDiff.Controls.Uno

Reusable Uno controls for rendering SemanticDiff graph and code-review surfaces outside `SemanticDiff.App`.

## Included Controls

| Control | Purpose | Primary Inputs |
| --- | --- | --- |
| `DiffCanvasControl` | Interactive Skia-backed graph canvas for diff nodes, groups, annotations, pan/zoom, selection, and node commands. | `SemanticDiff.Rendering.DiffCanvasScene` |
| `CodeFileViewerControl` | Skia-backed code/diff text viewer with syntax token rendering, line numbers, folding metadata, annotations, text selection, and font scaling. | `IEnumerable<SemanticDiff.Core.DiffLine>`, `IEnumerable<SemanticDiff.Core.CodeFoldRegion>` |
| `ResizeCursorGrid` | Small Uno primitive for splitter/cursor behavior. | Standard WinUI layout/input properties |

## Usage

Add the project or package reference, then merge the shared resources before using the controls:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
      <ResourceDictionary Source="ms-appx:///SemanticDiff.Controls.Uno/Themes/SemanticDiffControls.xaml" />
      <ResourceDictionary Source="ms-appx:///SemanticDiff.Controls.Uno/Themes/SemanticDiffWorkbenchTemplates.xaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

```xml
<Page
    xmlns:controls="using:SemanticDiff.Controls.Uno">
  <Grid ColumnDefinitions="*,*">
    <controls:DiffCanvasControl x:Name="DiffCanvas" />
    <controls:CodeFileViewerControl Grid.Column="1"
                                    x:Name="CodeViewer"
                                    IsDiffMode="True"
                                    CodeFontSize="15" />
  </Grid>
</Page>
```

```csharp
var documents = SampleDiffDocuments.Create();
DiffCanvas.Scene = DiffCanvasScene.FromDocuments(documents);
CodeViewer.Lines = documents[0].Lines;
```

## Dependency Boundary

`SemanticDiff.Controls.Uno` is the only project in the reusable stack that references Uno/WinUI and SkiaSharp view hosts. It intentionally does not reference `SemanticDiff.App`; consumers should provide their own view models, commands, and application state.

Current direct dependencies:

| Dependency | Reason |
| --- | --- |
| `SemanticDiff.Core` | Diff documents, lines, annotations, fold regions, and review metadata. |
| `SemanticDiff.Diff` | Sample/test diff document helpers and diff model creation support. |
| `SemanticDiff.Rendering` | Canvas scene model and renderer contract. |
| `SkiaSharp.Views.Uno.WinUI` | `SKXamlCanvas` host used by graph and text controls. |

## Packaging Decision

Do not publish this project as a NuGet package yet. The controls are reusable inside this repository and are validated by the standalone sample host, but the public API is still stabilizing around:

| Area | Before Packing |
| --- | --- |
| Control commands/events | Finalize command/event names for node, annotation, and line-context actions. |
| Text viewer model | Decide whether `DiffLine` remains the public input or gets a smaller rendering DTO. |
| Theme resources | Freeze resource keys or add compatibility aliases. |
| Target frameworks | Decide whether to keep only `net10.0-desktop` or add mobile/browser targets. |
