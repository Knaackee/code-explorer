using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using CodeExplorer.Core.Analysis;
using CodeExplorer.Core.Models;
using CodeExplorer.Core.Parsing;
using CodeExplorer.Core.Search;
using Microsoft.Extensions.Logging.Abstractions;

// ── Entry Point ───────────────────────────────────────────────────────────────

BenchmarkRunner.Run(
[
    typeof(SymbolExtractionBenchmarks),
    typeof(BM25SearchBenchmarks),
    typeof(FuzzySearchBenchmarks),
    typeof(PageRankBenchmarks),
    typeof(SymbolIdBenchmarks),
    typeof(ByteOffsetRetrievalBenchmarks),
]);

// ── Config ────────────────────────────────────────────────────────────────────

[Config(typeof(BenchmarkConfig))]
public class BenchmarkBase { }

public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.MediumRun
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(10));

        AddColumn(StatisticColumn.Mean);
        AddColumn(StatisticColumn.StdDev);
        AddColumn(StatisticColumn.Median);
        AddColumn(StatisticColumn.P95);
        AddColumn(RankColumn.Arabic);

        SummaryStyle = SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend);

        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}

// ── Symbol Extraction ─────────────────────────────────────────────────────────

[MemoryDiagnoser]
public class SymbolExtractionBenchmarks : BenchmarkBase
{
    private SymbolExtractor _extractor = null!;
    private LanguageSpec _pythonSpec = null!;
    private string _smallSource = null!;
    private string _mediumSource = null!;
    private string _largeSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        _extractor = new SymbolExtractor(NullLogger<SymbolExtractor>.Instance);
        _pythonSpec = LanguageRegistry.All["python"];

        _smallSource = GeneratePythonSource(10);
        _mediumSource = GeneratePythonSource(100);
        _largeSource = GeneratePythonSource(500);
    }

    [Benchmark(Baseline = true)]
    public object Extract_Small_10Functions() =>
        _extractor.Extract(_smallSource, "small.py", "python", _pythonSpec);

    [Benchmark]
    public object Extract_Medium_100Functions() =>
        _extractor.Extract(_mediumSource, "medium.py", "python", _pythonSpec);

    [Benchmark]
    public object Extract_Large_500Functions() =>
        _extractor.Extract(_largeSource, "large.py", "python", _pythonSpec);

    [Benchmark]
    public object Extract_TypeScript_100Functions()
    {
        var spec = LanguageRegistry.All["typescript"];
        var source = GenerateTypeScriptSource(100);
        return _extractor.Extract(source, "large.ts", "typescript", spec);
    }

    private static string GeneratePythonSource(int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
            sb.AppendLine($"def function_{i}(arg1, arg2):\n    \"\"\"Process item {i}.\"\"\"\n    return arg1 + arg2\n");
        return sb.ToString();
    }

    private static string GenerateTypeScriptSource(int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
            sb.AppendLine($"export function process{i}(value: string): string {{ return value.trim(); }}\n");
        return sb.ToString();
    }
}

// ── BM25 Search ───────────────────────────────────────────────────────────────

[MemoryDiagnoser]
public partial class BM25SearchBenchmarks : BenchmarkBase
{
    private BM25Engine _engine = null!;
    private List<Symbol> _corpus100 = null!;
    private List<Symbol> _corpus1000 = null!;
    private List<Symbol> _corpus10000 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new BM25Engine();
        _corpus100   = GenerateCorpus(100);
        _corpus1000  = GenerateCorpus(1_000);
        _corpus10000 = GenerateCorpus(10_000);
    }

    [Benchmark(Baseline = true)]
    public object Search_100Symbols() => _engine.Rank("authenticate user", _corpus100);

    [Benchmark]
    public object Search_1000Symbols() => _engine.Rank("authenticate user", _corpus1000);

    [Benchmark]
    public object Search_10000Symbols() => _engine.Rank("authenticate user", _corpus10000);

    [Benchmark]
    public object Search_MultiToken_1000() => _engine.Rank("parse json config file reader", _corpus1000);

    private static List<Symbol> GenerateCorpus(int count)
    {
        var names = new[] { "authenticate", "authorize", "parse", "validate", "process",
                            "render", "fetch", "save", "delete", "update", "create",
                            "get_user", "login", "logout", "register", "configure" };
        var summaries = new[] { "Handles user authentication",  "Checks user permissions",
                                "Parses input data",             "Validates request payload",
                                "Processes incoming request",    "Renders template response" };

        var rng = new Random(42);
        return Enumerable.Range(0, count).Select(i => new Symbol
        {
            Id            = $"src/module_{i}.py::func_{i}#function",
            FilePath      = $"src/module_{i % 20}.py",
            QualifiedName = names[i % names.Length] + $"_{i}",
            Name          = names[i % names.Length] + $"_{i}",
            Kind          = SymbolKind.Function,
            Language      = "python",
            Signature     = $"def {names[i % names.Length]}_{i}(request):",
            Summary       = summaries[i % summaries.Length],
            ContentHash   = $"hash{i}",
        }).ToList();
    }
}

// ── Fuzzy Search ──────────────────────────────────────────────────────────────

