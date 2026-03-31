using System.Text.Json;
using System.Text.Json.Serialization;
using CodeExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeExplorer.Core.Storage;

/// <summary>
/// Persists code indexes as JSON files under ~/.code-index/
/// Uses atomic write (temp file + move) to prevent corruption.
/// </summary>
public sealed class JsonFileIndexStore : IIndexStore
{
    private readonly string _basePath;
    private readonly ILogger<JsonFileIndexStore> _logger;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileIndexStore(IOptions<CodeExplorerOptions> options, ILogger<JsonFileIndexStore> logger)
    {
        _basePath = options.Value.IndexPath;
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    public async Task<CodeIndex?> LoadAsync(string repoKey, CancellationToken ct = default)
    {
        var path = IndexPath(repoKey);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CodeIndex>(stream, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load index for {RepoKey}", repoKey);
            return null;
        }
    }

    public async Task SaveAsync(string repoKey, CodeIndex index, CancellationToken ct = default)
    {
        var path = IndexPath(repoKey);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var semaphore = _writeLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tmp))
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, ct);

            // Atomic replace
            File.Move(tmp, path, overwrite: true);
            _logger.LogDebug("Saved index for {RepoKey} ({Symbols} symbols)", repoKey, index.SymbolCount);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public Task DeleteAsync(string repoKey, CancellationToken ct = default)
    {
        var path = IndexPath(repoKey);
        if (File.Exists(path)) File.Delete(path);
        var sourceDir = SourceDir(repoKey);
        if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, recursive: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListRepoKeysAsync(CancellationToken ct = default)
    {
        var keys = Directory
            .GetFiles(_basePath, "*.index.json", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f).Replace(".index.json", "").Replace("__", "/"))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    public async Task<string?> GetRawSourceAsync(string repoKey, string filePath, CancellationToken ct = default)
    {
        var path = SourceFilePath(repoKey, filePath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task SaveRawSourceAsync(string repoKey, string filePath, string content, CancellationToken ct = default)
    {
        var path = SourceFilePath(repoKey, filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string IndexPath(string repoKey) =>
        Path.Combine(_basePath, SafeKey(repoKey) + ".index.json");

    private string SourceDir(string repoKey) =>
        Path.Combine(_basePath, "src", SafeKey(repoKey));

    private string SourceFilePath(string repoKey, string filePath) =>
        Path.Combine(SourceDir(repoKey), filePath.Replace('/', Path.DirectorySeparatorChar));

    private static string SafeKey(string key) => key.Replace("/", "__").Replace("\\", "__");
}
