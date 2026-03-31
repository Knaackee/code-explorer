using CodeExplorer.Core.Watching;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CodeExplorer.Core.Tests.Watching;

public sealed class FolderWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public FolderWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CodeExplorer-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Start_DoesNotThrow()
    {
        var indexer = Substitute.For<ICodeIndexer>();
        using var watcher = new FolderWatcher(_tempDir, indexer, NullLogger<FolderWatcher>.Instance);

        var act = () => watcher.Start();
        act.Should().NotThrow();
    }

    [Fact]
    public void Stop_AfterStart_DoesNotThrow()
    {
        var indexer = Substitute.For<ICodeIndexer>();
        using var watcher = new FolderWatcher(_tempDir, indexer, NullLogger<FolderWatcher>.Instance);

        watcher.Start();
        var act = () => watcher.Stop();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var indexer = Substitute.For<ICodeIndexer>();
        var watcher = new FolderWatcher(_tempDir, indexer, NullLogger<FolderWatcher>.Instance);

        var act = () => watcher.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task FileChange_TriggersReIndex()
    {
        var indexer = Substitute.For<ICodeIndexer>();
        using var watcher = new FolderWatcher(_tempDir, indexer, NullLogger<FolderWatcher>.Instance);
        watcher.Start();

        // Create a file to trigger the watcher
        File.WriteAllText(Path.Combine(_tempDir, "test.py"), "x = 1");

        // Wait for debounce (2s) + processing margin
        await Task.Delay(4000);

        await indexer.Received().IndexFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
