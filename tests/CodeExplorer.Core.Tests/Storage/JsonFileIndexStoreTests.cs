using CodeExplorer.Core.Models;
using CodeExplorer.Core.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeExplorer.Core.Tests.Storage;

public sealed class JsonFileIndexStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"CodeExplorer-test-{Guid.NewGuid():N}");
    private readonly JsonFileIndexStore _sut;

    public JsonFileIndexStoreTests()
    {
        _sut = new JsonFileIndexStore(
            Options.Create(new CodeExplorerOptions { IndexPath = _tempDir }),
            NullLogger<JsonFileIndexStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static CodeIndex MakeIndex(string key, int symbolCount = 3) => new()
    {
        RepoKey   = key,
        RepoUrl   = $"https://github.com/{key}",
        IsLocal   = false,
        IndexedAt = DateTimeOffset.UtcNow,
        Symbols   = Enumerable.Range(0, symbolCount).ToDictionary(
            i => $"{key}/f.py::func{i}#function",
            i => new Symbol
            {
                Id            = $"{key}/f.py::func{i}#function",
                FilePath      = "f.py",
                QualifiedName = $"func{i}",
                Name          = $"func{i}",
                Kind          = SymbolKind.Function,
                Language      = "python",
                Signature     = $"def func{i}():",
                ContentHash   = $"hash{i}",
            }),
    };

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesAllData()
    {
        var index = MakeIndex("owner/repo");
        await _sut.SaveAsync("owner/repo", index);

        var loaded = await _sut.LoadAsync("owner/repo");

        loaded.Should().NotBeNull();
        loaded!.RepoKey.Should().Be("owner/repo");
        loaded.SymbolCount.Should().Be(3);
        loaded.Symbols.Keys.Should().BeEquivalentTo(index.Symbols.Keys);
    }

    [Fact]
    public async Task Load_NonExistentKey_ReturnsNull()
    {
        var result = await _sut.LoadAsync("does/not/exist");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_Overwrites_ExistingIndex()
    {
        await _sut.SaveAsync("owner/repo", MakeIndex("owner/repo", 5));
        await _sut.SaveAsync("owner/repo", MakeIndex("owner/repo", 2));

        var loaded = await _sut.LoadAsync("owner/repo");
        loaded!.SymbolCount.Should().Be(2);
    }

    [Fact]
    public async Task Delete_ExistingIndex_RemovesIt()
    {
        await _sut.SaveAsync("owner/repo", MakeIndex("owner/repo"));
        await _sut.DeleteAsync("owner/repo");

        var loaded = await _sut.LoadAsync("owner/repo");
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonExistentKey_DoesNotThrow()
    {
        var act = async () => await _sut.DeleteAsync("no/such/repo");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListRepoKeys_ReturnsAllSavedKeys()
    {
        await _sut.SaveAsync("owner/repo1", MakeIndex("owner/repo1"));
        await _sut.SaveAsync("owner/repo2", MakeIndex("owner/repo2"));

        var keys = await _sut.ListRepoKeysAsync();

        keys.Should().HaveCount(2);
        keys.Should().Contain("owner/repo1");
        keys.Should().Contain("owner/repo2");
    }

    [Fact]
    public async Task ListRepoKeys_EmptyStore_ReturnsEmpty()
    {
        var keys = await _sut.ListRepoKeysAsync();
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAndGetRawSource_RoundTrip_PreservesContent()
    {
        const string content = "def foo():\n    return 42\n";
        await _sut.SaveRawSourceAsync("owner/repo", "src/main.py", content);

        var loaded = await _sut.GetRawSourceAsync("owner/repo", "src/main.py");
        loaded.Should().Be(content);
    }

    [Fact]
    public async Task GetRawSource_NonExistentFile_ReturnsNull()
    {
        var result = await _sut.GetRawSourceAsync("owner/repo", "nonexistent.py");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_IsAtomicOnConcurrentWrites_DoesNotCorrupt()
    {
        var index = MakeIndex("owner/repo", 100);

        // Concurrent saves
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _sut.SaveAsync("owner/repo", index));

        await Task.WhenAll(tasks);

        // Should still be readable
        var loaded = await _sut.LoadAsync("owner/repo");
        loaded.Should().NotBeNull();
        loaded!.SymbolCount.Should().Be(100);
    }

    [Fact]
    public async Task Save_SymbolsAreSerializedWithEnumNames()
    {
        var index = MakeIndex("owner/repo");
        await _sut.SaveAsync("owner/repo", index);

        // Read raw JSON and check enum is stored as string, not int
        var rawPath = Directory.GetFiles(_tempDir, "*.index.json", SearchOption.AllDirectories)[0];
        var json = await File.ReadAllTextAsync(rawPath);
        json.Should().Contain("\"Function\""); // not "0"
    }
}
