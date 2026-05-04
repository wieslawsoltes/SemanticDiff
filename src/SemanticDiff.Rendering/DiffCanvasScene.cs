using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Rendering;

public sealed class DiffNode
{
    public const double MinWidth = 320;
    public const double MinHeight = 180;
    public const double TitleHeight = 32;
    public const double FooterHeight = 22;
    public const double DefaultFontSize = 12.5;
    public const double MinFontSize = 9;
    public const double MaxFontSize = 22;
    public const double FontSizeStep = 1;
    public const double FontControlButtonScreenSize = 20;
    public const double FontControlGapScreenSize = 5;
    public const double FontControlRightInsetScreenSize = 98;
    public const double FontControlLineCountInset = 100;
    public const double FontControlLineCountGapScreenSize = 8;
    public const double FontControlMinimumNodeScreenWidth = 220;
    public const double FontControlMinimumTitleScreenHeight = 22;
    public const double DiffGutterWidth = 94;
    public const double FullFileGutterWidth = 76;
    public const double MarkerWidth = 24;
    public const double CodeLeftPadding = 10;
    private const int EditorTabSize = 4;

    public DiffNode(DiffDocumentSnapshot document, Rect2 bounds, bool isPinned = false, double fontSize = DefaultFontSize)
    {
        DiffDocument = document;
        Document = document;
        Bounds = bounds;
        IsPinned = isPinned;
        FontSize = NormalizeFontSize(fontSize);
    }

    public DiffDocumentSnapshot DiffDocument { get; }

    public DiffDocumentSnapshot? FullFileDocument { get; private set; }

    public string? FullText { get; private set; }

    public ImmutableArray<CodeFoldRegion> FoldRegions { get; private set; } = [];

    public DiffDocumentSnapshot Document { get; private set; }

    public bool HasFullFileDocument => FullFileDocument is not null;

    public bool IsShowingFullFile => (fullFileOverride ?? workspaceShowFullFile) && FullFileDocument is not null;

    public bool IsEditingActive => IsShowingFullFile && (editingOverride ?? workspaceEditing);

    public bool IsEditorFocused { get; private set; }

    public int CaretLineIndex { get; private set; }

    public int CaretColumn { get; private set; }

    public bool? FullFileViewOverride => fullFileOverride;

    public bool? EditingOverride => editingOverride;

    public Rect2 Bounds { get; set; }

    public double ScrollOffsetY { get; private set; }

    public bool IsSelected { get; set; }

    public bool IsPinned { get; set; }

    public double FontSize { get; private set; }

    public double LineHeight => Math.Round(FontSize + 4.5, 2);

    public double MaxScrollOffset => Math.Max(0, VisibleLineCount * LineHeight - BodyBounds.Height);

    public Rect2 TitleBounds => new(Bounds.X, Bounds.Y, Bounds.Width, TitleHeight);

    public Rect2 BodyBounds => new(Bounds.X, Bounds.Y + TitleHeight, Bounds.Width, Math.Max(0, Bounds.Height - TitleHeight - FooterHeight));

    public int VisibleLineCount => GetVisibleLineCount();

    private bool workspaceShowFullFile;
    private bool workspaceEditing;
    private bool? fullFileOverride;
    private bool? editingOverride;
    private readonly HashSet<int> collapsedFoldStartLines = [];
    private List<string>? editableLines;
    private Dictionary<int, CodeFoldRegion>? foldRegionsByStartLineIndex;
    private CodeFoldRegion[]? activeFoldRegionsByStartLine;
    private Dictionary<int, ImmutableArray<CodeFoldRegion>>? activeFoldRegionsByLineIndex;
    private int? visibleLineCountCache;

    public void ScrollBy(double deltaY)
    {
        ScrollOffsetY = ClampScrollOffset(ScrollOffsetY + deltaY);
    }

    public void ClampScrollOffset()
    {
        ScrollOffsetY = ClampScrollOffset(ScrollOffsetY);
    }

    public void RestoreScrollOffset(double scrollOffsetY)
    {
        ScrollOffsetY = ClampScrollOffset(scrollOffsetY);
    }

    private double ClampScrollOffset(double scrollOffsetY)
    {
        return Math.Clamp(scrollOffsetY, 0, MaxScrollOffset);
    }

    public void SetScrollOffset(double scrollOffsetY)
    {
        ScrollOffsetY = ClampScrollOffset(scrollOffsetY);
    }

    public void IncreaseFontSize() => SetFontSize(FontSize + FontSizeStep);

    public void DecreaseFontSize() => SetFontSize(FontSize - FontSizeStep);

    public void SetFontSize(double fontSize)
    {
        FontSize = NormalizeFontSize(fontSize);
        ClampScrollOffset();
    }

    public void SetFullFileDocument(DiffDocumentSnapshot fullFileDocument, ImmutableArray<CodeFoldRegion> foldRegions, string fullText)
    {
        FullFileDocument = fullFileDocument;
        FullText = fullText;
        FoldRegions = foldRegions.IsDefault ? ImmutableArray<CodeFoldRegion>.Empty : foldRegions;
        editableLines = null;
        foldRegionsByStartLineIndex = null;
        activeFoldRegionsByStartLine = null;
        activeFoldRegionsByLineIndex = null;
        visibleLineCountCache = null;
        collapsedFoldStartLines.RemoveWhere(lineIndex => lineIndex < 0 || lineIndex >= fullFileDocument.LineCount);
        ApplyDocumentMode();
    }

    public void ApplyWorkspaceMode(bool showFullFile, bool enableEditing)
    {
        workspaceShowFullFile = showFullFile;
        workspaceEditing = enableEditing;
        ApplyDocumentMode();
    }

    public void ToggleFullFileOverride()
    {
        fullFileOverride = !IsShowingFullFile;
        ApplyDocumentMode();
    }

    public void SetFullFileOverride(bool? isShowingFullFile)
    {
        fullFileOverride = isShowingFullFile;
        ApplyDocumentMode();
    }

    public void ClearFullFileOverride()
    {
        fullFileOverride = null;
        ApplyDocumentMode();
    }

    public void ToggleEditingOverride()
    {
        editingOverride = !IsEditingActive;
        ApplyDocumentMode();
    }

    public void SetEditingOverride(bool? isEditing)
    {
        editingOverride = isEditing;
        ApplyDocumentMode();
    }

    public void ClearEditingOverride()
    {
        editingOverride = null;
        ApplyDocumentMode();
    }

    public void RestoreEditorState(bool? fullFileViewOverride, bool? editingOverride, int caretLineIndex, int caretColumn)
    {
        this.fullFileOverride = fullFileViewOverride;
        this.editingOverride = editingOverride;
        ApplyDocumentMode();
        CaretLineIndex = Math.Clamp(caretLineIndex, 0, Math.Max(0, Document.LineCount - 1));
        CaretColumn = Math.Clamp(caretColumn, 0, GetEditableLine(CaretLineIndex).Length);
    }

    public void SetEditorFocus(bool isFocused)
    {
        IsEditorFocused = isFocused && IsEditingActive;
    }

    public bool ToggleFold(int startLineIndex)
    {
        if (!IsShowingFullFile || !GetFoldRegionsByStartLineIndex().ContainsKey(startLineIndex))
        {
            return false;
        }

        if (!collapsedFoldStartLines.Add(startLineIndex))
        {
            collapsedFoldStartLines.Remove(startLineIndex);
        }

        visibleLineCountCache = null;
        activeFoldRegionsByLineIndex = null;
        ClampScrollOffset();
        return true;
    }

    public bool TryHitTestFold(Point2 worldPoint, out int startLineIndex)
    {
        startLineIndex = -1;
        if (!IsShowingFullFile || !BodyBounds.Contains(worldPoint))
        {
            return false;
        }

        var foldLaneLeft = BodyBounds.Left + 8;
        var foldLaneRight = BodyBounds.Left + 30;
        if (worldPoint.X < foldLaneLeft || worldPoint.X > foldLaneRight)
        {
            return false;
        }

        var rowIndex = (int)Math.Floor((worldPoint.Y - BodyBounds.Top + ScrollOffsetY) / LineHeight);
        var rows = GetVisibleRows(rowIndex, 1);
        if (rows.Length == 0 || rows[0].FoldRegion is not { } foldRegion)
        {
            return false;
        }

        startLineIndex = foldRegion.StartLineIndex;
        return true;
    }

    public bool TrySetCaretFromWorldPoint(Point2 worldPoint)
    {
        if (!IsEditingActive || !BodyBounds.Contains(worldPoint))
        {
            return false;
        }

        var rowIndex = (int)Math.Floor((worldPoint.Y - BodyBounds.Top + ScrollOffsetY) / LineHeight);
        var rows = GetVisibleRows(rowIndex, 1);
        if (rows.Length == 0)
        {
            return false;
        }

        var codeX = BodyBounds.Left + FullFileGutterWidth + CodeLeftPadding;
        var characterWidth = Math.Max(4.0, FontSize * 0.62);
        var visualColumn = Math.Max(0, (int)Math.Round((worldPoint.X - codeX) / characterWidth));
        var text = rows[0].Line.Text;
        CaretLineIndex = rows[0].Line.Index;
        CaretColumn = GetSourceColumnFromVisualColumn(text, visualColumn);
        IsEditorFocused = true;
        return true;
    }

    public bool MoveCaret(int lineDelta, int columnDelta)
    {
        if (!IsEditingActive)
        {
            return false;
        }

        var visibleLineCount = VisibleLineCount;
        if (visibleLineCount == 0)
        {
            return false;
        }

        var currentRowIndex = GetVisibleRowIndexForLineIndex(CaretLineIndex);
        if (currentRowIndex < 0)
        {
            currentRowIndex = 0;
        }

        if (lineDelta == 0 && columnDelta != 0)
        {
            var direction = Math.Sign(columnDelta);
            for (var step = 0; step < Math.Abs(columnDelta); step++)
            {
                MoveCaretByCharacter(direction);
            }

            EnsureCaretVisible();
            return true;
        }

        var nextRowIndex = Math.Clamp(currentRowIndex + lineDelta, 0, visibleLineCount - 1);
        if (!TryGetLineIndexAtVisibleRowIndex(nextRowIndex, out var nextLineIndex))
        {
            return false;
        }

        CaretLineIndex = nextLineIndex;
        CaretColumn = Math.Clamp(CaretColumn + columnDelta, 0, GetEditableLine(nextLineIndex).Length);
        EnsureCaretVisible();
        return true;
    }

    public bool MoveCaretWord(int direction)
    {
        if (!IsEditingActive || direction == 0)
        {
            return false;
        }

        EnsureEditableLines();
        var text = string.Join('\n', editableLines!);
        var offset = GetEditableTextOffset();
        offset = direction < 0
            ? FindPreviousTextWordBoundary(text, offset)
            : FindNextTextWordBoundary(text, offset);
        SetCaretFromTextOffset(offset);
        EnsureCaretVisible();
        return true;
    }

    public bool SetCaretColumn(int column)
    {
        if (!IsEditingActive)
        {
            return false;
        }

        CaretColumn = Math.Clamp(column, 0, GetEditableLine(CaretLineIndex).Length);
        EnsureCaretVisible();
        return true;
    }

