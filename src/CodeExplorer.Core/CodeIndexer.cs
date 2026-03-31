using System.Diagnostics;
using CodeExplorer.Core.Models;
using CodeExplorer.Core.Parsing;
using CodeExplorer.Core.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeExplorer.Core;

/// <summary>Orchestrates the full indexing pipeline.</summary>
public sealed class CodeIndexer : ICodeIndexer
{
    private readonly IRepositoryClient _repoClient;
    private readonly IIndexStore _store;
    private readonly ISymbolSummarizer _summarizer;
    private readonly ISecurityFilter _security;
    private readonly SymbolExtractor _extractor;
    private readonly Retrieval.LocalFolderClient _localClient;
    private readonly CodeExplorerOptions _options;
    private readonly ILogger<CodeIndexer> _logger;

    public CodeIndexer(
        IRepositoryClient repoClient,
        IIndexStore store,
        ISymbolSummarizer summarizer,
        ISecurityFilter security,
        SymbolExtractor extractor,
        Retrieval.LocalFolderClient localClient,
        IOptions<CodeExplorerOptions> options,
        ILogger<CodeIndexer> logger)
    {
        _repoClient = repoClient;
        _store = store;
        _summarizer = summarizer;
        _security = security;
        _extractor = extractor;
        _localClient = localClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CodeIndex> IndexRepoAsync(string ownerSlashRepo, CancellationToken ct = default)
    {
        var parts = ownerSlashRepo.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException("Expected 'owner/repo' format", nameof(ownerSlashRepo));

        var (owner, repo) = (parts[0], parts[1]);
        var repoKey = ownerSlashRepo;

        _logger.LogInformation("Starting indexing of {RepoKey}", repoKey);
        var sw = Stopwatch.StartNew();

        // Check for existing index and incremental update
        var existing = await _store.LoadAsync(repoKey, ct);

        var files = await _repoClient.GetFilesAsync(owner, repo, ct);
        var index = await BuildIndexAsync(repoKey, ownerSlashRepo, false, files, owner, repo, existing, ct);

        index.IndexDuration = sw.Elapsed;
        await _store.SaveAsync(repoKey, index, ct);

        _logger.LogInformation(
            "Indexed {RepoKey}: {Symbols} symbols in {Files} files ({Duration:F1}s)",
            repoKey, index.SymbolCount, index.FileCount, sw.Elapsed.TotalSeconds);

        return index;
    }

    public async Task<CodeIndex> IndexFolderAsync(string absolutePath, CancellationToken ct = default)
    {
        absolutePath = Path.GetFullPath(absolutePath);
        var repoKey = Path.GetFileName(absolutePath);
        _logger.LogInformation("Starting indexing of folder {Path}", absolutePath);
        var sw = Stopwatch.StartNew();

        // Load .gitignore rules if available
        if (_security is Security.DefaultSecurityFilter dsf)
            dsf.LoadGitIgnore(absolutePath);

        _localClient.SetRootPath(absolutePath);

        var files = await _localClient.GetFilesAsync(string.Empty, string.Empty, ct);
        var existing = await _store.LoadAsync(repoKey, ct);
        var index = await BuildIndexAsync(repoKey, absolutePath, true, files, string.Empty, absolutePath, existing, ct);

        index.IndexDuration = sw.Elapsed;
        await _store.SaveAsync(repoKey, index, ct);
        return index;
    }

    public Task<CodeIndex> ReIndexAsync(string repoKey, CancellationToken ct = default)
    {
        // Determine if local or remote and re-run
        return repoKey.Contains('/') ? IndexRepoAsync(repoKey, ct) : IndexFolderAsync(repoKey, ct);
    }

    private async Task<CodeIndex> BuildIndexAsync(
        string repoKey, string repoUrl, bool isLocal,
        IReadOnlyList<RepoFile> files,
        string owner, string repo,
        CodeIndex? existing,
        CancellationToken ct)
    {
        var index = new CodeIndex
        {
            RepoKey = repoKey,
            RepoUrl = repoUrl,
            IsLocal = isLocal,
            IndexedAt = DateTimeOffset.UtcNow,
        };

        // Existing hashes for incremental indexing
        var existingHashes = existing?.FileHashes ?? [];

        var semaphore = new SemaphoreSlim(4); // max parallel file processing
        var tasks = files.Take(_options.MaxFilesPerRepo).Select(async file =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await ProcessFileAsync(file, owner, repo, index, existingHashes, existing, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Summarize symbols in batches
        await SummarizeSymbolsAsync(index, ct);

        // Build page-rank scores
        Analysis.PageRankCalculator.Calculate(index);

        return index;
    }

    private async Task ProcessFileAsync(
        RepoFile file, string owner, string repo,
        CodeIndex index, IReadOnlyDictionary<string, string> existingHashes,
        CodeIndex? existing, CancellationToken ct)
    {
        try
        {
            string source;
            if (owner == string.Empty)
            {
                // Local
                var fullPath = Path.Combine(repo == string.Empty ? string.Empty : repo, file.Path);
                source = await File.ReadAllTextAsync(fullPath, ct);
            }
            else
            {
                source = await _repoClient.GetFileContentAsync(owner, repo, file.Path, ct);
            }

            var fileHash = ComputeHash(source);

            // Incremental: skip if unchanged
            if (existingHashes.TryGetValue(file.Path, out var oldHash) && oldHash == fileHash && existing != null)
            {
                if (existing.FileSymbols.TryGetValue(file.Path, out var ids))
                {
                    lock (index)
                    {
                        index.FileHashes[file.Path] = fileHash;
                        index.FileSymbols[file.Path] = ids;
                        foreach (var id in ids)
                            if (existing.Symbols.TryGetValue(id, out var sym))
                                index.Symbols[id] = sym;
                    }
                }
                return;
            }

            // Store raw source for O(1) byte-offset retrieval
            await _store.SaveRawSourceAsync(index.RepoKey, file.Path, source, ct);

            var spec = LanguageRegistry.ForFile(file.Path);
            if (spec == null) return;

            var symbols = _extractor.Extract(source, file.Path, file.Language, spec);

            lock (index)
            {
                index.FileHashes[file.Path] = fileHash;
                index.FileSymbols[file.Path] = symbols.Select(s => s.Id).ToList();
                index.TotalBytes += System.Text.Encoding.UTF8.GetByteCount(source);
                foreach (var sym in symbols)
                    index.Symbols[sym.Id] = sym;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process {File}", file.Path);
        }
    }

    private async Task SummarizeSymbolsAsync(CodeIndex index, CancellationToken ct)
    {
        var unsummarized = index.Symbols.Values
            .Where(s => string.IsNullOrEmpty(s.Summary))
            .ToList();

        _logger.LogDebug("Summarizing {Count} symbols", unsummarized.Count);

        var semaphore = new SemaphoreSlim(5);
        var tasks = unsummarized.Select(async symbol =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                symbol.Summary = await _summarizer.SummarizeAsync(symbol, ct: ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
    }
}

/// <summary>Symbol retrieval with O(1) byte-offset seeking.</summary>
public sealed class SymbolRetriever : ISymbolRetriever
{
    private readonly IIndexStore _store;
    private readonly HybridSearchEngine _search;
    private readonly TextSearchEngine _textSearch;
    private readonly ILogger<SymbolRetriever> _logger;

    public SymbolRetriever(
        IIndexStore store,
        HybridSearchEngine search,
        TextSearchEngine textSearch,
        ILogger<SymbolRetriever> logger)
    {
        _store = store;
        _search = search;
        _textSearch = textSearch;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchSymbolsAsync(
        string repoKey, string query,
        SymbolKind? kind = null, string? language = null,
        int topK = 20, CancellationToken ct = default)
    {
        var index = await RequireIndexAsync(repoKey, ct);
        return await _search.SearchAsync(query, index.Symbols.Values, kind, language, topK, ct);
    }

    public async Task<string?> GetSymbolSourceAsync(string repoKey, string symbolId, CancellationToken ct = default)
    {
        var index = await RequireIndexAsync(repoKey, ct);
        if (!index.Symbols.TryGetValue(symbolId, out var symbol)) return null;

        // O(1) byte-offset retrieval from cached raw source
        var raw = await _store.GetRawSourceAsync(repoKey, symbol.FilePath, ct);
        if (raw == null) return null;

        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var start = (int)Math.Min(symbol.ByteStart, bytes.Length);
        var end   = (int)Math.Min(symbol.ByteEnd,   bytes.Length);
        return System.Text.Encoding.UTF8.GetString(bytes[start..end]);
    }

    public async Task<IReadOnlyList<SymbolWithSource>> GetSymbolsWithSourceAsync(
        string repoKey, IEnumerable<string> symbolIds, CancellationToken ct = default)
    {
        var results = new List<SymbolWithSource>();
        foreach (var id in symbolIds)
        {
            var source = await GetSymbolSourceAsync(repoKey, id, ct);
            var index = await RequireIndexAsync(repoKey, ct);
            if (index.Symbols.TryGetValue(id, out var sym))
                results.Add(new SymbolWithSource { Symbol = sym, Source = source ?? sym.Signature });
        }
        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchTextAsync(
        string repoKey, string query, int topK = 20, CancellationToken ct = default)
    {
        var index = await RequireIndexAsync(repoKey, ct);
        return _textSearch.SearchText(query, index.Symbols,
            filePath => _store.GetRawSourceAsync(repoKey, filePath, ct).GetAwaiter().GetResult())
            .Take(topK).ToList();
    }

    public async Task<ContextBundle> GetRankedContextAsync(
        string repoKey, string query, int tokenBudget = 4000, CancellationToken ct = default)
    {
        var results = await SearchSymbolsAsync(repoKey, query, topK: 50, ct: ct);
        var bundle = new ContextBundle { Query = query, BudgetTokens = tokenBudget };

        int used = 0;
        int excluded = 0;

        foreach (var result in results.OrderByDescending(r => r.Score))
        {
            var source = await GetSymbolSourceAsync(repoKey, result.Symbol.Id, ct) ?? result.Symbol.Signature;
            var tokens = result.Symbol.TokenEstimate;

            if (used + tokens > tokenBudget) { excluded++; continue; }

            bundle.Symbols.Add(new SymbolWithSource { Symbol = result.Symbol, Source = source });
            used += tokens;
        }

        var finalBundle = new ContextBundle
        {
            Query = bundle.Query,
            BudgetTokens = bundle.BudgetTokens,
            Symbols = bundle.Symbols,
            UsedTokens = used,
            ExcludedCount = excluded,
        };
        return finalBundle;
    }

    private async Task<CodeIndex> RequireIndexAsync(string repoKey, CancellationToken ct)
    {
        var index = await _store.LoadAsync(repoKey, ct);
        if (index == null)
            throw new InvalidOperationException($"No index found for '{repoKey}'. Run 'index' first.");
        return index;
    }
}
