using CodeExplorer.Core.Models;
using CodeExplorer.Core.Summarizer;
using FluentAssertions;
using Xunit;

namespace CodeExplorer.Core.Tests.Summarizer;

public sealed class SignatureFallbackSummarizerTests
{
    private readonly SignatureFallbackSummarizer _sut = new();

    private static Symbol MakeSymbol(string name, string signature) =>
        new()
        {
            Id = $"f.py::{name}#function", FilePath = "f.py",
            QualifiedName = name, Name = name, Kind = SymbolKind.Function,
            Language = "python", Signature = signature, ContentHash = "x",
        };

    [Fact]
    public async Task SummarizeAsync_ReturnsNonEmptyString()
    {
        var symbol = MakeSymbol("authenticate", "def authenticate(user, password):");
        var result = await _sut.SummarizeAsync(symbol);
        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SummarizeAsync_ContainsSymbolName()
    {
        var symbol = MakeSymbol("parse_config", "def parse_config(path: str) -> Config:");
        var result = await _sut.SummarizeAsync(symbol);
        result.Should().Contain("parse_config");
    }

    [Fact]
    public async Task SummarizeAsync_NeverThrows()
    {
        var symbol = MakeSymbol("", "");
        var act = async () => await _sut.SummarizeAsync(symbol);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SummarizeAsync_WithCancellation_StillReturns()
    {
        var symbol = MakeSymbol("foo", "def foo():");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await _sut.SummarizeAsync(symbol, ct: cts.Token);
        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SummarizeAsync_WithDocstring_ExtractsDocstring()
    {
        var symbol = MakeSymbol("greet", "def greet(name):");
        var source = "def greet(name):\n    \"\"\"Say hello to the user.\"\"\"\n    pass";
        var result = await _sut.SummarizeAsync(symbol, source);
        result.Should().Contain("Say hello");
    }

    [Fact]
    public async Task SummarizeAsync_NoSignature_ReturnsKindAndName()
    {
        var symbol = MakeSymbol("do_things", "");
        var result = await _sut.SummarizeAsync(symbol);
        result.Should().Contain("do_things");
    }
}