[MemoryDiagnoser]
public partial class FuzzySearchBenchmarks : BenchmarkBase
{
    private FuzzySearchEngine _engine = null!;
    private List<Symbol> _corpus1000 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new FuzzySearchEngine(threshold: 70);
        _corpus1000 = BM25SearchBenchmarks.GenerateCorpus_Public(1_000);
    }

    [Benchmark(Baseline = true)]
    public object FuzzySearch_ExactQuery() => _engine.Search("authenticate", _corpus1000);

    [Benchmark]
    public object FuzzySearch_TypoQuery() => _engine.Search("authentciate", _corpus1000);

    [Benchmark]
    public object FuzzySearch_PartialQuery() => _engine.Search("auth", _corpus1000);
}

// ── PageRank ──────────────────────────────────────────────────────────────────

[MemoryDiagnoser]
public class PageRankBenchmarks : BenchmarkBase
{
    private CodeIndex _index100 = null!;
    private CodeIndex _index500 = null!;
    private CodeIndex _index2000 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _index100  = GenerateIndex(100,  density: 0.05);
        _index500  = GenerateIndex(500,  density: 0.03);
        _index2000 = GenerateIndex(2000, density: 0.01);
    }

    [Benchmark(Baseline = true)]
    public void PageRank_100Symbols() => PageRankCalculator.Calculate(_index100);

    [Benchmark]
    public void PageRank_500Symbols() => PageRankCalculator.Calculate(_index500);

    [Benchmark]
    public void PageRank_2000Symbols() => PageRankCalculator.Calculate(_index2000);

    private static CodeIndex GenerateIndex(int count, double density)
    {
        var rng = new Random(42);
        var ids = Enumerable.Range(0, count).Select(i => $"s{i}").ToList();

        var symbols = ids.Select((id, i) =>
        {
            var refs = ids.Where((_, j) => j != i && rng.NextDouble() < density).ToList();
            return new Symbol
            {
                Id = id, FilePath = "f.py", QualifiedName = id, Name = id,
                Kind = SymbolKind.Function, Language = "python", Signature = $"def {id}():",
                ContentHash = "x", References = refs,
            };
        }).ToDictionary(s => s.Id);

        return new CodeIndex
        {
            RepoKey = "bench", RepoUrl = "https://example.com",
            IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
            Symbols = symbols,
        };
    }
}

// ── Symbol ID ─────────────────────────────────────────────────────────────────

[MemoryDiagnoser]
public class SymbolIdBenchmarks : BenchmarkBase
{
    [Benchmark(Baseline = true)]
    public string Build_SimpleId() =>
        SymbolIdBuilder.Build("src/main.py", "UserService.login", SymbolKind.Method);

    [Benchmark]
    public string Build_LongPath() =>
        SymbolIdBuilder.Build("very/deep/nested/path/to/some/module.py", "LongClassName.veryLongMethodName", SymbolKind.Method);

    [Benchmark]
    public (string, string, string) ParseId() =>
        SymbolIdBuilder.Parse("src/main.py::UserService.login#method");
}

// ── Byte Offset Retrieval ─────────────────────────────────────────────────────

[MemoryDiagnoser]
public class ByteOffsetRetrievalBenchmarks : BenchmarkBase
{
    private byte[] _sourceBytes = null!;
    private long _byteStart;
    private long _byteEnd;

    [GlobalSetup]
    public void Setup()
    {
        // Simulate a 50KB source file
        var source = string.Join("\n", Enumerable.Range(0, 1000)
            .Select(i => $"def func_{i}(arg1, arg2):\n    return arg1 + arg2"));
        _sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);
        _byteStart = 10_000;
        _byteEnd   = 10_200;
    }

    [Benchmark(Baseline = true)]
    public string ExtractByByteOffset()
    {
        var start = (int)Math.Min(_byteStart, _sourceBytes.Length);
        var end   = (int)Math.Min(_byteEnd,   _sourceBytes.Length);
        return System.Text.Encoding.UTF8.GetString(_sourceBytes[start..end]);
    }

    [Benchmark]
    public string ExtractByLineNumbers()
    {
        // Simulate the traditional approach: split by newline then slice
        var text = System.Text.Encoding.UTF8.GetString(_sourceBytes);
        var lines = text.Split('\n');
        return string.Join("\n", lines[50..55]);
    }
}

// ── Public static helper for sharing corpus between benchmarks ────────────────

public static class BenchmarkCorpusHelper
{
    public static List<Symbol> GenerateCorpus(int count) =>
        BM25SearchBenchmarks.GenerateCorpus_Public(count);
}

// Expose internal method for reuse
public partial class BM25SearchBenchmarks
{
    internal static List<Symbol> GenerateCorpus_Public(int count)
    {
        var names = new[] { "authenticate", "authorize", "parse", "validate", "process",
                            "render", "fetch", "save", "delete", "update" };
        return Enumerable.Range(0, count).Select(i => new Symbol
        {
            Id = $"s{i}::f{i}#function", FilePath = $"m{i % 10}.py",
            QualifiedName = names[i % names.Length] + $"_{i}",
            Name = names[i % names.Length] + $"_{i}",
            Kind = SymbolKind.Function, Language = "python",
            Signature = $"def func_{i}():", ContentHash = $"h{i}",
            Summary = "Handles core operation",
        }).ToList();
    }
}

// Make FuzzySearchBenchmarks work with the shared helper
public partial class FuzzySearchBenchmarks
{
    internal static List<Symbol> GenerateCorpus_Public(int count) =>
        BM25SearchBenchmarks.GenerateCorpus_Public(count);
}
