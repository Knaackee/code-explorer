using CodeExplorer.Core.Analysis;
using CodeExplorer.Core.Models;
using FluentAssertions;
using Xunit;

namespace CodeExplorer.Core.Tests.Analysis;

public sealed class PageRankCalculatorTests
{
    private static Symbol MakeSymbol(string id, params string[] refs) =>
        new()
        {
            Id            = id,
            FilePath      = "f.py",
            QualifiedName = id,
            Name          = id,
            Kind          = SymbolKind.Function,
            Language      = "python",
            Signature     = $"def {id}():",
            ContentHash   = "x",
            References    = [.. refs],
        };

    private static CodeIndex MakeIndex(params Symbol[] symbols) => new()
    {
        RepoKey   = "test/repo",
        RepoUrl   = "https://github.com/test/repo",
        IsLocal   = false,
        IndexedAt = DateTimeOffset.UtcNow,
        Symbols   = symbols.ToDictionary(s => s.Id),
    };

    [Fact]
    public void Calculate_EmptyIndex_DoesNotThrow()
    {
        var index = MakeIndex();
        var act = () => PageRankCalculator.Calculate(index);
        act.Should().NotThrow();
    }

    [Fact]
    public void Calculate_SingleNode_ScoreIsOne()
    {
        var sym = MakeSymbol("a");
        var index = MakeIndex(sym);

        PageRankCalculator.Calculate(index);

        index.Symbols["a"].CentralityScore.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void Calculate_LinearChain_HeadHasHighestScore()
    {
        // a → b → c → d
        // 'd' is referenced by 'c' which is referenced by 'b'...
        // The "sink" (most referenced) should score highest
        var a = MakeSymbol("a", "b");
        var b = MakeSymbol("b", "c");
        var c = MakeSymbol("c", "d");
        var d = MakeSymbol("d");
        var index = MakeIndex(a, b, c, d);

        PageRankCalculator.Calculate(index);

        // 'd' has highest in-degree (most referenced transitively)
        index.Symbols["d"].CentralityScore
            .Should().BeGreaterThan(index.Symbols["a"].CentralityScore);
    }

    [Fact]
    public void Calculate_HubNode_ScoresHighest()
    {
        // All symbols reference 'core_utils' — it should score highest
        var core = MakeSymbol("core_utils");
        var a    = MakeSymbol("service_a", "core_utils");
        var b    = MakeSymbol("service_b", "core_utils");
        var c    = MakeSymbol("service_c", "core_utils");
        var index = MakeIndex(core, a, b, c);

        PageRankCalculator.Calculate(index);

        index.Symbols["core_utils"].CentralityScore
            .Should().BeGreaterThan(index.Symbols["service_a"].CentralityScore);
    }

    [Fact]
    public void Calculate_AllScoresAreBetweenZeroAndOne()
    {
        var symbols = Enumerable.Range(0, 20)
            .Select(i => MakeSymbol($"s{i}", i > 0 ? $"s{i - 1}" : string.Empty))
            .ToArray();
        var index = MakeIndex(symbols);

        PageRankCalculator.Calculate(index);

        index.Symbols.Values.Should().AllSatisfy(s =>
        {
            s.CentralityScore.Should().BeGreaterOrEqualTo(0.0);
            s.CentralityScore.Should().BeLessOrEqualTo(1.0);
        });
    }

    [Fact]
    public void Calculate_IsDeterministic()
    {
        var a = MakeSymbol("a", "b", "c");
        var b = MakeSymbol("b", "c");
        var c = MakeSymbol("c");
        var index1 = MakeIndex(a, b, c);
        var index2 = MakeIndex(a, b, c);

        PageRankCalculator.Calculate(index1);
        PageRankCalculator.Calculate(index2);

        index1.Symbols["a"].CentralityScore.Should().BeApproximately(
            index2.Symbols["a"].CentralityScore, 1e-10);
    }
}

public sealed class DeadCodeDetectorTests
{
    private readonly DeadCodeDetector _sut = new();

