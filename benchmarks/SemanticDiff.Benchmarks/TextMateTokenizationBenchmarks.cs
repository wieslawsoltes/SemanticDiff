using BenchmarkDotNet.Attributes;
using SemanticDiff.Core;
using SemanticDiff.Diff;

namespace SemanticDiff.Benchmarks;

[MemoryDiagnoser]
public class TextMateTokenizationBenchmarks
{
    private DiffDocumentSnapshot document = null!;
    private TextMateDocumentTokenizer tokenizer = null!;

    [Params(5_000, 30_000)]
    public int LineCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        document = new DiffDocumentFactory().CreateFromText(
            new DiffDocumentMetadata(
                new DiffDocumentId("LargeGenerated.cs"),
                "LargeGenerated.cs",
                null,
                DiffFileStatus.Modified,
                "C#",
                0,
                0),
            CreateSourceText(LineCount),
            DiffLineKind.Context);
        tokenizer = new TextMateDocumentTokenizer(pageSize: 128);
        tokenizer.PrimeTokenizationAsync(document, 511, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> ExactFirstViewport()
    {
        var lines = await tokenizer.TokenizePageAsync(document, 0, 320, CancellationToken.None);
        return CountTokens(lines);
    }

    [Benchmark]
    public int CacheOnlyFirstViewport()
    {
        return tokenizer.TryGetTokenizedLines(document, 0, 320, out var lines)
            ? CountTokens(lines)
            : 0;
    }

    [Benchmark]
    public async Task<int> PrimeMidFileViewport()
    {
        await tokenizer.PrimeTokenizationAsync(document, LineCount / 2, CancellationToken.None);
        return tokenizer.TryGetTokenizedLines(document, LineCount / 2 - 128, 256, out var lines)
            ? CountTokens(lines)
            : 0;
    }

    private static int CountTokens(IReadOnlyList<DiffLine> lines)
    {
        var count = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            count += lines[index].Tokens.Length;
        }

        return count;
    }

    private static string CreateSourceText(int lineCount)
    {
        var builder = new System.Text.StringBuilder(lineCount * 72);
        builder.AppendLine("namespace SemanticDiff.Generated;");
        builder.AppendLine("public sealed class LargeGenerated");
        builder.AppendLine("{");
        for (var index = 0; index < lineCount - 5; index++)
        {
            builder.Append("    public string Value");
            builder.Append(index);
            builder.Append(" => \"");
            builder.Append(index);
            builder.AppendLine("\";");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }
}
