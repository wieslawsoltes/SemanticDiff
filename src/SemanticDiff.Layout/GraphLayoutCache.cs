using System.Collections.Immutable;
using SemanticDiff.Core;
using SemanticDiff.Rendering;

namespace SemanticDiff.Layout;

public readonly record struct GraphLayoutCacheStatistics(
    long PrepareHits,
    long PrepareMisses,
    long MeasureHits,
    long MeasureMisses,
    long ArrangeHits,
    long ArrangeMisses,
    int PreparedEntryCount,
    int MeasureEntryCount,
    int ArrangeEntryCount);

public static class GraphLayoutCacheDiagnostics
{
    public static GraphLayoutCacheStatistics Snapshot() => GraphLayoutCacheStore.Shared.Snapshot();

    public static void Clear() => GraphLayoutCacheStore.Shared.Clear();
}

internal sealed class PreparedGraphLayout
{
    public PreparedGraphLayout(
        GraphLayoutFingerprint fingerprint,
        ImmutableArray<MeasuredGraphNode> nodes)
    {
        Fingerprint = fingerprint;
        Nodes = nodes;
    }

    public GraphLayoutFingerprint Fingerprint { get; }

    public ImmutableArray<MeasuredGraphNode> Nodes { get; }
}

internal readonly record struct MeasuredGraphNode(
    DiffDocumentId DocumentId,
    string Path,
    DiffFileStatus Status,
    Size2 Size);

internal readonly record struct GraphLayoutFingerprint(ulong Hash, ulong SecondaryHash, int DocumentCount, int AnchorCount, int EdgeCount, GraphLayoutMode Mode);

internal sealed class GraphLayoutCacheStore
{
    private const int MaxPreparedLayouts = 96;
    private const int MaxMeasuredNodes = 16384;
    private const int MaxArrangedLayouts = 96;
    private const double HorizontalNodePadding = 96;
    private const double MaximumMeasuredNodeWidth = 1120;
    private const double MaximumMeasuredNodeHeight = 760;
    private static readonly TextFontDescriptor TitleFont = new("Menlo", 15.5f, Bold: true);
    private static readonly TextFontDescriptor DetailFont = new("Menlo", 12.5f);

    private readonly object gate = new();
    private readonly Dictionary<GraphLayoutFingerprint, PreparedGraphLayout> preparedLayouts = [];
    private readonly Queue<GraphLayoutFingerprint> preparedLayoutOrder = [];
    private readonly Dictionary<GraphNodeMeasureKey, Size2> measuredNodes = [];
    private readonly Queue<GraphNodeMeasureKey> measuredNodeOrder = [];
    private readonly Dictionary<GraphLayoutFingerprint, ImmutableArray<DiffNodeLayout>> arrangedLayouts = [];
    private readonly Queue<GraphLayoutFingerprint> arrangedLayoutOrder = [];
    private long prepareHits;
    private long prepareMisses;
    private long measureHits;
    private long measureMisses;
    private long arrangeHits;
    private long arrangeMisses;

    public static GraphLayoutCacheStore Shared { get; } = new();

    public PreparedGraphLayout Prepare(GraphLayoutRequest request, GraphLayoutMode effectiveMode, CancellationToken cancellationToken)
    {
        var fingerprint = CreateFingerprint(request, effectiveMode, cancellationToken);
        lock (gate)
        {
            if (preparedLayouts.TryGetValue(fingerprint, out var cached))
            {
                prepareHits++;
                return cached;
            }

            prepareMisses++;
        }

        var builder = ImmutableArray.CreateBuilder<MeasuredGraphNode>(request.Documents.Length);
        foreach (var document in request.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(new MeasuredGraphNode(
                document.Id,
                document.Metadata.Path,
                document.Metadata.Status,
                MeasureNode(document, request.DefaultNodeSize)));
        }

        var prepared = new PreparedGraphLayout(fingerprint, builder.ToImmutable());
        lock (gate)
        {
            if (!preparedLayouts.TryGetValue(fingerprint, out var cached))
            {
                preparedLayouts[fingerprint] = prepared;
                preparedLayoutOrder.Enqueue(fingerprint);
                TrimPreparedLayouts();
                return prepared;
            }

            return cached;
        }
    }

