using System.Collections.Concurrent;
using System.Globalization;
using Pretext;
using Pretext.SkiaSharp;
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
    private const string MeasurementTabReplacement = "    ";
    private static readonly object PretextMeasurementGate = new();
    private static int pretextBackendInitialized;
    private static int pretextMeasurementUnavailable;
    private readonly object gate = new();
    private readonly int maxEntries;
    private readonly int maxEllipsizeEntries;
    private readonly Dictionary<TextMeasureKey, float> measuredWidths = [];
    private readonly Queue<TextMeasureKey> measuredWidthOrder = [];
    private readonly Dictionary<TextEllipsizeKey, string> middleEllipsizedTexts = [];
    private readonly Queue<TextEllipsizeKey> middleEllipsizedTextOrder = [];
    private readonly Dictionary<TextFontDescriptor, string> pretextFontStrings = [];
    private readonly ConcurrentDictionary<TypefaceKey, CachedTypeface> typefaces = [];

    public TextMetricsCache(int maxEntries = 16384)
    {
        this.maxEntries = Math.Max(256, maxEntries);
        maxEllipsizeEntries = Math.Max(128, this.maxEntries / 2);
        EnsurePretextBackend();
    }

    public static TextMetricsCache Shared { get; } = new();

    public float MeasureNaturalWidth(string? text, TextFontDescriptor fontDescriptor)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var measuredText = NormalizeMeasuredText(text);
        var key = new TextMeasureKey(fontDescriptor, measuredText);
        lock (gate)
        {
            if (measuredWidths.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var width = TryMeasureWithPretext(measuredText, fontDescriptor) ??
            MeasureWithFallback(measuredText, fontDescriptor);
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

        var widthKey = QuantizeWidth(maxWidth);
        if (widthKey <= 0)
        {
            return string.Empty;
        }

        var key = new TextEllipsizeKey(fontDescriptor, text, widthKey);
        lock (gate)
        {
            if (middleEllipsizedTexts.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var best = MiddleEllipsizeCore(text, widthKey / 10f, fontDescriptor);
        lock (gate)
        {
            if (!middleEllipsizedTexts.ContainsKey(key))
            {
                middleEllipsizedTexts[key] = best;
                middleEllipsizedTextOrder.Enqueue(key);
                TrimMiddleEllipsizedTexts();
            }
        }

        return best;
    }

    private string MiddleEllipsizeCore(string text, float maxWidth, TextFontDescriptor fontDescriptor)
    {
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

    private static int QuantizeWidth(float maxWidth) =>
        Math.Max(0, (int)MathF.Floor(maxWidth * 10));

    private static void EnsurePretextBackend()
    {
        if (Interlocked.Exchange(ref pretextBackendInitialized, 1) == 1)
        {
            return;
        }

        if (!TryWarmUpSkia())
        {
            Volatile.Write(ref pretextMeasurementUnavailable, 1);
            return;
        }

        PretextLayout.SetTextMeasurerFactory(new SkiaSharpTextMeasurerFactory());
    }

    private static bool TryWarmUpSkia()
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1));
            return surface is not null;
        }
        catch (Exception exception) when (IsRecoverablePretextMeasurementFailure(exception))
        {
            return false;
        }
    }

    private float? TryMeasureWithPretext(string text, TextFontDescriptor fontDescriptor)
    {
        if (Volatile.Read(ref pretextMeasurementUnavailable) == 1)
        {
            return null;
        }

        var pretextFontString = GetPretextFontString(fontDescriptor);
        lock (PretextMeasurementGate)
        {
            if (Volatile.Read(ref pretextMeasurementUnavailable) == 1)
            {
                return null;
            }

            try
            {
                var prepared = PretextLayout.PrepareWithSegments(
                    text,
                    pretextFontString,
                    new PrepareOptions(WhiteSpaceMode.PreWrap));
                return (float)PretextLayout.MeasureNaturalWidth(prepared);
            }
            catch (Exception exception) when (IsRecoverablePretextMeasurementFailure(exception))
            {
                Volatile.Write(ref pretextMeasurementUnavailable, 1);
                return null;
            }
        }
    }

    private string GetPretextFontString(TextFontDescriptor fontDescriptor)
    {
        lock (gate)
        {
            if (pretextFontStrings.TryGetValue(fontDescriptor, out var cached))
            {
                return cached;
            }

            var fontString = fontDescriptor.ToPretextFontString();
            pretextFontStrings[fontDescriptor] = fontString;
            return fontString;
        }
    }

    internal static bool IsRecoverablePretextMeasurementFailure(Exception exception) =>
        exception is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException or TypeLoadException or InvalidOperationException;

    private static float MeasureWithFallback(string text, TextFontDescriptor fontDescriptor)
    {
        var maxLineWidth = 0f;
        var currentLineWidth = 0f;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                maxLineWidth = Math.Max(maxLineWidth, currentLineWidth);
                currentLineWidth = 0;
                continue;
            }

            currentLineWidth += GetFallbackCharacterWidth(character, fontDescriptor);
        }

        return Math.Max(maxLineWidth, currentLineWidth);
    }

    private static float GetFallbackCharacterWidth(char character, TextFontDescriptor fontDescriptor)
    {
        var size = Math.Max(1, fontDescriptor.Size);
        var weightFactor = fontDescriptor.Bold ? 1.06f : 1f;
        var widthFactor = character switch
        {
            ' ' => 0.34f,
            '.' or ',' or ':' or ';' or '\'' or '"' or '`' => 0.32f,
            'i' or 'l' or 'I' or '!' or '|' => 0.36f,
            'm' or 'w' or 'M' or 'W' => 0.82f,
            _ when char.IsDigit(character) => 0.58f,
            _ when char.IsUpper(character) => 0.66f,
            _ => 0.57f
        };

        return size * widthFactor * weightFactor;
    }

    private static string NormalizeMeasuredText(string text) =>
        text.Contains('\t', StringComparison.Ordinal)
            ? text.Replace("\t", MeasurementTabReplacement, StringComparison.Ordinal)
            : text;

    internal SKTypeface GetTypeface(TextFontDescriptor fontDescriptor)
    {
        var key = new TypefaceKey(fontDescriptor.FamilyName, fontDescriptor.Bold, fontDescriptor.Italic);
        return typefaces.GetOrAdd(key, static typefaceKey =>
        {
            var weight = typefaceKey.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            var slant = typefaceKey.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
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
            middleEllipsizedTexts.Clear();
            middleEllipsizedTextOrder.Clear();
            pretextFontStrings.Clear();
        }
    }

    public void Dispose()
    {
        Clear();
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

    private void TrimMiddleEllipsizedTexts()
    {
        while (middleEllipsizedTextOrder.Count > maxEllipsizeEntries)
        {
            middleEllipsizedTexts.Remove(middleEllipsizedTextOrder.Dequeue());
        }
    }

    private readonly record struct TextMeasureKey(TextFontDescriptor FontDescriptor, string Text);

    private readonly record struct TextEllipsizeKey(TextFontDescriptor FontDescriptor, string Text, int MaxWidthTenths);

    private readonly record struct TypefaceKey(string FamilyName, bool Bold, bool Italic);

    private readonly record struct CachedTypeface(SKTypeface Typeface, bool OwnsTypeface);
}
