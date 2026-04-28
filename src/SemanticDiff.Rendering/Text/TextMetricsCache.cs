using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace SemanticDiff.Rendering;

public readonly record struct TextFontDescriptor(string FamilyName, float Size, bool Bold = false, bool Italic = false)
{
    public string ToPretextFontString()
    {
        var style = Italic ? "italic " : string.Empty;
        var weight = Bold ? "bold " : string.Empty;
        var family = FamilyName.Contains(' ', StringComparison.Ordinal) ? $"\"{FamilyName}\"" : FamilyName;
        return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2:0.###}px {3}", style, weight, Size, family);
    }
}

public sealed class TextMetricsCache : IDisposable
{
    private const string TextEllipsis = "...";
    private readonly object gate = new();
    private readonly int maxEntries;
    private readonly Dictionary<TextMeasureKey, float> measuredWidths = [];
    private readonly Queue<TextMeasureKey> measuredWidthOrder = [];
    private readonly ConcurrentDictionary<TypefaceKey, CachedTypeface> typefaces = [];
    private readonly ConcurrentDictionary<TextFontDescriptor, CachedFont> fonts = [];

    public TextMetricsCache(int maxEntries = 16384)
    {
        this.maxEntries = Math.Max(256, maxEntries);
    }

    public static TextMetricsCache Shared { get; } = new();

    public float MeasureNaturalWidth(string? text, TextFontDescriptor fontDescriptor)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var key = new TextMeasureKey(fontDescriptor, text);
        lock (gate)
        {
            if (measuredWidths.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var width = MeasureWithSkia(text, fontDescriptor);
        lock (gate)
        {
            if (!measuredWidths.ContainsKey(key))
            {
                measuredWidths[key] = width;
                measuredWidthOrder.Enqueue(key);
                TrimMeasuredWidths();
            }
        }

        return width;
    }

    public float MeasureMonospaceAdvance(TextFontDescriptor fontDescriptor) =>
        Math.Max(1, MeasureNaturalWidth("M", fontDescriptor));

    public string MiddleEllipsize(string text, float maxWidth, TextFontDescriptor fontDescriptor)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }

        if (MeasureNaturalWidth(text, fontDescriptor) <= maxWidth)
        {
            return text;
        }

        if (MeasureNaturalWidth(TextEllipsis, fontDescriptor) > maxWidth)
        {
            return string.Empty;
        }

        var best = TextEllipsis;
        var low = 0;
        var high = text.Length - 1;
        while (low <= high)
        {
            var keepCount = low + (high - low) / 2;
            var prefixCount = keepCount / 2;
            var suffixCount = keepCount - prefixCount;
            var candidate = string.Concat(
                text.AsSpan(0, prefixCount),
                TextEllipsis,
                text.AsSpan(text.Length - suffixCount, suffixCount));

            if (MeasureNaturalWidth(candidate, fontDescriptor) <= maxWidth)
            {
                best = candidate;
                low = keepCount + 1;
            }
            else
            {
                high = keepCount - 1;
            }
        }

        return best;
    }

    private float MeasureWithSkia(string text, TextFontDescriptor fontDescriptor)
    {
        var font = fonts.GetOrAdd(fontDescriptor, descriptor => new CachedFont(GetTypeface(descriptor), descriptor.Size));
        lock (font)
        {
            return font.Font.MeasureText(text);
        }
    }

    internal SKTypeface GetTypeface(TextFontDescriptor fontDescriptor)
    {
        var key = new TypefaceKey(fontDescriptor.FamilyName, fontDescriptor.Bold, fontDescriptor.Italic);
        return typefaces.GetOrAdd(key, static typefaceKey =>
        {
            var style = typefaceKey.Bold ? SKFontStyle.Bold : SKFontStyle.Normal;
            var resolvedTypeface = SKTypeface.FromFamilyName(typefaceKey.FamilyName, style);
            return new CachedTypeface(resolvedTypeface ?? SKTypeface.Default, resolvedTypeface is not null);
        }).Typeface;
    }

    public void Clear()
    {
        lock (gate)
        {
            measuredWidths.Clear();
            measuredWidthOrder.Clear();
        }
    }

    public void Dispose()
    {
        Clear();
        foreach (var font in fonts.Values)
        {
            font.Dispose();
        }

        fonts.Clear();
        foreach (var typeface in typefaces.Values)
        {
            if (typeface.OwnsTypeface)
            {
                typeface.Typeface.Dispose();
            }
        }

        typefaces.Clear();
    }

    private void TrimMeasuredWidths()
    {
        while (measuredWidthOrder.Count > maxEntries)
        {
            measuredWidths.Remove(measuredWidthOrder.Dequeue());
        }
    }

    private sealed class CachedFont : IDisposable
    {
        public CachedFont(SKTypeface typeface, float size)
        {
            Font = new SKFont(typeface, size, 1, 0);
        }

        public SKFont Font { get; }

        public void Dispose()
        {
            Font.Dispose();
        }
    }

    private readonly record struct TextMeasureKey(TextFontDescriptor FontDescriptor, string Text);

    private readonly record struct TypefaceKey(string FamilyName, bool Bold, bool Italic);

    private readonly record struct CachedTypeface(SKTypeface Typeface, bool OwnsTypeface);
}
