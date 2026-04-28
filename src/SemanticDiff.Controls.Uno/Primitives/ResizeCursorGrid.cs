using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SemanticDiff.Controls.Uno;

public sealed class ResizeCursorGrid : Grid
{
    private readonly InputSystemCursor horizontalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private readonly InputSystemCursor verticalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

    public void UseHorizontalResizeCursor()
    {
        ProtectedCursor = horizontalResizeCursor;
    }

    public void UseVerticalResizeCursor()
    {
        ProtectedCursor = verticalResizeCursor;
    }

    public void ClearCursor()
    {
        ProtectedCursor = null;
    }
}
