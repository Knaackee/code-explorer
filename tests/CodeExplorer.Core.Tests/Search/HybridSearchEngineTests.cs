using CodeExplorer.Core.Models;
using CodeExplorer.Core.Search;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeExplorer.Core.Tests.Search;

public sealed class HybridSearchEngineTests
{
    private static HybridSearchEngine CreateEngine()
    {
        return new HybridSearchEngine(
            new BM25Engine(),
            new FuzzySearchEngine(70),
            NullLogger<HybridSearchEngine>.Instance);
    }

    private static List<Symbol> MakeCorpus() =>
    [
        new Symbol { Id = "s1", FilePath = "f.py", QualifiedName = "authenticate_user", Name = "authenticate_user",
            Kind = SymbolKind.Function, Language = "python", Signature = "def authenticate_user():",
            ContentHash = "h1", Summary = "Validates user credentials" },
        new Symbol { Id = "s2", FilePath = "f.py", QualifiedName = "parse_config", Name = "parse_config",
            Kind = SymbolKind.Function, Language = "python", Signature = "def parse_config():",
            ContentHash = "h2", Summary = "Parses configuration file" },
        new Symbol { Id = "s3", FilePath = "f.ts", QualifiedName = "UserService", Name = "UserService",
            Kind = SymbolKind.Class, Language = "typescript", Signature = "class UserService",
            ContentHash = "h3", Summary = "Manages user sessions" },
    ];

    [Fact]
    public async Task SearchAsync_ReturnsRelevantResults()
    {
        var engine = CreateEngine();
        var results = await engine.SearchAsync("authenticate", MakeCorpus());

        results.Should().NotBeEmpty();
        results[0].Symbol.Name.Should().Contain("authenticate");
    }

    [Fact]
    public async Task SearchAsync_KindFilter_OnlyReturnsMatchingKind()
    {
        var engine = CreateEngine();
        var results = await engine.SearchAsync("user", MakeCorpus(), kindFilter: SymbolKind.Class);

        results.Should().AllSatisfy(r => r.Symbol.Kind.Should().Be(SymbolKind.Class));
    }

    [Fact]
    public async Task SearchAsync_LanguageFilter_OnlyReturnsMatchingLanguage()
    {
        var engine = CreateEngine();
        var results = await engine.SearchAsync("user", MakeCorpus(), languageFilter: "python");

        results.Should().AllSatisfy(r => r.Symbol.Language.Should().Be("python"));
    }

    [Fact]
    public async Task SearchAsync_EmptyCorpus_ReturnsEmpty()
    {
        var engine = CreateEngine();
        var results = await engine.SearchAsync("anything", new List<Symbol>());

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_TopK_LimitsResults()
    {
        var engine = CreateEngine();
        var results = await engine.SearchAsync("user", MakeCorpus(), topK: 1);

        results.Should().HaveCountLessOrEqualTo(1);
    }

    [Fact]
    public async Task SearchAsync_ScoresArePositive()
    {
        var engine = CreateEngine();
        var results = await engine.SearchAsync("config", MakeCorpus());

        results.Should().AllSatisfy(r => r.Score.Should().BeGreaterThan(0));
    }
}
