# SemanticDiff.Controls.Uno.Sample

Standalone Uno desktop host for the reusable SemanticDiff controls.

The sample references `SemanticDiff.Controls.Uno` and does not reference `SemanticDiff.App`. It proves the extracted controls can be hosted independently with sample diff documents.

## Run

```bash
dotnet run --project samples/SemanticDiff.Controls.Uno.Sample/SemanticDiff.Controls.Uno.Sample.csproj
```

## What It Shows

| Surface | Control | Data |
| --- | --- | --- |
| Graph pane | `DiffCanvasControl` | `DiffCanvasScene.FromDocuments(SampleDiffDocuments.Create())` |
| Text pane | `CodeFileViewerControl` | First sample document's `DiffLine` collection |
