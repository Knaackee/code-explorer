using Microsoft.Extensions.Logging;

namespace CodeExplorer.Core.Watching;

/// <summary>
/// Watches a folder for file changes and triggers re-indexing.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly ICodeIndexer _indexer;
    private readonly ILogger<FolderWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private readonly string _rootPath;
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);

    public FolderWatcher(
        string rootPath,
        ICodeIndexer indexer,
        ILogger<FolderWatcher> logger)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _indexer = indexer;
        _logger = logger;
    }

    /// <summary>Start watching for file changes.</summary>
    public void Start()
    {
        if (_watcher != null) return;

        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("Watching {Path} for changes", _rootPath);
    }

    /// <summary>Stop watching.</summary>
    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
        _logger.LogInformation("Stopped watching {Path}", _rootPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => ScheduleReIndex();
    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleReIndex();

    private void ScheduleReIndex()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceDelay, token);
                _logger.LogInformation("Re-indexing {Path} due to file changes", _rootPath);
                await _indexer.IndexFolderAsync(_rootPath, token);
            }
            catch (OperationCanceledException) { /* debounce cancelled, newer change incoming */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Re-indexing failed for {Path}", _rootPath);
            }
        }, token);
    }

    public void Dispose()
    {
        Stop();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
