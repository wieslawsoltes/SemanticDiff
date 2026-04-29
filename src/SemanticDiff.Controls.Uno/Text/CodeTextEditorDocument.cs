using System.Text;

namespace SemanticDiff.Controls.Uno;

internal sealed class CodeTextEditorDocument
{
    private readonly List<string> lines = [string.Empty];
    private readonly Stack<CodeTextEditorSnapshot> undoStack = [];
    private readonly Stack<CodeTextEditorSnapshot> redoStack = [];

    public int Version { get; private set; }

    public int LineCount => lines.Count;

    public bool CanUndo => undoStack.Count > 0;

    public bool CanRedo => redoStack.Count > 0;

    public string Text => string.Join('\n', lines);

    public void SetText(string? text)
    {
        var normalized = NormalizeNewLines(text);
        if (!string.Equals(Text, normalized, StringComparison.Ordinal))
        {
            lines.Clear();
            lines.AddRange(SplitLines(normalized));
            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            Version++;
        }

        undoStack.Clear();
        redoStack.Clear();
    }

    public string GetLine(int lineIndex) =>
        lineIndex >= 0 && lineIndex < lines.Count ? lines[lineIndex] : string.Empty;

    public bool ReplaceLines(int firstLine, int lastLine, IReadOnlyList<string> replacementLines, out CodeTextPosition newCaret)
    {
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        var first = Math.Clamp(firstLine, 0, lines.Count - 1);
        var last = Math.Clamp(lastLine, first, lines.Count - 1);
        var replacement = replacementLines
            .SelectMany(line => SplitLines(line ?? string.Empty))
            .ToList();
        var current = lines.GetRange(first, last - first + 1);
        if (current.SequenceEqual(replacement, StringComparer.Ordinal))
        {
            var row = Math.Clamp(first, 0, Math.Max(0, lines.Count - 1));
            newCaret = new CodeTextPosition(row, 0);
            return false;
        }

        lines.RemoveRange(first, last - first + 1);
        if (replacement.Count > 0)
        {
            lines.InsertRange(Math.Min(first, lines.Count), replacement);
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        var caretRow = replacement.Count > 0
            ? Math.Min(first + replacement.Count - 1, lines.Count - 1)
            : Math.Min(first, lines.Count - 1);
        newCaret = new CodeTextPosition(caretRow, 0);
        Version++;
        return true;
    }

    public CodeTextPosition ClampPosition(CodeTextPosition position)
    {
        var row = Math.Clamp(position.RowIndex, 0, Math.Max(0, lines.Count - 1));
        var column = Math.Clamp(position.Column, 0, lines[row].Length);
        return new CodeTextPosition(row, column);
    }

    public void CaptureUndoState(CodeTextPosition caret, CodeTextPosition? selectionAnchor, CodeTextPosition? selectionActive)
    {
        undoStack.Push(new CodeTextEditorSnapshot(Text, caret, selectionAnchor, selectionActive));
        redoStack.Clear();
    }

    public bool Undo(ref CodeTextPosition caret, ref CodeTextPosition? selectionAnchor, ref CodeTextPosition? selectionActive)
    {
        if (undoStack.Count == 0)
        {
            return false;
        }

        redoStack.Push(new CodeTextEditorSnapshot(Text, caret, selectionAnchor, selectionActive));
        Restore(undoStack.Pop(), ref caret, ref selectionAnchor, ref selectionActive);
        return true;
    }

    public bool Redo(ref CodeTextPosition caret, ref CodeTextPosition? selectionAnchor, ref CodeTextPosition? selectionActive)
    {
        if (redoStack.Count == 0)
        {
            return false;
        }

        undoStack.Push(new CodeTextEditorSnapshot(Text, caret, selectionAnchor, selectionActive));
        Restore(redoStack.Pop(), ref caret, ref selectionAnchor, ref selectionActive);
        return true;
    }

    public bool Replace(CodeTextPosition start, CodeTextPosition end, string? replacement, out CodeTextPosition newCaret)
    {
        start = ClampPosition(start);
        end = ClampPosition(end);
        if (Compare(start, end) > 0)
        {
            (start, end) = (end, start);
        }

        var normalizedReplacement = NormalizeNewLines(replacement);
        if (Compare(start, end) == 0 && normalizedReplacement.Length == 0)
        {
            newCaret = start;
            return false;
        }

        var insertedLines = SplitLines(normalizedReplacement);
        var prefix = lines[start.RowIndex][..start.Column];
        var suffix = lines[end.RowIndex][end.Column..];
        var removeCount = end.RowIndex - start.RowIndex + 1;
        lines.RemoveRange(start.RowIndex, removeCount);

        if (insertedLines.Count == 1)
        {
            var merged = prefix + insertedLines[0] + suffix;
            lines.Insert(start.RowIndex, merged);
            newCaret = new CodeTextPosition(start.RowIndex, prefix.Length + insertedLines[0].Length);
        }
        else
        {
            insertedLines[0] = prefix + insertedLines[0];
            insertedLines[^1] += suffix;
            lines.InsertRange(start.RowIndex, insertedLines);
            newCaret = new CodeTextPosition(
                start.RowIndex + insertedLines.Count - 1,
                insertedLines[^1].Length - suffix.Length);
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
            newCaret = new CodeTextPosition(0, 0);
        }

        Version++;
        return true;
    }

    public string GetText(CodeTextPosition start, CodeTextPosition end)
    {
        start = ClampPosition(start);
        end = ClampPosition(end);
        if (Compare(start, end) > 0)
        {
            (start, end) = (end, start);
        }

        if (Compare(start, end) == 0)
        {
            return string.Empty;
        }

        if (start.RowIndex == end.RowIndex)
        {
            return lines[start.RowIndex][start.Column..end.Column];
        }

        var builder = new StringBuilder();
        builder.Append(lines[start.RowIndex][start.Column..]);
        for (var row = start.RowIndex + 1; row < end.RowIndex; row++)
        {
            builder.Append('\n');
            builder.Append(lines[row]);
        }

        builder.Append('\n');
        builder.Append(lines[end.RowIndex][..end.Column]);
        return builder.ToString();
    }

    public static int Compare(CodeTextPosition left, CodeTextPosition right)
    {
        var rowComparison = left.RowIndex.CompareTo(right.RowIndex);
        return rowComparison != 0 ? rowComparison : left.Column.CompareTo(right.Column);
    }

    private void Restore(CodeTextEditorSnapshot snapshot, ref CodeTextPosition caret, ref CodeTextPosition? selectionAnchor, ref CodeTextPosition? selectionActive)
    {
        lines.Clear();
        lines.AddRange(SplitLines(snapshot.Text));
        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        caret = ClampPosition(snapshot.Caret);
        selectionAnchor = snapshot.SelectionAnchor is { } anchor ? ClampPosition(anchor) : null;
        selectionActive = snapshot.SelectionActive is { } active ? ClampPosition(active) : null;
        Version++;
    }

    private static List<string> SplitLines(string? text)
    {
        var normalized = NormalizeNewLines(text);
        return normalized.Split('\n').ToList();
    }

    private static string NormalizeNewLines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal sealed record CodeTextEditorSnapshot(
    string Text,
    CodeTextPosition Caret,
    CodeTextPosition? SelectionAnchor,
    CodeTextPosition? SelectionActive);