    private static Symbol MakeSymbol(string id, SymbolKind kind = SymbolKind.Function, params string[] refs) =>
        new()
        {
            Id = id, FilePath = "f.py", QualifiedName = id, Name = id,
            Kind = kind, Language = "python", Signature = $"def {id}():", ContentHash = "x",
            References = [.. refs],
        };

    [Fact]
    public void Detect_UnreferencedFunction_IsReportedDead()
    {
        var dead  = MakeSymbol("truly_dead");
        var alive = MakeSymbol("alive", refs: "other");
        var caller = MakeSymbol("caller", refs: "alive"); // caller references alive
        var index = new CodeIndex
        {
            RepoKey = "r", RepoUrl = "u", IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
            Symbols = new() { [dead.Id] = dead, [alive.Id] = alive, [caller.Id] = caller },
        };

        var results = _sut.Detect(index);

        results.Should().Contain(d => d.Symbol.Id == "truly_dead");
        results.Should().NotContain(d => d.Symbol.Id == "alive");
    }

    [Fact]
    public void Detect_ClassSymbols_AreNotFlaggedDead()
    {
        // Top-level classes are entry points — never dead
        var cls = MakeSymbol("MyClass", SymbolKind.Class);
        var index = new CodeIndex
        {
            RepoKey = "r", RepoUrl = "u", IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
            Symbols = new() { [cls.Id] = cls },
        };

        var results = _sut.Detect(index);
        results.Should().NotContain(d => d.Symbol.Id == "MyClass");
    }

    [Fact]
    public void Detect_EmptyIndex_ReturnsEmpty()
    {
        var index = new CodeIndex
        {
            RepoKey = "r", RepoUrl = "u", IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
        };

        _sut.Detect(index).Should().BeEmpty();
    }
}

public sealed class BlastRadiusCalculatorTests
{
    private readonly BlastRadiusCalculator _sut = new();

    private static Symbol MakeSymbol(string id, params string[] refs) =>
        new()
        {
            Id = id, FilePath = "f.py", QualifiedName = id, Name = id,
            Kind = SymbolKind.Function, Language = "python", Signature = $"def {id}():",
            ContentHash = "x", References = [.. refs],
        };

    [Fact]
    public void Calculate_DirectDependents_AreIncluded()
    {
        // a and b both reference 'core'
        var core = MakeSymbol("core");
        var a    = MakeSymbol("a", "core");
        var b    = MakeSymbol("b", "core");
        var index = new CodeIndex
        {
            RepoKey = "r", RepoUrl = "u", IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
            Symbols = new() { [core.Id] = core, [a.Id] = a, [b.Id] = b },
        };

        var blast = _sut.Calculate(index, "core", maxDepth: 3);

        blast.Should().Contain(s => s.Id == "a");
        blast.Should().Contain(s => s.Id == "b");
        blast.Should().NotContain(s => s.Id == "core"); // self excluded
    }

    [Fact]
    public void Calculate_DepthLimit_IsRespected()
    {
        // chain: a → b → c → d → core
        var core = MakeSymbol("core");
        var d    = MakeSymbol("d",    "core");
        var c    = MakeSymbol("c",    "d");
        var b    = MakeSymbol("b",    "c");
        var a    = MakeSymbol("a",    "b");
        var index = new CodeIndex
        {
            RepoKey = "r", RepoUrl = "u", IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
            Symbols = new() { ["core"] = core, ["d"] = d, ["c"] = c, ["b"] = b, ["a"] = a },
        };

        var blast1 = _sut.Calculate(index, "core", maxDepth: 1);
        var blast3 = _sut.Calculate(index, "core", maxDepth: 4);

        blast1.Count.Should().BeLessThan(blast3.Count);
        blast1.Should().Contain(s => s.Id == "d");
        blast1.Should().NotContain(s => s.Id == "a");
        blast3.Should().Contain(s => s.Id == "a");
    }

    [Fact]
    public void Calculate_UnknownSymbol_ReturnsEmpty()
    {
        var index = new CodeIndex
        {
            RepoKey = "r", RepoUrl = "u", IsLocal = false, IndexedAt = DateTimeOffset.UtcNow,
        };

        var blast = _sut.Calculate(index, "nonexistent", maxDepth: 3);
        blast.Should().BeEmpty();
    }
}
