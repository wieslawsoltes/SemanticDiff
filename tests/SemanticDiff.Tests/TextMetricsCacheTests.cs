using Pretext;
using SemanticDiff.Rendering;

namespace SemanticDiff.Tests;

public sealed class TextMetricsCacheTests
{
    [Fact]
    public void MeasureNaturalWidth_ReturnsStablePositiveWidth()
    {
        using var cache = new TextMetricsCache(maxEntries: 512);
        var descriptor = new TextFontDescriptor("Menlo", 15);

        var first = cache.MeasureNaturalWidth("SemanticDiff", descriptor);
        var second = cache.MeasureNaturalWidth("SemanticDiff", descriptor);

        Assert.True(first > 0);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MiddleEllipsize_TrimsFromMiddleWithinAvailableWidth()
    {
        using var cache = new TextMetricsCache(maxEntries: 512);
        var descriptor = new TextFontDescriptor("Menlo", 15);
        const string path = "src/SamplesApp/UITests.Shared/Windows_UI_Xaml_Controls/ScrollViewer/ScrollViewer_Anchoring.xaml";
        var maxWidth = cache.MeasureNaturalWidth(path, descriptor) * 0.45f;

        var trimmed = cache.MiddleEllipsize(path, maxWidth, descriptor);

        Assert.Contains("...", trimmed);
        Assert.StartsWith("src/", trimmed);
        Assert.EndsWith(".xaml", trimmed);
        Assert.True(trimmed.Length < path.Length);
        Assert.True(cache.MeasureNaturalWidth(trimmed, descriptor) <= maxWidth);
    }

    [Fact]
    public void MeasureNaturalWidth_MatchesPretextNaturalWidth()
    {
        using var cache = new TextMetricsCache(maxEntries: 512);
        var descriptor = new TextFontDescriptor("Menlo", 15);
        const string text = "alpha\tbeta\nlonger-gamma";

        var width = cache.MeasureNaturalWidth(text, descriptor);
        try
        {
            var prepared = PretextLayout.PrepareWithSegments(
                "alpha    beta\nlonger-gamma",
                descriptor.ToPretextFontString(),
                new PrepareOptions(WhiteSpaceMode.PreWrap));

            Assert.Equal((float)PretextLayout.MeasureNaturalWidth(prepared), width);
        }
        catch (Exception exception) when (TextMetricsCache.IsRecoverablePretextMeasurementFailure(exception))
        {
            Assert.True(width > 0);
        }
    }

    [Fact]
    public void MeasureNaturalWidth_UsesFourSpaceTabStops()
    {
        using var cache = new TextMetricsCache(maxEntries: 512);
        var descriptor = new TextFontDescriptor("Menlo", 15);

        var tabWidth = cache.MeasureNaturalWidth("alpha\tbeta", descriptor);
        var expandedWidth = cache.MeasureNaturalWidth("alpha    beta", descriptor);

        Assert.Equal(expandedWidth, tabWidth);
    }

    [Fact]
    public void TextFontDescriptor_FormatsPretextCompatibleFontString()
    {
        var descriptor = new TextFontDescriptor("SF Pro Text", 17, Bold: true);

        var font = descriptor.ToPretextFontString();

        Assert.Equal("bold 17px \"SF Pro Text\"", font);
    }
}
