using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using SemanticDiff.Core;
using SemanticDiff.Diff;
using SemanticDiff.Layout;

namespace SemanticDiff.Benchmarks;

[MemoryDiagnoser]
public class GraphLayoutBenchmarks
{
    private readonly GridGraphLayoutEngine engine = new();
    private readonly MsaglGraphLayoutEngine layeredEngine = new();
    private GraphLayoutRequest request = null!;
    private GraphLayoutRequest equivalentRequest = null!;
    private GraphLayoutRequest statusLaneRequest = null!;
    private GraphLayoutRequest semanticClusterRequest = null!;

    [Params(250, 1000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        request = new GraphLayoutRequest(
            CreateDocuments(DocumentCount),
            SemanticGraph.Empty,
            new Size2(620, 420),
            LayoutMode: GraphLayoutMode.CompactGrid);
        equivalentRequest = new GraphLayoutRequest(
            CreateDocuments(DocumentCount),
            SemanticGraph.Empty,
            new Size2(620, 420),
            LayoutMode: GraphLayoutMode.CompactGrid);
        statusLaneRequest = new GraphLayoutRequest(
            CreateDocuments(DocumentCount, varyStatus: true),
            SemanticGraph.Empty,
            new Size2(620, 420),
            LayoutMode: GraphLayoutMode.StatusLanes);
        var semanticDocuments = CreateDocuments(DocumentCount, varyStatus: true);
        semanticClusterRequest = new GraphLayoutRequest(
            semanticDocuments,
            CreateSemanticGraph(semanticDocuments),
            new Size2(620, 420),
            LayoutMode: GraphLayoutMode.Layered);
    }

    [IterationSetup(Target = nameof(WarmCompactGridLayout))]
    public void SetupWarmCompactGridLayout()
    {
        GraphLayoutCacheDiagnostics.Clear();
        engine.LayoutAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> ColdCompactGridLayout()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var result = await engine.LayoutAsync(request, CancellationToken.None).AsTask();
        return result.Nodes.Length;
    }

    [Benchmark]
    public async Task<int> WarmCompactGridLayout()
    {
        var result = await engine.LayoutAsync(equivalentRequest, CancellationToken.None).AsTask();
        return result.Nodes.Length;
    }

    [Benchmark]
    public async Task<int> ColdStatusLaneLayout()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var result = await engine.LayoutAsync(statusLaneRequest, CancellationToken.None).AsTask();
        return result.Nodes.Length;
    }

    [Benchmark]
    public async Task<int> ColdLargeSemanticClusterLayout()
    {
        GraphLayoutCacheDiagnostics.Clear();
        var result = await layeredEngine.LayoutAsync(semanticClusterRequest, CancellationToken.None).AsTask();
        return result.Nodes.Length;
    }

    private static ImmutableArray<DiffDocumentSnapshot> CreateDocuments(int count, bool varyStatus = false)
    {
        var factory = new DiffDocumentFactory();
        var builder = ImmutableArray.CreateBuilder<DiffDocumentSnapshot>(count);
        for (var index = 0; index < count; index++)
        {
            var root = (index % 5) switch
            {
                0 => "src/SemanticDiff.App/ViewModels",
                1 => "src/SemanticDiff.Rendering/Text",
                2 => "src/SemanticDiff.Layout/Engines",
                3 => "tests/SemanticDiff.Tests/Layout",
                _ => "docs/architecture/layout"
            };
            var status = varyStatus ? CreateStatus(index) : index % 11 == 0 ? DiffFileStatus.Added : DiffFileStatus.Modified;
            var path = $"{root}/GeneratedGraphLayoutBenchmarkFileWithLongName{index:00000}.cs";
            var lineCount = 24 + index % 1200;
            var sourceText = CreateSourceText(index, lineCount);
            builder.Add(factory.CreateFromText(
                new DiffDocumentMetadata(new DiffDocumentId(path), path, null, status, "C#", index % 17, index % 9),
                sourceText));
        }

        return builder.ToImmutable();
    }

    private static DiffFileStatus CreateStatus(int index) => (index % 7) switch
    {
        0 => DiffFileStatus.Added,
        1 => DiffFileStatus.Modified,
        2 => DiffFileStatus.Renamed,
        3 => DiffFileStatus.Deleted,
        4 => DiffFileStatus.Copied,
        5 => DiffFileStatus.Untracked,
        _ => DiffFileStatus.Conflicted
    };

    private static SemanticGraph CreateSemanticGraph(ImmutableArray<DiffDocumentSnapshot> documents)
    {
        var anchors = ImmutableArray.CreateBuilder<SemanticAnchor>(documents.Length);
        var edges = ImmutableArray.CreateBuilder<SemanticEdge>(Math.Max(0, documents.Length * 2 - 2));
        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            anchors.Add(new SemanticAnchor($"anchor:{index}", document.Id, new TextRange(0, 1, 1, 1), SemanticAnchorKind.Type, $"Type{index:00000}"));
        }

        for (var index = 1; index < documents.Length; index++)
        {
            edges.Add(new SemanticEdge(
                $"edge:{index}:prev",
                anchors[index].Id,
                anchors[index - 1].Id,
                SemanticEdgeKind.SymbolReference,
                0.92,
                "uses"));

            if (index > 3 && index % 3 == 0)
            {
                edges.Add(new SemanticEdge(
                    $"edge:{index}:root",
                    anchors[index].Id,
                    anchors[index / 3].Id,
                    SemanticEdgeKind.TypeInheritance,
                    0.88,
                    "inherits"));
            }
        }

        return new SemanticGraph(anchors.ToImmutable(), edges.ToImmutable());
    }

    private static string CreateSourceText(int index, int lineCount)
    {
        var lines = new string[lineCount];
        for (var line = 0; line < lines.Length; line++)
        {
            lines[line] = $"// benchmark file {index:00000} line {line:0000}";
        }

        return string.Join('\n', lines);
    }
}