    public bool MoveCaretToDocumentStart()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        CaretLineIndex = 0;
        CaretColumn = 0;
        EnsureCaretVisible();
        return true;
    }

    public bool MoveCaretToDocumentEnd()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        CaretLineIndex = Math.Max(0, editableLines!.Count - 1);
        CaretColumn = GetEditableLine(CaretLineIndex).Length;
        EnsureCaretVisible();
        return true;
    }

    public bool MoveCaretToSmartLineStart()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        var line = GetEditableLine(CaretLineIndex);
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < line.Length && char.IsWhiteSpace(line[firstNonWhitespace]))
        {
            firstNonWhitespace++;
        }

        CaretColumn = CaretColumn == firstNonWhitespace ? 0 : firstNonWhitespace;
        EnsureCaretVisible();
        return true;
    }

    public bool InsertText(string text)
    {
        if (!IsEditingActive || string.IsNullOrEmpty(text))
        {
            return false;
        }

        EnsureEditableLines();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                InsertNewLineCore();
                continue;
            }

            if (character == '\n')
            {
                InsertNewLineCore();
            }
            else if (!char.IsControl(character))
            {
                InsertCharacterCore(character);
            }
        }

        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    private static int GetSourceColumnFromVisualColumn(string text, int visualColumn)
    {
        if (visualColumn <= 0 || text.Length == 0)
        {
            return 0;
        }

        var visual = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var width = text[index] == '\t' ? EditorTabSize : 1;
            var nextVisual = visual + width;
            if (visualColumn < nextVisual)
            {
                return visualColumn - visual < width / 2.0 ? index : index + 1;
            }

            visual = nextVisual;
        }

        return text.Length;
    }

    public bool InsertNewLine()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        InsertNewLineCore();
        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool Backspace()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        if (CaretColumn > 0)
        {
            var line = editableLines[CaretLineIndex];
            var column = Math.Clamp(CaretColumn, 0, line.Length);
            editableLines[CaretLineIndex] = line.Remove(column - 1, 1);
            CaretColumn = column - 1;
        }
        else if (CaretLineIndex > 0)
        {
            var previous = editableLines[CaretLineIndex - 1];
            var current = editableLines[CaretLineIndex];
            editableLines[CaretLineIndex - 1] = previous + current;
            editableLines.RemoveAt(CaretLineIndex);
            CaretLineIndex--;
            CaretColumn = previous.Length;
        }
        else
        {
            return false;
        }

        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool BackspaceWord()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        var line = editableLines[CaretLineIndex];
        var column = Math.Clamp(CaretColumn, 0, line.Length);
        if (column > 0)
        {
            var startColumn = FindPreviousWordBoundary(line, column);
            if (startColumn == column)
            {
                return false;
            }

            editableLines[CaretLineIndex] = line.Remove(startColumn, column - startColumn);
            CaretColumn = startColumn;
        }
        else if (CaretLineIndex > 0)
        {
            var previous = editableLines[CaretLineIndex - 1];
            editableLines[CaretLineIndex - 1] = previous + line;
            editableLines.RemoveAt(CaretLineIndex);
            CaretLineIndex--;
            CaretColumn = previous.Length;
        }
        else
        {
            return false;
        }

        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool Delete()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        var line = editableLines[CaretLineIndex];
        var column = Math.Clamp(CaretColumn, 0, line.Length);
        if (column < line.Length)
        {
            editableLines[CaretLineIndex] = line.Remove(column, 1);
        }
        else if (CaretLineIndex < editableLines.Count - 1)
        {
            editableLines[CaretLineIndex] = line + editableLines[CaretLineIndex + 1];
            editableLines.RemoveAt(CaretLineIndex + 1);
        }
        else
        {
            return false;
        }

        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool DeleteWord()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        var line = editableLines[CaretLineIndex];
        var column = Math.Clamp(CaretColumn, 0, line.Length);
        if (column < line.Length)
        {
            var endColumn = FindNextWordBoundary(line, column);
            if (endColumn == column)
            {
                return false;
            }

            editableLines[CaretLineIndex] = line.Remove(column, endColumn - column);
        }
        else if (CaretLineIndex < editableLines.Count - 1)
        {
            editableLines[CaretLineIndex] = line + editableLines[CaretLineIndex + 1];
            editableLines.RemoveAt(CaretLineIndex + 1);
        }
        else
        {
            return false;
        }

        CaretColumn = Math.Clamp(column, 0, editableLines[CaretLineIndex].Length);
        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool IndentCurrentLine()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        editableLines[CaretLineIndex] = new string(' ', EditorTabSize) + editableLines[CaretLineIndex];
        CaretColumn += EditorTabSize;
        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool OutdentCurrentLine()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        var (line, removed) = RemoveIndent(editableLines[CaretLineIndex]);
        if (removed == 0)
        {
            return false;
        }

        editableLines[CaretLineIndex] = line;
        CaretColumn = Math.Max(0, CaretColumn - Math.Min(CaretColumn, removed));
        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool DuplicateCurrentLine()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        var line = editableLines[CaretLineIndex];
        editableLines.Insert(CaretLineIndex + 1, line);
        CaretLineIndex++;
        CaretColumn = Math.Clamp(CaretColumn, 0, line.Length);
        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public bool DeleteCurrentLine()
    {
        if (!IsEditingActive)
        {
            return false;
        }

        EnsureEditableLines();
        if (CaretLineIndex < 0 || CaretLineIndex >= editableLines!.Count)
        {
            return false;
        }

        editableLines.RemoveAt(CaretLineIndex);
        if (editableLines.Count == 0)
        {
            editableLines.Add(string.Empty);
        }

        CaretLineIndex = Math.Clamp(CaretLineIndex, 0, editableLines.Count - 1);
        CaretColumn = Math.Clamp(CaretColumn, 0, editableLines[CaretLineIndex].Length);
        RebuildEditedDocument();
        EnsureCaretVisible();
        return true;
    }

    public ImmutableArray<DiffNodeVisibleLine> GetVisibleRows(int firstRowIndex = 0, int count = int.MaxValue)
    {
        if (!IsShowingFullFile || FoldRegions.IsDefaultOrEmpty)
        {
            return GetFlatVisibleRows(firstRowIndex, count);
        }

        var normalizedFirstRow = Math.Max(0, firstRowIndex);
        var remainingRows = Math.Max(0, GetVisibleLineCount() - normalizedFirstRow);
        var requestedCount = count == int.MaxValue
            ? remainingRows
            : Math.Min(remainingRows, Math.Max(0, count));
        if (requestedCount == 0)
        {
            return [];
        }

        var rows = ImmutableArray.CreateBuilder<DiffNodeVisibleLine>(requestedCount);
        var foldRegions = GetFoldRegionsByStartLineIndex();
        var rowIndex = 0;
        for (var lineIndex = 0; lineIndex < Document.LineCount; lineIndex++)
        {
            var line = Document.Lines[lineIndex];
            foldRegions.TryGetValue(line.Index, out var foldRegion);
            var isCollapsed = foldRegion is not null && collapsedFoldStartLines.Contains(foldRegion.StartLineIndex);
            if (rowIndex >= normalizedFirstRow && rows.Count < requestedCount)
            {
                rows.Add(new DiffNodeVisibleLine(rowIndex, line.Index, line, foldRegion, isCollapsed, GetActiveFoldRegions(line.Index)));
                if (rows.Count == requestedCount)
                {
                    break;
                }
            }

            rowIndex++;
            if (foldRegion is not null && isCollapsed)
            {
                lineIndex = Math.Max(lineIndex, foldRegion.EndLineIndex);
            }
        }

        return rows.ToImmutable();
    }

    public Rect2 GetScrollbarThumbBounds(double cameraScale)
    {
        var body = BodyBounds;
        var contentHeight = Math.Max(1, VisibleLineCount * LineHeight);
        if (contentHeight <= body.Height)
        {
            return Rect2.Empty;
        }

        var trackInset = DiffCanvasScene.ScreenStableWorldLength(cameraScale, 3);
        var thumbWidth = DiffCanvasScene.ScreenStableWorldLength(cameraScale, 6);
        var minThumbHeight = DiffCanvasScene.ScreenStableWorldLength(cameraScale, 24);
        var thumbHeight = Math.Max(minThumbHeight, body.Height * body.Height / contentHeight);
        thumbHeight = Math.Min(body.Height, thumbHeight);
        var trackHeight = Math.Max(0, body.Height - thumbHeight);
        var thumbTop = body.Top;
        if (trackHeight > 0)
        {
            thumbTop += ScrollOffsetY / MaxScrollOffset * trackHeight;
        }

        return new Rect2(body.Right - thumbWidth - trackInset, thumbTop, thumbWidth, thumbHeight);
    }

    public Rect2 GetFontSizeButtonBounds(DiffNodeFontSizeAction action, double cameraScale)
    {
        if (!CanShowFontSizeButtons(cameraScale))
        {
            return Rect2.Empty;
        }

        var buttonSize = DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlButtonScreenSize);
        var gap = DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlGapScreenSize);
        var rightInset = Math.Max(
            DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlRightInsetScreenSize),
            FontControlLineCountInset + DiffCanvasScene.ScreenStableWorldLength(cameraScale, FontControlLineCountGapScreenSize));
        var plusRight = Bounds.Right - rightInset;
        var buttonLeft = action == DiffNodeFontSizeAction.Increase
            ? plusRight - buttonSize
            : plusRight - buttonSize * 2 - gap;
        var top = Bounds.Top + (TitleHeight - buttonSize) / 2;
        return new Rect2(buttonLeft, top, buttonSize, buttonSize);
    }

    public bool CanShowFontSizeButtons(double cameraScale)
    {
        var scale = Math.Max(cameraScale, 0.01);
        return Bounds.Width * scale >= FontControlMinimumNodeScreenWidth &&
            TitleHeight * scale >= FontControlMinimumTitleScreenHeight;
    }

    private static double NormalizeFontSize(double fontSize) => double.IsFinite(fontSize)
        ? Math.Clamp(fontSize, MinFontSize, MaxFontSize)
        : DefaultFontSize;

    public void ScrollToLine(int lineNumber)
    {
        var targetLineIndex = Math.Clamp(lineNumber - 1, 0, Math.Max(0, Document.LineCount - 1));
        for (var index = 0; index < Document.Lines.Length; index++)
        {
            var line = Document.Lines[index];
            if (line.OldLineNumber == lineNumber || line.NewLineNumber == lineNumber)
            {
                targetLineIndex = index;
                break;
            }
        }

        ScrollToLineIndex(targetLineIndex);
    }

    private ImmutableArray<DiffNodeVisibleLine> GetFlatVisibleRows(int firstRowIndex, int count)
    {
        var start = Math.Clamp(firstRowIndex, 0, Math.Max(0, Document.LineCount));
        var availableRows = Math.Max(0, Document.LineCount - start);
        var requestedCount = count == int.MaxValue
            ? availableRows
            : Math.Min(availableRows, Math.Max(0, count));
        if (requestedCount == 0)
        {
            return [];
        }

        var end = start + requestedCount;
        var builder = ImmutableArray.CreateBuilder<DiffNodeVisibleLine>(requestedCount);
        for (var index = start; index < end; index++)
        {
            var line = Document.Lines[index];
            builder.Add(new DiffNodeVisibleLine(index, line.Index, line, null, false, ImmutableArray<CodeFoldRegion>.Empty));
        }

        return builder.ToImmutable();
    }

    private int GetVisibleLineCount()
    {
        if (!IsShowingFullFile || FoldRegions.IsDefaultOrEmpty)
        {
            return Document.LineCount;
        }

        if (visibleLineCountCache is { } cached)
        {
            return cached;
        }

        var foldRegions = GetFoldRegionsByStartLineIndex();
        var count = 0;
        for (var lineIndex = 0; lineIndex < Document.LineCount; lineIndex++)
        {
            var line = Document.Lines[lineIndex];
            if (foldRegions.TryGetValue(line.Index, out var foldRegion) && collapsedFoldStartLines.Contains(foldRegion.StartLineIndex))
            {
                lineIndex = Math.Max(lineIndex, foldRegion.EndLineIndex);
            }

            count++;
        }

        visibleLineCountCache = count;
        return count;
    }

    private Dictionary<int, CodeFoldRegion> GetFoldRegionsByStartLineIndex()
    {
        if (foldRegionsByStartLineIndex is { } cached)
        {
            return cached;
        }

        var regions = new Dictionary<int, CodeFoldRegion>(FoldRegions.IsDefaultOrEmpty ? 0 : FoldRegions.Length);
        foreach (var region in FoldRegions)
        {
            regions.TryAdd(region.StartLineIndex, region);
        }

        foldRegionsByStartLineIndex = regions;
        return regions;
    }

    private void ApplyDocumentMode()
    {
        Document = IsShowingFullFile ? FullFileDocument! : DiffDocument;
        if (!IsEditingActive)
        {
            IsEditorFocused = false;
        }

        CaretLineIndex = Math.Clamp(CaretLineIndex, 0, Math.Max(0, Document.LineCount - 1));
        CaretColumn = Math.Clamp(CaretColumn, 0, GetEditableLine(CaretLineIndex).Length);
        ClampScrollOffset();
    }

    private ImmutableArray<CodeFoldRegion> GetActiveFoldRegions(int lineIndex)
    {
        if (FoldRegions.IsDefaultOrEmpty)
        {
            return [];
        }

        if (activeFoldRegionsByLineIndex is not null &&
            activeFoldRegionsByLineIndex.TryGetValue(lineIndex, out var cached))
        {
            return cached;
        }

        if ((uint)lineIndex >= (uint)Document.LineCount)
        {
            return [];
        }

        var orderedRegions = GetActiveFoldRegionsByStartLine();
        var builder = ImmutableArray.CreateBuilder<CodeFoldRegion>(Math.Min(orderedRegions.Length, 8));
        foreach (var region in orderedRegions)
        {
            if (region.StartLineIndex >= lineIndex)
            {
                break;
            }

            if (region.EndLineIndex >= lineIndex &&
                !collapsedFoldStartLines.Contains(region.StartLineIndex))
            {
                builder.Add(region);
            }
        }

        var activeRegions = builder.Count == 0 ? ImmutableArray<CodeFoldRegion>.Empty : builder.ToImmutable();
        (activeFoldRegionsByLineIndex ??= new Dictionary<int, ImmutableArray<CodeFoldRegion>>(128))[lineIndex] = activeRegions;
        return activeRegions;
    }

    private CodeFoldRegion[] GetActiveFoldRegionsByStartLine()
    {
        if (activeFoldRegionsByStartLine is { } cached)
        {
            return cached;
        }

        if (FoldRegions.IsDefaultOrEmpty)
        {
            activeFoldRegionsByStartLine = [];
            return activeFoldRegionsByStartLine;
        }

        var orderedRegions = FoldRegions.ToArray();
        Array.Sort(orderedRegions, static (left, right) =>
        {
            var startComparison = left.StartLineIndex.CompareTo(right.StartLineIndex);
            return startComparison != 0
                ? startComparison
                : left.EndLineIndex.CompareTo(right.EndLineIndex);
        });

        activeFoldRegionsByStartLine = orderedRegions;
        return activeFoldRegionsByStartLine;
    }

    private void EnsureEditableLines()
    {
        if (editableLines is null)
        {
            editableLines = new List<string>(Document.Lines.Length);
            foreach (var line in Document.Lines)
            {
                editableLines.Add(line.Text);
            }
        }

        if (editableLines.Count == 0)
        {
            editableLines.Add(string.Empty);
        }

        CaretLineIndex = Math.Clamp(CaretLineIndex, 0, editableLines.Count - 1);
        CaretColumn = Math.Clamp(CaretColumn, 0, editableLines[CaretLineIndex].Length);
    }

    private string GetEditableLine(int lineIndex)
    {
        if (editableLines is not null)
        {
            return lineIndex >= 0 && lineIndex < editableLines.Count ? editableLines[lineIndex] : string.Empty;
        }

        if (lineIndex < 0 || lineIndex >= Document.LineCount)
        {
            return string.Empty;
        }

        return Document.Lines[lineIndex].Text;
    }

    private static int FindPreviousWordBoundary(string line, int column)
    {
        var index = Math.Clamp(column, 0, line.Length);
        while (index > 0 && char.IsWhiteSpace(line[index - 1]))
        {
            index--;
        }

        if (index > 0 && IsWordCharacter(line[index - 1]))
        {
            while (index > 0 && IsWordCharacter(line[index - 1]))
            {
                index--;
            }

            return index;
        }

        while (index > 0 && !char.IsWhiteSpace(line[index - 1]) && !IsWordCharacter(line[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static int FindNextWordBoundary(string line, int column)
    {
        var index = Math.Clamp(column, 0, line.Length);
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        if (index < line.Length && IsWordCharacter(line[index]))
        {
            while (index < line.Length && IsWordCharacter(line[index]))
            {
                index++;
            }

            return index;
        }

        while (index < line.Length && !char.IsWhiteSpace(line[index]) && !IsWordCharacter(line[index]))
        {
            index++;
        }

        return index;
    }

    private static int FindPreviousTextWordBoundary(string text, int offset)
    {
        var index = Math.Clamp(offset, 0, text.Length);
        if (index > 0)
        {
            index--;
        }

        while (index > 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        if (index >= 0 && index < text.Length && IsWordCharacter(text[index]))
        {
            while (index > 0 && IsWordCharacter(text[index - 1]))
            {
                index--;
            }

            return index;
        }

        while (index > 0 && !char.IsWhiteSpace(text[index - 1]) && !IsWordCharacter(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static int FindNextTextWordBoundary(string text, int offset)
    {
        var index = Math.Clamp(offset, 0, text.Length);
        if (index < text.Length && IsWordCharacter(text[index]))
        {
            while (index < text.Length && IsWordCharacter(text[index]))
            {
                index++;
            }

            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }
        else if (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }
        else if (index < text.Length)
        {
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && !IsWordCharacter(text[index]))
            {
                index++;
            }

            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        return index;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static (string Line, int Removed) RemoveIndent(string line)
    {
        if (line.Length == 0)
        {
            return (line, 0);
        }

        if (line[0] == '\t')
        {
            return (line[1..], 1);
        }

        var spaces = 0;
        while (spaces < Math.Min(EditorTabSize, line.Length) && line[spaces] == ' ')
        {
            spaces++;
        }

        return spaces == 0 ? (line, 0) : (line[spaces..], spaces);
    }

    private int GetEditableTextOffset()
    {
        EnsureEditableLines();
        var offset = 0;
        var lines = editableLines!;
        var lineCount = lines.Count;
        var row = Math.Clamp(CaretLineIndex, 0, Math.Max(0, lineCount - 1));
        for (var index = 0; index < row; index++)
        {
            offset += lines[index].Length + 1;
        }

        return offset + Math.Clamp(CaretColumn, 0, lines[row].Length);
    }

    private void SetCaretFromTextOffset(int offset)
    {
        EnsureEditableLines();
        var lines = editableLines!;
        var remaining = Math.Clamp(offset, 0, string.Join('\n', lines).Length);
        for (var row = 0; row < lines.Count; row++)
        {
            var lineLength = lines[row].Length;
            if (remaining <= lineLength)
            {
                CaretLineIndex = row;
                CaretColumn = remaining;
                return;
            }

            remaining -= lineLength + 1;
        }

        CaretLineIndex = Math.Max(0, lines.Count - 1);
        CaretColumn = lines.Count == 0 ? 0 : lines[^1].Length;
    }

    private void MoveCaretByCharacter(int direction)
    {
        var rowIndex = GetVisibleRowIndexForLineIndex(CaretLineIndex);
        if (rowIndex < 0)
        {
            rowIndex = Math.Clamp(CaretLineIndex, 0, Math.Max(0, VisibleLineCount - 1));
            if (TryGetLineIndexAtVisibleRowIndex(rowIndex, out var fallbackLineIndex))
            {
                CaretLineIndex = fallbackLineIndex;
            }
        }

        var line = GetEditableLine(CaretLineIndex);
        if (direction < 0)
        {
            if (CaretColumn > 0)
            {
                CaretColumn--;
                return;
            }

            if (rowIndex > 0)
            {
                if (TryGetLineIndexAtVisibleRowIndex(rowIndex - 1, out var previousLineIndex))
                {
                    CaretLineIndex = previousLineIndex;
                    CaretColumn = GetEditableLine(previousLineIndex).Length;
                }
            }

            return;
        }

        if (CaretColumn < line.Length)
        {
            CaretColumn++;
            return;
        }

        if (rowIndex < VisibleLineCount - 1)
        {
            if (TryGetLineIndexAtVisibleRowIndex(rowIndex + 1, out var nextLineIndex))
            {
                CaretLineIndex = nextLineIndex;
            }

            CaretColumn = 0;
        }
    }

    private void InsertCharacterCore(char character)
    {
        var line = editableLines![CaretLineIndex];
        var column = Math.Clamp(CaretColumn, 0, line.Length);
        editableLines[CaretLineIndex] = line.Insert(column, character.ToString());
        CaretColumn = column + 1;
    }

    private void InsertNewLineCore()
    {
        var line = editableLines![CaretLineIndex];
        var column = Math.Clamp(CaretColumn, 0, line.Length);
        editableLines[CaretLineIndex] = line[..column];
        editableLines.Insert(CaretLineIndex + 1, line[column..]);
        CaretLineIndex++;
        CaretColumn = 0;
    }

    private void RebuildEditedDocument()
    {
        if (editableLines is null)
        {
            return;
        }

        var builder = ImmutableArray.CreateBuilder<DiffLine>(editableLines.Count);
        for (var index = 0; index < editableLines.Count; index++)
        {
            var lineNumber = index + 1;
            builder.Add(new DiffLine(index, lineNumber, lineNumber, DiffLineKind.Context, editableLines[index], ImmutableArray<TokenSpan>.Empty));
        }

        var nextDocument = new DiffDocumentSnapshot(DiffDocument.Id, DiffDocument.Metadata, builder.ToImmutable());
        FullFileDocument = nextDocument;
        FullText = string.Join(Environment.NewLine, editableLines);
        Document = nextDocument;
        activeFoldRegionsByLineIndex = null;
        visibleLineCountCache = null;
    }

    private void EnsureCaretVisible()
    {
        var rowIndex = GetVisibleRowIndexForLineIndex(CaretLineIndex);
        if (rowIndex < 0)
        {
            return;
        }

        var top = rowIndex * LineHeight;
        var bottom = top + LineHeight;
        if (top < ScrollOffsetY)
        {
            SetScrollOffset(top);
        }
        else if (bottom > ScrollOffsetY + BodyBounds.Height)
        {
            SetScrollOffset(bottom - BodyBounds.Height);
        }
    }

    private int GetVisibleRowIndexForLineIndex(int targetLineIndex)
    {
        if (targetLineIndex < 0 || targetLineIndex >= Document.LineCount)
        {
            return -1;
        }

        if (!IsShowingFullFile || FoldRegions.IsDefaultOrEmpty)
        {
            return targetLineIndex;
        }

        var foldRegions = GetFoldRegionsByStartLineIndex();
        var rowIndex = 0;
        for (var lineIndex = 0; lineIndex < Document.LineCount; lineIndex++)
        {
            var line = Document.Lines[lineIndex];
            if (line.Index == targetLineIndex)
            {
                return rowIndex;
            }

            if (foldRegions.TryGetValue(line.Index, out var foldRegion) && collapsedFoldStartLines.Contains(foldRegion.StartLineIndex))
            {
                if (targetLineIndex <= foldRegion.EndLineIndex)
                {
                    return rowIndex;
                }

                lineIndex = Math.Max(lineIndex, foldRegion.EndLineIndex);
            }

            rowIndex++;
        }

        return -1;
    }

    private void ScrollToLineIndex(int targetLineIndex)
    {
        var rowIndex = GetVisibleRowIndexForLineIndex(targetLineIndex);
        if (rowIndex < 0)
        {
            rowIndex = Math.Clamp(targetLineIndex, 0, Math.Max(0, VisibleLineCount - 1));
        }

        var bodyHeight = BodyBounds.Height;
        ScrollOffsetY = Math.Clamp(rowIndex * LineHeight - bodyHeight * 0.35, 0, MaxScrollOffset);
    }

    private bool TryGetLineIndexAtVisibleRowIndex(int targetRowIndex, out int lineIndex)
    {
        lineIndex = -1;
        if (targetRowIndex < 0 || targetRowIndex >= VisibleLineCount || Document.LineCount == 0)
        {
            return false;
        }

        if (!IsShowingFullFile || FoldRegions.IsDefaultOrEmpty)
        {
            lineIndex = Document.Lines[targetRowIndex].Index;
            return true;
        }

        var foldRegions = GetFoldRegionsByStartLineIndex();
        var rowIndex = 0;
        for (var documentLineIndex = 0; documentLineIndex < Document.LineCount; documentLineIndex++)
        {
            var line = Document.Lines[documentLineIndex];
            if (rowIndex == targetRowIndex)
            {
                lineIndex = line.Index;
                return true;
            }

            if (foldRegions.TryGetValue(line.Index, out var foldRegion) && collapsedFoldStartLines.Contains(foldRegion.StartLineIndex))
            {
                documentLineIndex = Math.Max(documentLineIndex, foldRegion.EndLineIndex);
            }

            rowIndex++;
        }

        return false;
    }

    private static int IndexOfVisibleLine(ImmutableArray<DiffNodeVisibleLine> rows, int lineIndex)
    {
        for (var index = 0; index < rows.Length; index++)
        {
            if (rows[index].LineIndex == lineIndex)
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed record DiffNodeVisibleLine(
    int RowIndex,
    int LineIndex,
    DiffLine Line,
    CodeFoldRegion? FoldRegion,
    bool IsFoldCollapsed,
    ImmutableArray<CodeFoldRegion> ActiveFoldRegions);

public sealed record DiffNodeFullFileContent(
    DiffDocumentId DocumentId,
    DiffDocumentSnapshot FullFileDocument,
    ImmutableArray<CodeFoldRegion> FoldRegions,
    string FullText);

public enum DiffNodeFontSizeAction
{
    Decrease,
    Increase
}

public enum DiffNodeSelectionMode
{
    Replace,
    Add,
    Toggle
}

public enum DiffNodeSelectionScope
{
    Direct,
    Connected,
    Incoming,
    Outgoing
}

public enum DiffNodeResizeHandle
{
    None,
    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public sealed record DiffNodeViewState(
    DiffDocumentId DocumentId,
    Rect2 Bounds,
    double ScrollOffsetY,
    bool IsSelected,
    bool IsPinned,
    double FontSize,
    bool? FullFileViewOverride = null,
    bool? EditingOverride = null,
    int CaretLineIndex = 0,
    int CaretColumn = 0);

public sealed record DiffCanvasSceneViewState(CameraState Camera, ImmutableArray<DiffNodeViewState> Nodes)
{
    public string? SelectedDocumentId
    {
        get
        {
            foreach (var node in Nodes)
            {
                if (node.IsSelected)
                {
                    return node.DocumentId.Value;
                }
            }

            return null;
        }
    }
}

public sealed record DiffAnnotationHit(DiffNode Node, DiffAnnotation Annotation);

public sealed record GraphEdge(string SourceNodeId, string TargetNodeId, SemanticEdgeKind Kind, double Confidence, string? Label, int BundleCount = 1);

public sealed record GraphGroup(
    string Id,
    GraphGroupingMode Mode,
    string Label,
    Rect2 Bounds,
    int DocumentCount,
    int AddedLines,
    int DeletedLines,
    int ColorIndex,
    ImmutableArray<DiffDocumentId> DocumentIds = default)
{
    public string SummaryText => DocumentCount == 1 ? Label : $"{Label} ({DocumentCount:N0})";
}

public sealed record EdgeProjectionOptions(
    double MinimumConfidence = 0.65,
    int MaxEdgesPerDocumentPair = 1,
    bool BundleParallelEdges = true,
    ImmutableHashSet<SemanticEdgeKind>? IncludedEdgeKinds = null);

public sealed class DiffCanvasScene
{
    public const double ResizeHandleScreenSize = 10;
    private const double WheelZoomFactorPerNotch = 1.25;

    private readonly List<DiffNode> nodes;
    private readonly List<GraphEdge> edges;
    private ImmutableArray<GraphGroup> groups;
    private readonly ImmutableArray<DiffAnnotation> annotations;
    private Dictionary<LineAnnotationKey, DiffAnnotation[]>? lineAnnotationsByTarget;
    private Dictionary<DiffDocumentId, DiffAnnotation>? primaryNodeAnnotationsByDocumentId;
    private Dictionary<DiffDocumentId, DiffNode>? nodesByDocumentId;
    private DiffNode? focusedEditorNode;
    private int geometryVersion;
    private int graphBoundsCacheVersion = -1;
    private Rect2 graphBoundsCache;

    public DiffCanvasScene(
        IEnumerable<DiffNode> nodes,
        IEnumerable<GraphEdge> edges,
        ImmutableArray<GraphGroup> groups = default,
        ImmutableArray<DiffAnnotation> annotations = default,
        DiffAnnotationVisibilityState? annotationVisibility = null)
    {
        this.nodes = nodes.ToList();
        this.edges = edges.ToList();
        this.groups = groups.IsDefault ? ImmutableArray<GraphGroup>.Empty : groups;
        this.annotations = annotations.IsDefault ? ImmutableArray<DiffAnnotation>.Empty : annotations;
        AnnotationVisibility = annotationVisibility ?? DiffAnnotationVisibilityState.Default;
    }

    public IReadOnlyList<DiffNode> Nodes => nodes;

    public IEnumerable<DiffNode> SelectedNodes => EnumerateSelectedNodes();

    public int SelectedNodeCount
    {
        get
        {
            var count = 0;
            foreach (var node in nodes)
            {
                if (node.IsSelected)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private IEnumerable<DiffNode> EnumerateSelectedNodes()
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected)
            {
                yield return node;
            }
        }
    }

    public bool HasFocusedEditor => GetFocusedEditorNode() is not null;

    public IReadOnlyList<GraphEdge> Edges => edges;

    public ImmutableArray<GraphGroup> Groups => groups;

    public ImmutableArray<DiffAnnotation> Annotations => annotations;

    public DiffAnnotationVisibilityState AnnotationVisibility { get; }

    public string? HoveredAnnotationId { get; private set; }

    public int GeometryVersion => geometryVersion;

    public bool ShowFullFileNodes { get; private set; }

    public bool EnableNodeEditing { get; private set; }

    public CameraState Camera { get; private set; } = CameraState.Default;

    public Rect2 GraphBounds
    {
        get
        {
            if (graphBoundsCacheVersion != geometryVersion)
            {
                graphBoundsCache = CalculateGraphBounds(nodes, groups);
                graphBoundsCacheVersion = geometryVersion;
            }

            return graphBoundsCache;
        }
    }

    public static double ScreenStableWorldLength(double cameraScale, double screenPixels) => screenPixels / Math.Max(cameraScale, 0.01);

    private static Rect2 CalculateGraphBounds(IReadOnlyList<DiffNode> nodes, ImmutableArray<GraphGroup> groups)
    {
        var hasBounds = false;
        var left = 0d;
        var top = 0d;
        var right = 0d;
        var bottom = 0d;

        foreach (var node in nodes)
        {
            IncludeBounds(node.Bounds, ref hasBounds, ref left, ref top, ref right, ref bottom);
        }

        foreach (var group in groups)
        {
            IncludeBounds(group.Bounds, ref hasBounds, ref left, ref top, ref right, ref bottom);
        }

        return hasBounds ? new Rect2(left, top, right - left, bottom - top) : Rect2.Empty;
    }

    private static void IncludeBounds(Rect2 bounds, ref bool hasBounds, ref double left, ref double top, ref double right, ref double bottom)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        if (!hasBounds)
        {
            left = bounds.Left;
            top = bounds.Top;
            right = bounds.Right;
            bottom = bounds.Bottom;
            hasBounds = true;
            return;
        }

        left = Math.Min(left, bounds.Left);
        top = Math.Min(top, bounds.Top);
        right = Math.Max(right, bounds.Right);
        bottom = Math.Max(bottom, bounds.Bottom);
    }

    public void Pan(double deltaX, double deltaY) => Camera = Camera.Pan(deltaX, deltaY);

    public void ZoomAt(Point2 screenPoint, double zoomFactor) => Camera = Camera.ZoomAt(screenPoint, zoomFactor);

    public Rect2 GetViewportWorldBounds(Size2 viewportSize) =>
        CanvasViewportScrollbarCalculator.GetWorldViewport(Camera, viewportSize);

    public CanvasViewportScrollbarSet GetViewportScrollbars(Size2 viewportSize) =>
        CanvasViewportScrollbarCalculator.Calculate(GraphBounds, Camera, viewportSize);

    public void SetViewportWorldOrigin(double? worldLeft, double? worldTop)
    {
        Camera = Camera with
        {
            OffsetX = worldLeft is double left ? -left * Camera.Scale : Camera.OffsetX,
            OffsetY = worldTop is double top ? -top * Camera.Scale : Camera.OffsetY
        };
    }

    public void SetFullFileDocuments(IEnumerable<DiffNodeFullFileContent> fullFileContents)
    {
        var contentsByDocumentId = new Dictionary<DiffDocumentId, DiffNodeFullFileContent>();
        foreach (var content in fullFileContents)
        {
            contentsByDocumentId[content.DocumentId] = content;
        }

        var changed = false;
        foreach (var node in nodes)
        {
            if (!contentsByDocumentId.TryGetValue(node.DiffDocument.Id, out var content))
            {
                continue;
            }

            node.SetFullFileDocument(content.FullFileDocument, content.FoldRegions, content.FullText);
            node.ApplyWorkspaceMode(ShowFullFileNodes, EnableNodeEditing);
            changed = true;
        }

        if (changed)
        {
            geometryVersion++;
        }
    }

    public void SetShowFullFileNodes(bool showFullFile)
    {
        if (ShowFullFileNodes == showFullFile)
        {
            return;
        }

        ShowFullFileNodes = showFullFile;
        foreach (var node in nodes)
        {
            node.ApplyWorkspaceMode(ShowFullFileNodes, EnableNodeEditing);
        }

        geometryVersion++;
    }

    public void SetNodeEditingEnabled(bool enableEditing)
    {
        if (EnableNodeEditing == enableEditing)
        {
            return;
        }

        EnableNodeEditing = enableEditing;
        foreach (var node in nodes)
        {
            node.ApplyWorkspaceMode(ShowFullFileNodes, EnableNodeEditing);
        }

        geometryVersion++;
    }

    public bool ToggleNodeFullFileView(string documentId)
    {
        var node = FindNode(documentId);
        if (node is null || !node.HasFullFileDocument)
        {
            return false;
        }

        node.ToggleFullFileOverride();
        geometryVersion++;
        return true;
    }

    public bool ClearNodeFullFileViewOverride(string documentId)
    {
        var node = FindNode(documentId);
        if (node is null || node.FullFileViewOverride is null)
        {
            return false;
        }

        node.ClearFullFileOverride();
        geometryVersion++;
        return true;
    }

    public bool ToggleNodeEditing(string documentId)
    {
        var node = FindNode(documentId);
        if (node is null || !node.HasFullFileDocument)
        {
            return false;
        }

        var nextEditing = !node.IsEditingActive;
        if (nextEditing && !node.IsShowingFullFile)
        {
            node.SetFullFileOverride(true);
        }

        node.SetEditingOverride(nextEditing);
        geometryVersion++;
        return true;
    }

    public bool ClearNodeEditingOverride(string documentId)
    {
        var node = FindNode(documentId);
        if (node is null || node.EditingOverride is null)
        {
            return false;
        }

        node.ClearEditingOverride();
        geometryVersion++;
        return true;
    }

    public bool ToggleFoldAt(Point2 screenPoint)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var node = HitTestNode(screenPoint);
        if (node is null || !node.TryHitTestFold(worldPoint, out var startLineIndex))
        {
            return false;
        }

        var changed = node.ToggleFold(startLineIndex);
        if (changed)
        {
            geometryVersion++;
        }

        return changed;
    }

    public bool TryFocusEditorAt(Point2 screenPoint)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var targetNode = HitTestNode(screenPoint);
        var changed = false;
        foreach (var node in nodes)
        {
            var focus = ReferenceEquals(node, targetNode) && node.TrySetCaretFromWorldPoint(worldPoint);
            if (node.IsEditorFocused != focus)
            {
                SetNodeEditorFocus(node, focus);
                changed = true;
            }
        }

        if (changed || targetNode is not null)
        {
            geometryVersion++;
            return targetNode?.IsEditorFocused == true;
        }

        return false;
    }

    public bool MoveFocusedEditorCaret(int lineDelta, int columnDelta)
    {
        var node = GetFocusedEditorNode();
        if (node?.MoveCaret(lineDelta, columnDelta) != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool MoveFocusedEditorCaretWord(int direction)
    {
        var node = GetFocusedEditorNode();
        if (node?.MoveCaretWord(direction) != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool SetFocusedEditorCaretColumn(int column)
    {
        var node = GetFocusedEditorNode();
        if (node?.SetCaretColumn(column) != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool MoveFocusedEditorCaretToDocumentStart()
    {
        var node = GetFocusedEditorNode();
        if (node?.MoveCaretToDocumentStart() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool MoveFocusedEditorCaretToDocumentEnd()
    {
        var node = GetFocusedEditorNode();
        if (node?.MoveCaretToDocumentEnd() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool MoveFocusedEditorCaretToSmartLineStart()
    {
        var node = GetFocusedEditorNode();
        if (node?.MoveCaretToSmartLineStart() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool InsertTextInFocusedEditor(string text)
    {
        var node = GetFocusedEditorNode();
        if (node?.InsertText(text) != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool InsertNewLineInFocusedEditor()
    {
        var node = GetFocusedEditorNode();
        if (node?.InsertNewLine() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool BackspaceInFocusedEditor()
    {
        var node = GetFocusedEditorNode();
        if (node?.Backspace() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool BackspaceWordInFocusedEditor()
    {
        var node = GetFocusedEditorNode();
        if (node?.BackspaceWord() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool DeleteInFocusedEditor()
    {
        var node = GetFocusedEditorNode();
        if (node?.Delete() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool DeleteWordInFocusedEditor()
    {
        var node = GetFocusedEditorNode();
        if (node?.DeleteWord() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool IndentFocusedEditorLine()
    {
        var node = GetFocusedEditorNode();
        if (node?.IndentCurrentLine() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool OutdentFocusedEditorLine()
    {
        var node = GetFocusedEditorNode();
        if (node?.OutdentCurrentLine() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool DuplicateFocusedEditorLine()
    {
        var node = GetFocusedEditorNode();
        if (node?.DuplicateCurrentLine() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public bool DeleteFocusedEditorLine()
    {
        var node = GetFocusedEditorNode();
        if (node?.DeleteCurrentLine() != true)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public void HandleWheel(Point2 screenPoint, double wheelDelta, bool zoomCanvas)
        => HandleWheel(screenPoint, wheelDelta, zoomCanvas, panCanvasWhenUnhandled: false);

    public void HandleWheel(Point2 screenPoint, double wheelDelta, bool zoomCanvas, bool panCanvasWhenUnhandled)
    {
        if (!zoomCanvas && TryScrollNodeAt(screenPoint, -wheelDelta * 0.6))
        {
            return;
        }

        if (!zoomCanvas)
        {
            if (panCanvasWhenUnhandled)
            {
                Pan(0, wheelDelta * 0.6);
            }

            return;
        }

        var wheelNotches = Math.Clamp(wheelDelta / 120.0, -6.0, 6.0);
        var zoomFactor = Math.Pow(WheelZoomFactorPerNotch, wheelNotches);
        ZoomAt(screenPoint, zoomFactor);
    }

    public void FitToGraph(Size2 viewportSize) => Camera = CameraState.Fit(GraphBounds, viewportSize, 48);

    public void FitToNode(DiffNode node, Size2 viewportSize) => Camera = CameraState.Fit(node.Bounds, viewportSize, 64);

    public DiffNode? HitTestNode(Point2 screenPoint)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);

        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            if (nodes[nodeIndex].Bounds.Contains(worldPoint))
            {
                return nodes[nodeIndex];
            }
        }

        return null;
    }

    private DiffNode? FindNode(string documentId) =>
        GetNodesByDocumentId().TryGetValue(new DiffDocumentId(documentId), out var node) ? node : null;

    public bool TryHitTestResizeHandle(Point2 screenPoint, out DiffNode? node, out DiffNodeResizeHandle handle)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var handleSize = ScreenStableWorldLength(Camera.Scale, ResizeHandleScreenSize);

        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (!candidate.Bounds.Inflate(handleSize).Contains(worldPoint))
            {
                continue;
            }

            var scrollbarThumb = candidate.GetScrollbarThumbBounds(Camera.Scale);
            if (!scrollbarThumb.IsEmpty && scrollbarThumb.Inflate(handleSize * 0.35).Contains(worldPoint))
            {
                continue;
            }

            handle = GetResizeHandle(candidate.Bounds, worldPoint, handleSize);
            if (handle != DiffNodeResizeHandle.None)
            {
                node = candidate;
                return true;
            }
        }

        node = null;
        handle = DiffNodeResizeHandle.None;
        return false;
    }

    public bool TryHitTestTitleBar(Point2 screenPoint, out DiffNode? node)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (candidate.TitleBounds.Contains(worldPoint))
            {
                node = candidate;
                return true;
            }
        }

        node = null;
        return false;
    }

    public bool TryHitTestScrollbarThumb(Point2 screenPoint, out DiffNode? node, out double thumbGrabOffsetY)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var hitPadding = ScreenStableWorldLength(Camera.Scale, 3);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            var thumb = candidate.GetScrollbarThumbBounds(Camera.Scale);
            if (!thumb.IsEmpty && thumb.Inflate(hitPadding).Contains(worldPoint))
            {
                node = candidate;
                thumbGrabOffsetY = worldPoint.Y - thumb.Top;
                return true;
            }
        }

        node = null;
        thumbGrabOffsetY = 0;
        return false;
    }

    public bool TryHitTestFontSizeButton(Point2 screenPoint, out DiffNode? node, out DiffNodeFontSizeAction action)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (candidate.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Decrease, Camera.Scale).Contains(worldPoint))
            {
                node = candidate;
                action = DiffNodeFontSizeAction.Decrease;
                return true;
            }

            if (candidate.GetFontSizeButtonBounds(DiffNodeFontSizeAction.Increase, Camera.Scale).Contains(worldPoint))
            {
                node = candidate;
                action = DiffNodeFontSizeAction.Increase;
                return true;
            }
        }

        node = null;
        action = DiffNodeFontSizeAction.Decrease;
        return false;
    }

    public bool TryHitTestAnnotation(Point2 screenPoint, out DiffAnnotationHit? hit)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        for (var nodeIndex = nodes.Count - 1; nodeIndex >= 0; nodeIndex--)
        {
            var candidate = nodes[nodeIndex];
            if (!candidate.Bounds.Contains(worldPoint))
            {
                continue;
            }

            if (TryHitTestLineAnnotation(candidate, worldPoint, out var lineAnnotation))
            {
                hit = new DiffAnnotationHit(candidate, lineAnnotation);
                return true;
            }

            if (TryHitTestNodeAnnotation(candidate, worldPoint, out var nodeAnnotation))
            {
                hit = new DiffAnnotationHit(candidate, nodeAnnotation);
                return true;
            }
        }

        hit = null;
        return false;
    }

    public bool TryHitTestGroup(Point2 screenPoint, out GraphGroup? group)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        for (var groupIndex = groups.Length - 1; groupIndex >= 0; groupIndex--)
        {
            var candidate = groups[groupIndex];
            if (candidate.Bounds.Contains(worldPoint))
            {
                group = candidate;
                return true;
            }
        }

        group = null;
        return false;
    }

    public GraphGroup? FindGroupForNode(DiffNode node)
    {
        GraphGroup? bestGroup = null;
        var bestArea = double.PositiveInfinity;

        foreach (var group in groups)
        {
            if (!GroupContainsNode(group, node))
            {
                continue;
            }

            var area = group.Bounds.Width * group.Bounds.Height;
            if (area < bestArea)
            {
                bestGroup = group;
                bestArea = area;
            }
        }

        return bestGroup;
    }

    public bool SetHoveredAnnotation(DiffAnnotation? annotation)
    {
        var nextId = annotation?.Id;
        if (string.Equals(HoveredAnnotationId, nextId, StringComparison.Ordinal))
        {
            return false;
        }

        HoveredAnnotationId = nextId;
        return true;
    }

    public void SelectNode(DiffNode? selectedNode)
    {
        foreach (var node in nodes)
        {
            SetNodeSelected(node, ReferenceEquals(node, selectedNode));
        }
    }

    public void ClearSelection() => SelectNode(null);

    public void ClearSelectionAndEditorFocus()
    {
        foreach (var node in nodes)
        {
            node.IsSelected = false;
            SetNodeEditorFocus(node, false);
        }
    }

    public void AddNodeToSelection(DiffNode node)
    {
        node.IsSelected = true;
    }

    public void ToggleNodeSelection(DiffNode node)
    {
        SetNodeSelected(node, !node.IsSelected);
    }

    public void SelectNodes(IEnumerable<DiffNode> selectedNodes)
    {
        var selected = selectedNodes.ToHashSet();
        foreach (var node in nodes)
        {
            SetNodeSelected(node, selected.Contains(node));
        }
    }

    public int SelectAllNodes()
    {
        foreach (var node in nodes)
        {
            node.IsSelected = true;
        }

        return nodes.Count;
    }

    public int InvertNodeSelection()
    {
        foreach (var node in nodes)
        {
            SetNodeSelected(node, !node.IsSelected);
        }

        return SelectedNodeCount;
    }

    public int SelectNodesInRect(Rect2 worldRect, DiffNodeSelectionMode mode)
    {
        var matchingNodes = new List<DiffNode>();
        foreach (var node in nodes)
        {
            if (node.Bounds.Intersects(worldRect))
            {
                matchingNodes.Add(node);
            }
        }

        ApplySelection(matchingNodes, mode);
        return matchingNodes.Count;
    }

    public int SelectGroupNodes(GraphGroup group, DiffNodeSelectionMode mode)
    {
        var groupNodes = GetGroupNodeList(group);
        ApplySelection(groupNodes, mode);
        return groupNodes.Count;
    }

    public int SelectConnectedNodes(DiffNode node, DiffNodeSelectionMode mode, DiffNodeSelectionScope scope = DiffNodeSelectionScope.Connected)
    {
        var documentId = node.DiffDocument.Id.Value;
        var selectedIds = new HashSet<string>(StringComparer.Ordinal) { documentId };
        foreach (var edge in edges)
        {
            var sourceMatches = string.Equals(edge.SourceNodeId, documentId, StringComparison.Ordinal);
            var targetMatches = string.Equals(edge.TargetNodeId, documentId, StringComparison.Ordinal);
            var includeOutgoing = scope is DiffNodeSelectionScope.Connected or DiffNodeSelectionScope.Outgoing;
            var includeIncoming = scope is DiffNodeSelectionScope.Connected or DiffNodeSelectionScope.Incoming;
            if ((includeOutgoing && sourceMatches) || (includeIncoming && targetMatches))
            {
                selectedIds.Add(edge.SourceNodeId);
                selectedIds.Add(edge.TargetNodeId);
            }
        }

        var connectedNodes = new List<DiffNode>(selectedIds.Count);
        foreach (var candidate in nodes)
        {
            if (selectedIds.Contains(candidate.DiffDocument.Id.Value))
            {
                connectedNodes.Add(candidate);
            }
        }

        ApplySelection(connectedNodes, mode);
        return connectedNodes.Count;
    }

    public void MoveNode(DiffNode node, double deltaX, double deltaY)
    {
        node.Bounds = node.Bounds.Translate(deltaX, deltaY);
        node.IsPinned = true;
        geometryVersion++;
    }

    public bool MoveSelectedNodes(double deltaX, double deltaY)
    {
        if (Math.Abs(deltaX) < double.Epsilon && Math.Abs(deltaY) < double.Epsilon)
        {
            return false;
        }

        var moved = false;
        foreach (var node in nodes)
        {
            if (node.IsSelected)
            {
                node.Bounds = node.Bounds.Translate(deltaX, deltaY);
                node.IsPinned = true;
                moved = true;
            }
        }

        if (!moved)
        {
            return false;
        }

        geometryVersion++;
        return true;
    }

    public void MoveNodeTo(DiffNode node, double x, double y)
    {
        node.Bounds = new Rect2(x, y, node.Bounds.Width, node.Bounds.Height);
        node.IsPinned = true;
        geometryVersion++;
    }

    public GraphGroup? MoveGroup(GraphGroup group, double deltaX, double deltaY)
    {
        if (Math.Abs(deltaX) < double.Epsilon && Math.Abs(deltaY) < double.Epsilon)
        {
            return group;
        }

        var groupIndex = -1;
        for (var index = 0; index < groups.Length; index++)
        {
            if (string.Equals(groups[index].Id, group.Id, StringComparison.Ordinal))
            {
                groupIndex = index;
                break;
            }
        }

        if (groupIndex < 0)
        {
            return null;
        }

        foreach (var node in GetGroupNodeList(groups[groupIndex]))
        {
            node.Bounds = node.Bounds.Translate(deltaX, deltaY);
            node.IsPinned = true;
        }

        var movedGroup = groups[groupIndex] with { Bounds = groups[groupIndex].Bounds.Translate(deltaX, deltaY) };
        groups = groups.SetItem(groupIndex, movedGroup);
        geometryVersion++;
        return movedGroup;
    }

    public void ResizeNode(DiffNode node, DiffNodeResizeHandle handle, double deltaX, double deltaY)
    {
        if (handle == DiffNodeResizeHandle.None)
        {
            return;
        }

        var left = node.Bounds.Left;
        var top = node.Bounds.Top;
        var right = node.Bounds.Right;
        var bottom = node.Bounds.Bottom;

        if (handle is DiffNodeResizeHandle.Left or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.BottomLeft)
        {
            left += deltaX;
        }

        if (handle is DiffNodeResizeHandle.Right or DiffNodeResizeHandle.TopRight or DiffNodeResizeHandle.BottomRight)
        {
            right += deltaX;
        }

        if (handle is DiffNodeResizeHandle.Top or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.TopRight)
        {
            top += deltaY;
        }

        if (handle is DiffNodeResizeHandle.Bottom or DiffNodeResizeHandle.BottomLeft or DiffNodeResizeHandle.BottomRight)
        {
            bottom += deltaY;
        }

        if (right - left < DiffNode.MinWidth)
        {
            if (handle is DiffNodeResizeHandle.Left or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.BottomLeft)
            {
                left = right - DiffNode.MinWidth;
            }
            else
            {
                right = left + DiffNode.MinWidth;
            }
        }

        if (bottom - top < DiffNode.MinHeight)
        {
            if (handle is DiffNodeResizeHandle.Top or DiffNodeResizeHandle.TopLeft or DiffNodeResizeHandle.TopRight)
            {
                top = bottom - DiffNode.MinHeight;
            }
            else
            {
                bottom = top + DiffNode.MinHeight;
            }
        }

        node.Bounds = new Rect2(left, top, right - left, bottom - top);
        node.IsPinned = true;
        node.ClampScrollOffset();
        geometryVersion++;
    }

    public bool TryScrollNodeAt(Point2 screenPoint, double deltaY)
    {
        var worldPoint = Camera.ScreenToWorld(screenPoint);
        var node = HitTestNode(screenPoint);

        if (node is null || !node.BodyBounds.Contains(worldPoint))
        {
            return false;
        }

        node.ScrollBy(deltaY);
        return true;
    }

    public void DragScrollbarThumb(DiffNode node, double worldY, double thumbGrabOffsetY)
    {
        var body = node.BodyBounds;
        var thumb = node.GetScrollbarThumbBounds(Camera.Scale);
        if (thumb.IsEmpty || node.MaxScrollOffset <= 0)
        {
            return;
        }

        var trackHeight = Math.Max(0, body.Height - thumb.Height);
        if (trackHeight <= 0)
        {
            node.SetScrollOffset(0);
            return;
        }

        var thumbTop = Math.Clamp(worldY - thumbGrabOffsetY, body.Top, body.Bottom - thumb.Height);
        var scrollRatio = (thumbTop - body.Top) / trackHeight;
        node.SetScrollOffset(node.MaxScrollOffset * scrollRatio);
    }

    public void AdjustNodeFontSize(DiffNode node, DiffNodeFontSizeAction action)
    {
        if (action == DiffNodeFontSizeAction.Increase)
        {
            node.IncreaseFontSize();
        }
        else
        {
            node.DecreaseFontSize();
        }

        node.IsPinned = true;
    }

    public void TogglePinned(DiffNode node) => node.IsPinned = !node.IsPinned;

    public DiffCanvasSceneViewState CaptureViewState()
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeViewState>(nodes.Count);
        foreach (var node in nodes)
        {
            builder.Add(new DiffNodeViewState(
                node.DiffDocument.Id,
                node.Bounds,
                node.ScrollOffsetY,
                node.IsSelected,
                node.IsPinned,
                node.FontSize,
                node.FullFileViewOverride,
                node.EditingOverride,
                node.CaretLineIndex,
                node.CaretColumn));
        }

        return new DiffCanvasSceneViewState(Camera, builder.ToImmutable());
    }

    public void ApplyViewState(DiffCanvasSceneViewState viewState)
    {
        Camera = viewState.Camera;
        var nodesByDocumentId = new Dictionary<DiffDocumentId, DiffNodeViewState>(viewState.Nodes.Length);
        foreach (var nodeState in viewState.Nodes)
        {
            nodesByDocumentId[nodeState.DocumentId] = nodeState;
        }

        foreach (var node in nodes)
        {
            if (!nodesByDocumentId.TryGetValue(node.Document.Id, out var nodeState))
            {
                node.IsSelected = false;
                continue;
            }

            node.Bounds = NormalizeBounds(nodeState.Bounds);
            node.IsSelected = nodeState.IsSelected;
            node.IsPinned = nodeState.IsPinned;
            node.SetFontSize(nodeState.FontSize);
            node.RestoreEditorState(nodeState.FullFileViewOverride, nodeState.EditingOverride, nodeState.CaretLineIndex, nodeState.CaretColumn);
            node.RestoreScrollOffset(nodeState.ScrollOffsetY);
        }

        geometryVersion++;
    }

    private static DiffNodeResizeHandle GetResizeHandle(Rect2 bounds, Point2 worldPoint, double handleSize)
    {
        var nearLeft = Math.Abs(worldPoint.X - bounds.Left) <= handleSize && worldPoint.Y >= bounds.Top - handleSize && worldPoint.Y <= bounds.Bottom + handleSize;
        var nearRight = Math.Abs(worldPoint.X - bounds.Right) <= handleSize && worldPoint.Y >= bounds.Top - handleSize && worldPoint.Y <= bounds.Bottom + handleSize;
        var nearTop = Math.Abs(worldPoint.Y - bounds.Top) <= handleSize && worldPoint.X >= bounds.Left - handleSize && worldPoint.X <= bounds.Right + handleSize;
        var nearBottom = Math.Abs(worldPoint.Y - bounds.Bottom) <= handleSize && worldPoint.X >= bounds.Left - handleSize && worldPoint.X <= bounds.Right + handleSize;

        return (nearLeft, nearTop, nearRight, nearBottom) switch
        {
            (true, true, _, _) => DiffNodeResizeHandle.TopLeft,
            (_, true, true, _) => DiffNodeResizeHandle.TopRight,
            (true, _, _, true) => DiffNodeResizeHandle.BottomLeft,
            (_, _, true, true) => DiffNodeResizeHandle.BottomRight,
            (true, _, _, _) => DiffNodeResizeHandle.Left,
            (_, true, _, _) => DiffNodeResizeHandle.Top,
            (_, _, true, _) => DiffNodeResizeHandle.Right,
            (_, _, _, true) => DiffNodeResizeHandle.Bottom,
            _ => DiffNodeResizeHandle.None
        };
    }

    private static Rect2 NormalizeBounds(Rect2 bounds) => new(
        bounds.X,
        bounds.Y,
        Math.Max(DiffNode.MinWidth, bounds.Width),
        Math.Max(DiffNode.MinHeight, bounds.Height));

    private void ApplySelection(IReadOnlyCollection<DiffNode> selectedNodes, DiffNodeSelectionMode mode)
    {
        if (mode == DiffNodeSelectionMode.Replace)
        {
            SelectNodes(selectedNodes);
            return;
        }

        if (mode == DiffNodeSelectionMode.Add)
        {
            foreach (var node in selectedNodes)
            {
                node.IsSelected = true;
            }

            return;
        }

        foreach (var node in selectedNodes)
        {
            SetNodeSelected(node, !node.IsSelected);
        }
    }

    private void SetNodeSelected(DiffNode node, bool isSelected)
    {
        node.IsSelected = isSelected;
        if (!isSelected)
        {
            SetNodeEditorFocus(node, false);
        }
    }

    private DiffNode? GetFocusedEditorNode()
    {
        if (focusedEditorNode?.IsEditorFocused == true)
        {
            return focusedEditorNode;
        }

        focusedEditorNode = null;
        foreach (var node in nodes)
        {
            if (node.IsEditorFocused)
            {
                focusedEditorNode = node;
                return node;
            }
        }

        return null;
    }

    private void SetNodeEditorFocus(DiffNode node, bool isFocused)
    {
        node.SetEditorFocus(isFocused);
        if (node.IsEditorFocused)
        {
            if (!ReferenceEquals(focusedEditorNode, node) && focusedEditorNode is { } previousFocusedNode)
            {
                previousFocusedNode.SetEditorFocus(false);
            }

            focusedEditorNode = node;
            return;
        }

        if (ReferenceEquals(focusedEditorNode, node))
        {
            focusedEditorNode = null;
        }
    }

    public ImmutableArray<DiffNodeLayout> GetCurrentLayout()
    {
        var builder = ImmutableArray.CreateBuilder<DiffNodeLayout>(nodes.Count);
        foreach (var node in nodes)
        {
            builder.Add(new DiffNodeLayout(node.DiffDocument.Id, node.Bounds, node.IsPinned, node.FontSize));
        }

        return builder.ToImmutable();
    }

    public void ApplyLayout(GraphLayoutResult layoutResult)
    {
        if (layoutResult.Nodes.IsDefaultOrEmpty)
        {
            return;
        }

        var layoutByDocumentId = new Dictionary<DiffDocumentId, DiffNodeLayout>(layoutResult.Nodes.Length);
        foreach (var layoutNode in layoutResult.Nodes)
        {
            layoutByDocumentId[layoutNode.DocumentId] = layoutNode;
        }

        foreach (var node in nodes)
        {
            if (!layoutByDocumentId.TryGetValue(node.DiffDocument.Id, out var layoutNode))
            {
                continue;
            }

            node.Bounds = NormalizeBounds(layoutNode.Bounds);
            node.IsPinned = layoutNode.IsPinned;
            node.SetFontSize(layoutNode.FontSize);
            node.ClampScrollOffset();
        }

        RefreshGroupBounds();
        geometryVersion++;
    }

    public ImmutableHashSet<DiffDocumentId> GetPinnedDocumentIds()
    {
        var builder = ImmutableHashSet.CreateBuilder<DiffDocumentId>();
        foreach (var node in nodes)
        {
            if (node.IsPinned)
            {
                builder.Add(node.DiffDocument.Id);
            }
        }

        return builder.ToImmutable();
    }

    private bool TryHitTestLineAnnotation(DiffNode node, Point2 worldPoint, out DiffAnnotation annotation)
    {
        annotation = default!;
        var body = node.BodyBounds;
        if (!body.Contains(worldPoint) || node.Document.Lines.IsDefaultOrEmpty)
        {
            return false;
        }

        var rowIndex = (int)Math.Floor((worldPoint.Y - body.Top + node.ScrollOffsetY) / node.LineHeight);
        var rows = node.GetVisibleRows(rowIndex, 1);
        if (rows.Length == 0)
        {
            return false;
        }

        var lineIndex = rows[0].LineIndex;
        if (!TryGetVisibleLineAnnotationAt(node.Document.Id, lineIndex, 0, out var primary))
        {
            return false;
        }

        var lineTop = body.Top + rows[0].RowIndex * node.LineHeight - node.ScrollOffsetY;
        var relativeY = worldPoint.Y - lineTop;
        var markerZoneLeft = body.Right - 132;
        var markerIndex = (int)Math.Floor((relativeY - 4) / 9);
        var isInMarkerDot = markerIndex >= 0 &&
            markerIndex < 4 &&
            relativeY >= 4 + markerIndex * 9 &&
            relativeY <= 11 + markerIndex * 9 &&
            worldPoint.X >= body.Right - 18 &&
            worldPoint.X <= body.Right - 5;
        if (isInMarkerDot && TryGetVisibleLineAnnotationAt(node.Document.Id, lineIndex, markerIndex, out var markerAnnotation))
        {
            annotation = markerAnnotation;
            return true;
        }

        var isInteractiveBand = primary.Kind is DiffAnnotationKind.Navigation or DiffAnnotationKind.ParserDiagnostic or DiffAnnotationKind.Conflict or DiffAnnotationKind.Impact or DiffAnnotationKind.ReviewComment;
        if (worldPoint.X >= markerZoneLeft || isInteractiveBand)
        {
            annotation = primary;
            return true;
        }

        return false;
    }

    private bool TryHitTestNodeAnnotation(DiffNode node, Point2 worldPoint, out DiffAnnotation annotation)
    {
        annotation = default!;
        if (worldPoint.Y < node.Bounds.Bottom - DiffNode.FooterHeight ||
            worldPoint.X < node.Bounds.Right - 260)
        {
            return false;
        }

        return TryGetPrimaryNodeAnnotation(node.Document.Id, out annotation);
    }

    private bool TryGetVisibleLineAnnotationAt(DiffDocumentId documentId, int lineIndex, int orderedIndex, out DiffAnnotation annotation)
    {
        annotation = default!;
        if (orderedIndex is < 0 or >= 4)
        {
            return false;
        }

        var lineAnnotations = GetLineAnnotations(documentId, lineIndex);
        if (orderedIndex >= lineAnnotations.Length)
        {
            return false;
        }

        annotation = lineAnnotations[orderedIndex];
        return true;
    }

    private DiffAnnotation[] GetLineAnnotations(DiffDocumentId documentId, int lineIndex)
    {
        var key = new LineAnnotationKey(documentId, lineIndex);
        return GetLineAnnotationsByTarget().TryGetValue(key, out var lineAnnotations)
            ? lineAnnotations
            : Array.Empty<DiffAnnotation>();
    }

    private Dictionary<LineAnnotationKey, DiffAnnotation[]> GetLineAnnotationsByTarget()
    {
        if (lineAnnotationsByTarget is not null)
        {
            return lineAnnotationsByTarget;
        }

        var grouped = new Dictionary<LineAnnotationKey, List<AnnotationIndexEntry>>();
        for (var annotationIndex = 0; annotationIndex < annotations.Length; annotationIndex++)
        {
            var annotation = annotations[annotationIndex];
            if (annotation.Target != DiffAnnotationTarget.Line ||
                annotation.LineIndex is not { } lineIndex ||
                !AnnotationVisibility.IsVisible(annotation.Kind))
            {
                continue;
            }

            var key = new LineAnnotationKey(annotation.DocumentId, lineIndex);
            if (!grouped.TryGetValue(key, out var entries))
            {
                entries = [];
                grouped[key] = entries;
            }

            entries.Add(new AnnotationIndexEntry(annotation, annotationIndex));
        }

        var result = new Dictionary<LineAnnotationKey, DiffAnnotation[]>(grouped.Count);
        foreach (var (key, entries) in grouped)
        {
            entries.Sort(CompareAnnotationEntries);
            var orderedAnnotations = new DiffAnnotation[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                orderedAnnotations[index] = entries[index].Annotation;
            }

            result[key] = orderedAnnotations;
        }

        lineAnnotationsByTarget = result;
        return result;
    }

    private bool TryGetPrimaryNodeAnnotation(DiffDocumentId documentId, out DiffAnnotation annotation)
    {
        return GetPrimaryNodeAnnotationsByDocumentId().TryGetValue(documentId, out annotation!);
    }

    private Dictionary<DiffDocumentId, DiffAnnotation> GetPrimaryNodeAnnotationsByDocumentId()
    {
        if (primaryNodeAnnotationsByDocumentId is not null)
        {
            return primaryNodeAnnotationsByDocumentId;
        }

        var bestByDocumentId = new Dictionary<DiffDocumentId, AnnotationIndexEntry>();
        for (var annotationIndex = 0; annotationIndex < annotations.Length; annotationIndex++)
        {
            var annotation = annotations[annotationIndex];
            if (annotation.Target != DiffAnnotationTarget.Node ||
                !AnnotationVisibility.IsVisible(annotation.Kind))
            {
                continue;
            }

            var entry = new AnnotationIndexEntry(annotation, annotationIndex);
            if (!bestByDocumentId.TryGetValue(annotation.DocumentId, out var existing) ||
                CompareAnnotationEntries(entry, existing) < 0)
            {
                bestByDocumentId[annotation.DocumentId] = entry;
            }
        }

        var result = new Dictionary<DiffDocumentId, DiffAnnotation>(bestByDocumentId.Count);
        foreach (var (documentId, entry) in bestByDocumentId)
        {
            result[documentId] = entry.Annotation;
        }

        primaryNodeAnnotationsByDocumentId = result;
        return result;
    }

    private static int CompareAnnotationEntries(AnnotationIndexEntry left, AnnotationIndexEntry right)
    {
        var priority = AnnotationPriority(left.Annotation).CompareTo(AnnotationPriority(right.Annotation));
        if (priority != 0)
        {
            return priority;
        }

        var idComparison = string.CompareOrdinal(left.Annotation.Id, right.Annotation.Id);
        return idComparison != 0
            ? idComparison
            : left.Index.CompareTo(right.Index);
    }

    private static int AnnotationPriority(DiffAnnotation annotation) => AnnotationPriority(annotation.Kind);

    private static int AnnotationPriority(DiffAnnotationKind kind) => kind switch
    {
        DiffAnnotationKind.Conflict => 0,
        DiffAnnotationKind.ParserDiagnostic => 1,
        DiffAnnotationKind.Navigation => 2,
        DiffAnnotationKind.Impact => 3,
        DiffAnnotationKind.ReviewComment => 4,
        DiffAnnotationKind.MovedCode => 5,
        DiffAnnotationKind.ReviewNoise => 6,
        DiffAnnotationKind.SemanticAnchor => 7,
        DiffAnnotationKind.HistoryBlame => 8,
        DiffAnnotationKind.GitStatus => 9,
        _ => 10
    };

    private readonly record struct LineAnnotationKey(DiffDocumentId DocumentId, int LineIndex);

    private readonly record struct AnnotationIndexEntry(DiffAnnotation Annotation, int Index);

    public DiffCanvasScene WithAnnotations(ImmutableArray<DiffAnnotation> nextAnnotations, DiffAnnotationVisibilityState annotationVisibility)
    {
        var nextScene = new DiffCanvasScene(nodes, edges, groups, nextAnnotations, annotationVisibility)
        {
            Camera = Camera,
            HoveredAnnotationId = HoveredAnnotationId,
            ShowFullFileNodes = ShowFullFileNodes,
            EnableNodeEditing = EnableNodeEditing,
            geometryVersion = geometryVersion
        };
        return nextScene;
    }

    public static DiffCanvasScene FromDocuments(
        ImmutableArray<DiffDocumentSnapshot> documents,
        SemanticGraph? semanticGraph = null,
        GraphLayoutResult? layoutResult = null,
        EdgeProjectionOptions? edgeOptions = null,
        ImmutableArray<DiffAnnotation> annotations = default,
        DiffAnnotationVisibilityState? annotationVisibility = null,
        GraphGroupingMode groupingMode = GraphGroupingMode.Folder)
    {
        var nodeWidth = 620.0;
        var nodeHeight = 420.0;
        Dictionary<DiffDocumentId, DiffNodeLayout>? layoutByDocumentId = null;
        if (layoutResult is { Nodes.IsDefaultOrEmpty: false })
        {
            layoutByDocumentId = new Dictionary<DiffDocumentId, DiffNodeLayout>(layoutResult.Nodes.Length);
            foreach (var layoutNode in layoutResult.Nodes)
            {
                layoutByDocumentId[layoutNode.DocumentId] = layoutNode;
            }
        }

        var nodes = new DiffNode[documents.Length];
        var fallbackColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(documents.Length)));
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            if (layoutByDocumentId is not null && layoutByDocumentId.TryGetValue(document.Id, out var layoutNode))
            {
                var bounds = layoutNode.Bounds;
                nodes[index] = new DiffNode(document, bounds.Width > 0 && bounds.Height > 0 ? bounds : bounds with { Width = nodeWidth, Height = nodeHeight }, layoutNode.IsPinned, layoutNode.FontSize);
                continue;
            }

            var column = index % fallbackColumns;
            var row = index / fallbackColumns;
            nodes[index] = new DiffNode(document, new Rect2(column * 700, row * 500, nodeWidth, nodeHeight));
        }

        var documentIds = new HashSet<string>(documents.Length, StringComparer.Ordinal);
        foreach (var document in documents)
        {
            documentIds.Add(document.Id.Value);
        }

        Dictionary<string, SemanticAnchor> anchorsById = [];
        if (semanticGraph is not null)
        {
            anchorsById = new Dictionary<string, SemanticAnchor>(semanticGraph.Anchors.Length, StringComparer.Ordinal);
            foreach (var anchor in semanticGraph.Anchors)
            {
                anchorsById[anchor.Id] = anchor;
            }
        }
        var options = edgeOptions ?? new EdgeProjectionOptions();
        var projectedEdges = new List<GraphEdge>();
        if (semanticGraph is not null)
        {
            foreach (var semanticEdge in semanticGraph.Edges)
            {
                if (!IsIncluded(semanticEdge, options))
                {
                    continue;
                }

                if (TryCreateGraphEdge(semanticEdge, anchorsById, documentIds) is { } graphEdge)
                {
                    projectedEdges.Add(graphEdge);
                }
            }
        }

        var edges = BundleEdges(projectedEdges, options);
        var groups = BuildGroups(groupingMode, nodes, semanticGraph);

        return new DiffCanvasScene(nodes, edges, groups, annotations, annotationVisibility);
    }

    private static ImmutableArray<GraphGroup> BuildGroups(GraphGroupingMode groupingMode, IReadOnlyList<DiffNode> nodes, SemanticGraph? semanticGraph)
    {
        if (groupingMode == GraphGroupingMode.None || nodes.Count == 0)
        {
            return [];
        }

        Dictionary<DiffDocumentId, List<SemanticAnchor>>? anchorsByDocumentId = null;
        if (semanticGraph is { Anchors.IsDefaultOrEmpty: false })
        {
            anchorsByDocumentId = new Dictionary<DiffDocumentId, List<SemanticAnchor>>();
            foreach (var anchor in semanticGraph.Anchors)
            {
                if (!anchorsByDocumentId.TryGetValue(anchor.DocumentId, out var anchors))
                {
                    anchors = [];
                    anchorsByDocumentId[anchor.DocumentId] = anchors;
                }

                anchors.Add(anchor);
            }
        }

        var groupedNodes = new Dictionary<GraphGroupKey, List<DiffNode>>();
        foreach (var node in nodes)
        {
            IReadOnlyList<SemanticAnchor> anchors = anchorsByDocumentId is not null && anchorsByDocumentId.TryGetValue(node.DiffDocument.Id, out var documentAnchors)
                ? documentAnchors
                : Array.Empty<SemanticAnchor>();
            var key = CreateGroupKey(groupingMode, node.DiffDocument, anchors);
            if (string.IsNullOrWhiteSpace(key.Id))
            {
                continue;
            }

            if (!groupedNodes.TryGetValue(key, out var groupNodes))
            {
                groupNodes = [];
                groupedNodes[key] = groupNodes;
            }

            groupNodes.Add(node);
        }

        var groups = new List<GraphGroup>(groupedNodes.Count);
        foreach (var group in groupedNodes)
        {
            if (group.Value.Count >= 2)
            {
                groups.Add(CreateGraphGroup(groupingMode, group.Key, group.Value));
            }
        }

        groups.Sort(CompareGraphGroups);
        return groups.ToImmutableArray();
    }

    private static GraphGroup CreateGraphGroup(GraphGroupingMode mode, GraphGroupKey key, IReadOnlyList<DiffNode> nodes)
    {
        var hasBounds = false;
        var left = 0d;
        var top = 0d;
        var right = 0d;
        var bottom = 0d;
        var addedLines = 0;
        var deletedLines = 0;
        var documentIds = ImmutableArray.CreateBuilder<DiffDocumentId>(nodes.Count);
        foreach (var node in nodes)
        {
            IncludeBounds(node.Bounds, ref hasBounds, ref left, ref top, ref right, ref bottom);
            addedLines += node.DiffDocument.Metadata.AddedLines;
            deletedLines += node.DiffDocument.Metadata.DeletedLines;
            documentIds.Add(node.DiffDocument.Id);
        }

        var bounds = hasBounds ? ExpandGroupBounds(new Rect2(left, top, right - left, bottom - top)) : Rect2.Empty;
        return new GraphGroup(
            $"{mode}:{key.Id}",
            mode,
            key.Label,
            bounds,
            nodes.Count,
            addedLines,
            deletedLines,
            key.ColorIndex,
            documentIds.ToImmutable());
    }

    private static int CompareGraphGroups(GraphGroup left, GraphGroup right)
    {
        var countComparison = right.DocumentCount.CompareTo(left.DocumentCount);
        return countComparison != 0
            ? countComparison
            : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static Rect2 ExpandGroupBounds(Rect2 bounds) => bounds.IsEmpty
        ? Rect2.Empty
        : new Rect2(bounds.Left - 34, bounds.Top - 48, bounds.Width + 68, bounds.Height + 82);

    private void RefreshGroupBounds()
    {
        if (groups.IsDefaultOrEmpty)
        {
            return;
        }

        var nodesByDocumentId = GetNodesByDocumentId();
        var builder = ImmutableArray.CreateBuilder<GraphGroup>(groups.Length);
        foreach (var group in groups)
        {
            var hasBounds = false;
            var left = 0d;
            var top = 0d;
            var right = 0d;
            var bottom = 0d;

            if (group.DocumentIds.IsDefaultOrEmpty)
            {
                foreach (var node in nodes)
                {
                    if (group.Bounds.Intersects(node.Bounds))
                    {
                        IncludeBounds(node.Bounds, ref hasBounds, ref left, ref top, ref right, ref bottom);
                    }
                }
            }
            else
            {
                foreach (var documentId in group.DocumentIds)
                {
                    if (nodesByDocumentId.TryGetValue(documentId, out var node))
                    {
                        IncludeBounds(node.Bounds, ref hasBounds, ref left, ref top, ref right, ref bottom);
                    }
                }
            }

            builder.Add(hasBounds
                ? group with { Bounds = ExpandGroupBounds(new Rect2(left, top, right - left, bottom - top)) }
                : group);
        }

        groups = builder.ToImmutable();
    }

    private static GraphGroupKey CreateGroupKey(GraphGroupingMode groupingMode, DiffDocumentSnapshot document, IReadOnlyList<SemanticAnchor> anchors) => groupingMode switch
    {
        GraphGroupingMode.Folder => CreateFolderGroupKey(document.Metadata.Path),
        GraphGroupingMode.Semantic => CreateSemanticGroupKey(document, anchors),
        GraphGroupingMode.Language => CreateStableGroupKey($"language:{NormalizeGroupLabel(document.Metadata.Language, "Other")}", NormalizeGroupLabel(document.Metadata.Language, "Other")),
        GraphGroupingMode.Status => CreateStableGroupKey($"status:{document.Metadata.Status}", FormatStatusGroup(document.Metadata.Status)),
        _ => GraphGroupKey.Empty
    };

    private static GraphGroupKey CreateFolderGroupKey(string path)
    {
        var pathSpan = path.AsSpan();
        var segmentIndex = 0;
        if (!TryReadPathSegment(pathSpan, ref segmentIndex, out var firstSegment))
        {
            return CreateStableGroupKey("folder:Repository root", "Repository root");
        }

        if (!TryReadPathSegment(pathSpan, ref segmentIndex, out var secondSegment))
        {
            return CreateStableGroupKey("folder:Repository root", "Repository root");
        }

        var label = IsSourceRoot(firstSegment)
            ? string.Concat(firstSegment.ToString(), "/", secondSegment.ToString())
            : firstSegment.ToString();

        return CreateStableGroupKey($"folder:{label}", label);
    }

    private static bool TryReadPathSegment(ReadOnlySpan<char> path, ref int index, out ReadOnlySpan<char> segment)
    {
        while (index < path.Length && IsPathSeparator(path[index]))
        {
            index++;
        }

        var start = index;
        while (index < path.Length && !IsPathSeparator(path[index]))
        {
            index++;
        }

        segment = path[start..index].Trim();
        return !segment.IsEmpty;
    }

    private static bool IsPathSeparator(char value) => value is '/' or '\\';

    private static GraphGroupKey CreateSemanticGroupKey(DiffDocumentSnapshot document, IReadOnlyList<SemanticAnchor> anchors)
    {
        var path = document.Metadata.Path.Replace('\\', '/');
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);

        if (IsProjectLikeFile(extension, fileName))
        {
            return CreateStableGroupKey("semantic:projects", "Projects");
        }

        if (path.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStableGroupKey("semantic:tests", "Tests");
        }

        var hasXamlAnchor = false;
        var hasResourceAnchor = false;
        var hasCSharpAnchor = false;
        foreach (var anchor in anchors)
        {
            hasXamlAnchor |= anchor.Kind is SemanticAnchorKind.XamlRoot or SemanticAnchorKind.XamlName;
            hasResourceAnchor |= anchor.Kind == SemanticAnchorKind.Resource;
            hasCSharpAnchor |= anchor.Kind is SemanticAnchorKind.Type or SemanticAnchorKind.Member or SemanticAnchorKind.Namespace;
            if (hasXamlAnchor && hasResourceAnchor && hasCSharpAnchor)
            {
                break;
            }
        }

        if (hasXamlAnchor || document.Metadata.Language.Contains("XAML", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStableGroupKey("semantic:xaml", "UI/XAML");
        }

        if (hasResourceAnchor)
        {
            return CreateStableGroupKey("semantic:resources", "Resources");
        }

        if (hasCSharpAnchor || string.Equals(document.Metadata.Language, "C#", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStableGroupKey("semantic:csharp", "C# symbols");
        }

        if (extension is ".md" or ".txt" or ".rst")
        {
            return CreateStableGroupKey("semantic:docs", "Docs");
        }

        if (extension is ".json" or ".xml" or ".config" or ".props" or ".targets" or ".yml" or ".yaml")
        {
            return CreateStableGroupKey("semantic:config", "Config");
        }

        var label = NormalizeGroupLabel(document.Metadata.Language, "Other");
        return CreateStableGroupKey($"semantic:{label}", label);
    }

    private static bool IsProjectLikeFile(string extension, string fileName) =>
        extension is ".csproj" or ".sln" or ".slnx" or ".fsproj" or ".vbproj" ||
        string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceRoot(ReadOnlySpan<char> segment) =>
        segment.Equals("src", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("samples", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("examples", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGroupLabel(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static GraphGroupKey CreateStableGroupKey(string id, string label) => new(id, label, StableColorIndex(id));

    private bool GroupContainsNode(GraphGroup group, DiffNode node)
    {
        if (group.DocumentIds.IsDefaultOrEmpty)
        {
            return group.Bounds.Contains(node.Bounds.Center);
        }

        foreach (var documentId in group.DocumentIds)
        {
            if (documentId == node.DiffDocument.Id)
            {
                return true;
            }
        }

        return false;
    }

    private List<DiffNode> GetGroupNodeList(GraphGroup group)
    {
        var result = new List<DiffNode>(group.DocumentIds.IsDefaultOrEmpty ? 8 : group.DocumentIds.Length);
        if (group.DocumentIds.IsDefaultOrEmpty)
        {
            foreach (var node in nodes)
            {
                if (group.Bounds.Contains(node.Bounds.Center))
                {
                    result.Add(node);
                }
            }

            return result;
        }

        var map = GetNodesByDocumentId();
        foreach (var documentId in group.DocumentIds)
        {
            if (map.TryGetValue(documentId, out var node))
            {
                result.Add(node);
            }
        }

        return result;
    }

    private IReadOnlyDictionary<DiffDocumentId, DiffNode> GetNodesByDocumentId()
    {
        if (nodesByDocumentId is { Count: var count } cached && count == nodes.Count)
        {
            return cached;
        }

        var map = new Dictionary<DiffDocumentId, DiffNode>(nodes.Count);
        foreach (var node in nodes)
        {
            map[node.DiffDocument.Id] = node;
        }

        nodesByDocumentId = map;
        return map;
    }

    private static int StableColorIndex(string value)
    {
        var hash = 17;
        foreach (var character in value)
        {
            hash = unchecked(hash * 31 + character);
        }

        return (hash & int.MaxValue) % 8;
    }

    private static string FormatStatusGroup(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "Added",
        DiffFileStatus.Deleted => "Deleted",
        DiffFileStatus.Renamed => "Renamed",
        DiffFileStatus.Copied => "Copied",
        DiffFileStatus.Untracked => "Untracked",
        DiffFileStatus.Conflicted => "Conflicted",
        DiffFileStatus.Modified => "Modified",
        _ => "Unchanged"
    };

    private static bool IsIncluded(SemanticEdge edge, EdgeProjectionOptions options)
    {
        if (edge.Confidence < options.MinimumConfidence)
        {
            return false;
        }

        return options.IncludedEdgeKinds is null || options.IncludedEdgeKinds.Contains(edge.Kind);
    }

    private static GraphEdge[] BundleEdges(IEnumerable<GraphEdge> edges, EdgeProjectionOptions options)
    {
        var maxPerPair = Math.Max(1, options.MaxEdgesPerDocumentPair);
        if (!options.BundleParallelEdges)
        {
            var selected = new List<GraphEdge>();
            foreach (var edge in edges)
            {
                selected.Add(edge);
            }

            if (selected.Count == 0)
            {
                return [];
            }

            selected.Sort(CompareGraphEdgesByConfidence);
            if (selected.Count <= maxPerPair)
            {
                return selected.ToArray();
            }

            var result = new GraphEdge[maxPerPair];
            selected.CopyTo(0, result, 0, maxPerPair);
            return result;
        }

        var bundleByKind = new Dictionary<GraphEdgeBundleKey, GraphEdgeBundleAccumulator>();
        foreach (var edge in edges)
        {
            var key = new GraphEdgeBundleKey(edge.SourceNodeId, edge.TargetNodeId, edge.Kind);
            if (bundleByKind.TryGetValue(key, out var accumulator))
            {
                accumulator.Add(edge);
            }
            else
            {
                bundleByKind[key] = new GraphEdgeBundleAccumulator(edge);
            }
        }

        if (bundleByKind.Count == 0)
        {
            return [];
        }

        var edgesByPair = new Dictionary<GraphEdgePairKey, List<GraphEdge>>();
        foreach (var accumulator in bundleByKind.Values)
        {
            var edge = accumulator.ToEdge();
            var pairKey = new GraphEdgePairKey(edge.SourceNodeId, edge.TargetNodeId);
            if (!edgesByPair.TryGetValue(pairKey, out var pairEdges))
            {
                pairEdges = [];
                edgesByPair[pairKey] = pairEdges;
            }

            pairEdges.Add(edge);
        }

        var bundledEdges = new List<GraphEdge>(bundleByKind.Count);
        foreach (var pairEdges in edgesByPair.Values)
        {
            pairEdges.Sort(CompareGraphEdgesByConfidence);
            var take = Math.Min(maxPerPair, pairEdges.Count);
            for (var index = 0; index < take; index++)
            {
                bundledEdges.Add(pairEdges[index]);
            }
        }

        return bundledEdges.ToArray();
    }

    private static int CompareGraphEdgesByConfidence(GraphEdge left, GraphEdge right)
    {
        var confidenceComparison = right.Confidence.CompareTo(left.Confidence);
        if (confidenceComparison != 0)
        {
            return confidenceComparison;
        }

        var sourceComparison = string.Compare(left.SourceNodeId, right.SourceNodeId, StringComparison.Ordinal);
        if (sourceComparison != 0)
        {
            return sourceComparison;
        }

        var targetComparison = string.Compare(left.TargetNodeId, right.TargetNodeId, StringComparison.Ordinal);
        if (targetComparison != 0)
        {
            return targetComparison;
        }

        return left.Kind.CompareTo(right.Kind);
    }

    private readonly record struct GraphEdgeBundleKey(string SourceNodeId, string TargetNodeId, SemanticEdgeKind Kind);

    private readonly record struct GraphEdgePairKey(string SourceNodeId, string TargetNodeId);

    private sealed class GraphEdgeBundleAccumulator
    {
        private GraphEdge strongest;
        private int bundleCount;

        public GraphEdgeBundleAccumulator(GraphEdge edge)
        {
            strongest = edge;
            bundleCount = Math.Max(1, edge.BundleCount);
        }

        public void Add(GraphEdge edge)
        {
            bundleCount += Math.Max(1, edge.BundleCount);
            if (edge.Confidence > strongest.Confidence)
            {
                strongest = edge;
            }
        }

        public GraphEdge ToEdge()
        {
            var label = bundleCount > 1 ? $"{bundleCount} semantic links" : strongest.Label;
            return strongest with { Label = label, BundleCount = bundleCount };
        }
    }

    private static GraphEdge? TryCreateGraphEdge(
        SemanticEdge semanticEdge,
        IReadOnlyDictionary<string, SemanticAnchor> anchorsById,
        HashSet<string> documentIds)
    {
        if (!anchorsById.TryGetValue(semanticEdge.SourceAnchorId, out var sourceAnchor) ||
            !anchorsById.TryGetValue(semanticEdge.TargetAnchorId, out var targetAnchor) ||
            sourceAnchor.DocumentId == targetAnchor.DocumentId ||
            !documentIds.Contains(sourceAnchor.DocumentId.Value) ||
            !documentIds.Contains(targetAnchor.DocumentId.Value))
        {
            return null;
        }

        return new GraphEdge(sourceAnchor.DocumentId.Value, targetAnchor.DocumentId.Value, semanticEdge.Kind, semanticEdge.Confidence, semanticEdge.Label);
    }

    private sealed record GraphGroupKey(string Id, string Label, int ColorIndex)
    {
        public static GraphGroupKey Empty { get; } = new(string.Empty, string.Empty, 0);
    }
}