    public ImmutableArray<DiffNodeLayout> GetOrAddArranged(
        GraphLayoutFingerprint fingerprint,
        Func<ImmutableArray<DiffNodeLayout>> arrange)
    {
        ArgumentNullException.ThrowIfNull(arrange);

        lock (gate)
        {
            if (arrangedLayouts.TryGetValue(fingerprint, out var cached))
            {
                arrangeHits++;
                return cached;
            }

            arrangeMisses++;
        }

        var arranged = arrange();
        lock (gate)
        {
            if (arrangedLayouts.TryGetValue(fingerprint, out var cached))
            {
                return cached;
            }

            if (!arranged.IsDefault)
            {
                arrangedLayouts[fingerprint] = arranged;
                arrangedLayoutOrder.Enqueue(fingerprint);
                TrimArrangedLayouts();
            }
        }

        return arranged;
    }

    public GraphLayoutCacheStatistics Snapshot()
    {
        lock (gate)
        {
            return new GraphLayoutCacheStatistics(
                prepareHits,
                prepareMisses,
                measureHits,
                measureMisses,
                arrangeHits,
                arrangeMisses,
                preparedLayouts.Count,
                measuredNodes.Count,
                arrangedLayouts.Count);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            preparedLayouts.Clear();
            preparedLayoutOrder.Clear();
            measuredNodes.Clear();
            measuredNodeOrder.Clear();
            arrangedLayouts.Clear();
            arrangedLayoutOrder.Clear();
            prepareHits = 0;
            prepareMisses = 0;
            measureHits = 0;
            measureMisses = 0;
            arrangeHits = 0;
            arrangeMisses = 0;
        }
    }

