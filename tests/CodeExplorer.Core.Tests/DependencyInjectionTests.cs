using CodeExplorer.Core.DependencyInjection;
using CodeExplorer.Core.Models;
using CodeExplorer.Core.Summarizer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace CodeExplorer.Core.Tests;

public sealed class DependencyInjectionTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"CodeExplorer-di-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private IServiceProvider BuildDefault() =>
        new ServiceCollection()
            .AddLogging()
            .AddCodeExplorer(o => o.IndexPath = _tempDir)
            .Services
            .BuildServiceProvider();

    // ── Default registrations ─────────────────────────────────────────────────

    [Fact]
    public void DefaultRegistration_AllCoreServicesAreResolvable()
    {
        var sp = BuildDefault();

        sp.GetRequiredService<ICodeIndexer>().Should().NotBeNull();
        sp.GetRequiredService<ISymbolRetriever>().Should().NotBeNull();
        sp.GetRequiredService<IOutlineProvider>().Should().NotBeNull();
        sp.GetRequiredService<ICodeAnalyzer>().Should().NotBeNull();
        sp.GetRequiredService<IIndexStore>().Should().NotBeNull();
        sp.GetRequiredService<ISymbolSummarizer>().Should().NotBeNull();
        sp.GetRequiredService<ISecurityFilter>().Should().NotBeNull();
        sp.GetRequiredService<ILanguageDetector>().Should().NotBeNull();
    }

    [Fact]
    public void DefaultRegistration_Summarizer_IsSignatureFallback()
    {
        var sp = BuildDefault();
        sp.GetRequiredService<ISymbolSummarizer>().Should().BeOfType<SignatureFallbackSummarizer>();
    }

    // ── Custom summarizer ─────────────────────────────────────────────────────

    [Fact]
    public void WithSummarizer_CustomImplementation_IsResolved()
    {
        var customSummarizer = Substitute.For<ISymbolSummarizer>();

        var sp = new ServiceCollection()
            .AddLogging()
            .AddCodeExplorer(o => o.IndexPath = _tempDir)
            .WithSummarizer(customSummarizer)
            .Services
            .BuildServiceProvider();

        sp.GetRequiredService<ISymbolSummarizer>().Should().BeSameAs(customSummarizer);
    }

    [Fact]
    public void WithSummarizer_Generic_ReplacesDefault()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddCodeExplorer(o => o.IndexPath = _tempDir)
            .WithSummarizer<SignatureFallbackSummarizer>()
            .Services
            .BuildServiceProvider();

        sp.GetRequiredService<ISymbolSummarizer>().Should().BeOfType<SignatureFallbackSummarizer>();
    }

    // ── Custom index store ────────────────────────────────────────────────────

    [Fact]
    public void WithIndexStore_CustomImplementation_IsResolved()
    {
        var customStore = Substitute.For<IIndexStore>();

        var sp = new ServiceCollection()
            .AddLogging()
            .AddCodeExplorer(o => o.IndexPath = _tempDir)
            .WithIndexStore(customStore)
            .Services
            .BuildServiceProvider();

        sp.GetRequiredService<IIndexStore>().Should().BeSameAs(customStore);
    }

    // ── Options ───────────────────────────────────────────────────────────────

    [Fact]
    public void Options_CustomValues_AreApplied()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddCodeExplorer(o =>
            {
                o.IndexPath = _tempDir;
                o.MaxFileSizeBytes = 500_000;
                o.FuzzyThreshold = 80;
            })
            .Services
            .BuildServiceProvider();

        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CodeExplorerOptions>>();
        options.Value.IndexPath.Should().Be(_tempDir);
        options.Value.MaxFileSizeBytes.Should().Be(500_000);
        options.Value.FuzzyThreshold.Should().Be(80);
    }

    // ── Singleton verification ────────────────────────────────────────────────

    [Fact]
    public void Services_AreSingleton_SameInstanceReturnedTwice()
    {
        var sp = BuildDefault();

        var indexer1 = sp.GetRequiredService<ICodeIndexer>();
        var indexer2 = sp.GetRequiredService<ICodeIndexer>();
        indexer1.Should().BeSameAs(indexer2);

        var retriever1 = sp.GetRequiredService<ISymbolRetriever>();
        var retriever2 = sp.GetRequiredService<ISymbolRetriever>();
        retriever1.Should().BeSameAs(retriever2);
    }
}
