using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SemanticDiff.App.Views;

public sealed class ResizeCursorGrid : Grid
{
    private readonly InputSystemCursor horizontalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

    public void UseHorizontalResizeCursor()
    {
        ProtectedCursor = horizontalResizeCursor;
    }

    public void ClearCursor()
    {
        ProtectedCursor = null;
    }
}