    private Size2 MeasureNode(DiffDocumentSnapshot document, Size2 defaultNodeSize)
    {
        var key = new GraphNodeMeasureKey(
            document.Id.Value,
            document.Metadata.Path,
            document.Metadata.OldPath ?? string.Empty,
            document.Metadata.Language,
            document.Metadata.Status,
            document.Metadata.AddedLines,
            document.Metadata.DeletedLines,
            document.LineCount,
            defaultNodeSize.Width,
            defaultNodeSize.Height);

        lock (gate)
        {
            if (measuredNodes.TryGetValue(key, out var cached))
            {
                measureHits++;
                return cached;
            }

            measureMisses++;
        }

        var measured = MeasureNodeCore(document, defaultNodeSize);
        lock (gate)
        {
            if (measuredNodes.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (measured.Width > 0 && measured.Height > 0)
            {
                measuredNodes[key] = measured;
                measuredNodeOrder.Enqueue(key);
                TrimMeasuredNodes();
            }
        }

        return measured;
    }

    private static Size2 MeasureNodeCore(DiffDocumentSnapshot document, Size2 defaultNodeSize)
    {
        var titleWidth = TextMetricsCache.Shared.MeasureNaturalWidth(document.Metadata.Path, TitleFont);
        var widestTextWidth = titleWidth;
        if (titleWidth + HorizontalNodePadding < MaximumMeasuredNodeWidth)
        {
            var detailText = string.Concat(
                document.Metadata.Status.ToString(),
                " | ",
                document.Metadata.Language,
                " | +",
                document.Metadata.AddedLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
                " -",
                document.Metadata.DeletedLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
                " | ",
                document.LineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                " lines");
            widestTextWidth = Math.Max(widestTextWidth, TextMetricsCache.Shared.MeasureNaturalWidth(detailText, DetailFont));
        }

        var maximumWidth = Math.Max(defaultNodeSize.Width, MaximumMeasuredNodeWidth);
        var measuredWidth = Math.Max(defaultNodeSize.Width, widestTextWidth + HorizontalNodePadding);
        measuredWidth = Math.Min(maximumWidth, Math.Ceiling(measuredWidth));

        var linePressure = Math.Min(1, Math.Max(0, (document.LineCount - 160) / 840d));
        var maximumHeight = Math.Max(defaultNodeSize.Height, MaximumMeasuredNodeHeight);
        var measuredHeight = Math.Min(maximumHeight, defaultNodeSize.Height + linePressure * 180);
        return new Size2(measuredWidth, Math.Ceiling(measuredHeight));
    }

    private static GraphLayoutFingerprint CreateFingerprint(GraphLayoutRequest request, GraphLayoutMode effectiveMode, CancellationToken cancellationToken)
    {
        var builder = GraphLayoutFingerprintBuilder.Create();
        builder.Add("semanticdiff-graph-layout-v2");
        builder.Add((int)effectiveMode);
        builder.Add(request.DefaultNodeSize.Width);
        builder.Add(request.DefaultNodeSize.Height);
        builder.Add(request.Documents.Length);
        for (var documentIndex = 0; documentIndex < request.Documents.Length; documentIndex++)
        {
            if ((documentIndex & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var document = request.Documents[documentIndex];
            builder.Add(document.Id.Value);
            builder.Add(document.Metadata.Path);
            builder.Add(document.Metadata.OldPath ?? string.Empty);
            builder.Add(document.Metadata.Language);
            builder.Add((int)document.Metadata.Status);
            builder.Add(document.Metadata.AddedLines);
            builder.Add(document.Metadata.DeletedLines);
            builder.Add(document.LineCount);
        }

        if (effectiveMode == GraphLayoutMode.Layered)
        {
            builder.Add(request.SemanticGraph.Anchors.Length);
            for (var anchorIndex = 0; anchorIndex < request.SemanticGraph.Anchors.Length; anchorIndex++)
            {
                if ((anchorIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var anchor = request.SemanticGraph.Anchors[anchorIndex];
                builder.Add(anchor.Id);
                builder.Add(anchor.DocumentId.Value);
                builder.Add((int)anchor.Kind);
                builder.Add(anchor.Range.Line);
                builder.Add(anchor.Range.Column);
                builder.Add(anchor.DisplayName);
            }

            builder.Add(request.SemanticGraph.Edges.Length);
            for (var edgeIndex = 0; edgeIndex < request.SemanticGraph.Edges.Length; edgeIndex++)
            {
                if ((edgeIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var edge = request.SemanticGraph.Edges[edgeIndex];
                builder.Add(edge.Id);
                builder.Add(edge.SourceAnchorId);
                builder.Add(edge.TargetAnchorId);
                builder.Add((int)edge.Kind);
                builder.Add(edge.Confidence);
                builder.Add(edge.Label ?? string.Empty);
            }
        }
        else
        {
            builder.Add(0);
            builder.Add(0);
        }

        var semanticAnchorCount = effectiveMode == GraphLayoutMode.Layered ? request.SemanticGraph.Anchors.Length : 0;
        var semanticEdgeCount = effectiveMode == GraphLayoutMode.Layered ? request.SemanticGraph.Edges.Length : 0;

        return new GraphLayoutFingerprint(
            builder.ToHash(),
            builder.ToSecondaryHash(),
            request.Documents.Length,
            semanticAnchorCount,
            semanticEdgeCount,
            effectiveMode);
    }

    private void TrimMeasuredNodes()
    {
        while (measuredNodeOrder.Count > MaxMeasuredNodes)
        {
            measuredNodes.Remove(measuredNodeOrder.Dequeue());
        }
    }

    private void TrimPreparedLayouts()
    {
        while (preparedLayoutOrder.Count > MaxPreparedLayouts)
        {
            preparedLayouts.Remove(preparedLayoutOrder.Dequeue());
        }
    }

    private void TrimArrangedLayouts()
    {
        while (arrangedLayoutOrder.Count > MaxArrangedLayouts)
        {
            arrangedLayouts.Remove(arrangedLayoutOrder.Dequeue());
        }
    }

    private readonly record struct GraphNodeMeasureKey(
        string DocumentId,
        string Path,
        string OldPath,
        string Language,
        DiffFileStatus Status,
        int AddedLines,
        int DeletedLines,
        int LineCount,
        double DefaultWidth,
        double DefaultHeight);
}

internal ref struct GraphLayoutFingerprintBuilder
{
    private const ulong OffsetBasis = 14695981039346656037;
    private const ulong Prime = 1099511628211;
    private const ulong SecondaryOffsetBasis = 1099511628211;
    private const ulong SecondaryPrime = 14029467366897019727;
    private const ulong SecondaryScramble = 0x9E3779B97F4A7C15;
    private ulong hash;
    private ulong secondaryHash;

    public static GraphLayoutFingerprintBuilder Create() => new() { hash = OffsetBasis, secondaryHash = SecondaryOffsetBasis };

    public void Add(string? value)
    {
        if (value is null)
        {
            Add(-1);
            return;
        }

        Add(value.Length);
        foreach (var character in value)
        {
            Add(character);
        }
    }

    public void Add(int value)
    {
        unchecked
        {
            Add((uint)value);
        }
    }

    public void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    private void Add(long value)
    {
        unchecked
        {
            Add((ulong)value);
        }
    }

    private void Add(uint value)
    {
        unchecked
        {
            Add((ulong)value);
        }
    }

    private void Add(ulong value)
    {
        unchecked
        {
            hash ^= value;
            hash *= Prime;
            secondaryHash ^= value + SecondaryScramble + (secondaryHash << 6) + (secondaryHash >> 2);
            secondaryHash *= SecondaryPrime;
        }
    }

    public readonly ulong ToHash() => hash;

    public readonly ulong ToSecondaryHash() => secondaryHash;
}
