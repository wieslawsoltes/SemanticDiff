namespace SemanticDiff.Core;

public readonly record struct Point2(double X, double Y)
{
    public static Point2 Zero { get; } = new(0, 0);

    public Point2 Translate(double deltaX, double deltaY) => new(X + deltaX, Y + deltaY);
}

public readonly record struct Size2(double Width, double Height)
{
    public static Size2 Zero { get; } = new(0, 0);
}

public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public static Rect2 Empty { get; } = new(0, 0, 0, 0);

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Point2 Center => new(X + Width / 2, Y + Height / 2);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(Point2 point) => point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public Rect2 Translate(double deltaX, double deltaY) => new(X + deltaX, Y + deltaY, Width, Height);

    public Rect2 Inflate(double amount) => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public static Rect2 Union(IEnumerable<Rect2> rectangles)
    {
        var hasValue = false;
        var left = 0.0;
        var top = 0.0;
        var right = 0.0;
        var bottom = 0.0;

        foreach (var rectangle in rectangles)
        {
            if (rectangle.IsEmpty)
            {
                continue;
            }

            if (!hasValue)
            {
                left = rectangle.Left;
                top = rectangle.Top;
                right = rectangle.Right;
                bottom = rectangle.Bottom;
                hasValue = true;
                continue;
            }

            left = Math.Min(left, rectangle.Left);
            top = Math.Min(top, rectangle.Top);
            right = Math.Max(right, rectangle.Right);
            bottom = Math.Max(bottom, rectangle.Bottom);
        }

        return hasValue ? new Rect2(left, top, right - left, bottom - top) : Empty;
    }
}