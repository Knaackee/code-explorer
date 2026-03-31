using CodeExplorer.Core.Models;

namespace CodeExplorer.Core;

// ─── Summarizer ────────────────────────────────────────────────────────────────

/// <summary>
/// Generates a one-line summary for a symbol.
/// Default implementation extracts summaries from docstrings and signatures.
/// </summary>
public interface ISymbolSummarizer
{
    /// <summary>
    /// Returns a concise one-line description of the symbol.
    /// Must never throw — return empty string or signature on failure.
    /// </summary>
    Task<string> SummarizeAsync(Symbol symbol, string? sourceContext = null, CancellationToken ct = default);
}

// ─── GitHub Client ─────────────────────────────────────────────────────────────

/// <summary>
/// Provides access to repository file listings and content.
/// Implement to plug in custom GitHub auth or alternative hosting.
/// </summary>
public interface IRepositoryClient
{
    Task<IReadOnlyList<RepoFile>> GetFilesAsync(string owner, string repo, CancellationToken ct = default);
    Task<string> GetFileContentAsync(string owner, string repo, string path, CancellationToken ct = default);
    Task<bool> RepoExistsAsync(string owner, string repo, CancellationToken ct = default);
}

// ─── Index Store ───────────────────────────────────────────────────────────────

/// <summary>
/// Persists and retrieves code indexes.
/// Implement to store in a database instead of the filesystem.
/// </summary>
public interface IIndexStore
{
    Task<CodeIndex?> LoadAsync(string repoKey, CancellationToken ct = default);
    Task SaveAsync(string repoKey, CodeIndex index, CancellationToken ct = default);
    Task DeleteAsync(string repoKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListRepoKeysAsync(CancellationToken ct = default);
    Task<string?> GetRawSourceAsync(string repoKey, string filePath, CancellationToken ct = default);
    Task SaveRawSourceAsync(string repoKey, string filePath, string content, CancellationToken ct = default);
}

// ─── Indexer ───────────────────────────────────────────────────────────────────

/// <summary>Main indexing orchestrator.</summary>
public interface ICodeIndexer
{
    Task<CodeIndex> IndexRepoAsync(string ownerSlashRepo, CancellationToken ct = default);
    Task<CodeIndex> IndexFolderAsync(string absolutePath, CancellationToken ct = default);
    Task<CodeIndex> ReIndexAsync(string repoKey, CancellationToken ct = default);
}

// ─── Symbol Retrieval ──────────────────────────────────────────────────────────

/// <summary>Provides search and retrieval over an indexed codebase.</summary>
public interface ISymbolRetriever
{
    Task<IReadOnlyList<SearchResult>> SearchSymbolsAsync(
        string repoKey, string query,
        SymbolKind? kind = null, string? language = null,
        int topK = 20, CancellationToken ct = default);

    Task<string?> GetSymbolSourceAsync(string repoKey, string symbolId, CancellationToken ct = default);

    Task<IReadOnlyList<SymbolWithSource>> GetSymbolsWithSourceAsync(
        string repoKey, IEnumerable<string> symbolIds, CancellationToken ct = default);

    Task<IReadOnlyList<SearchResult>> SearchTextAsync(
        string repoKey, string query, int topK = 20, CancellationToken ct = default);

    Task<ContextBundle> GetRankedContextAsync(
        string repoKey, string query, int tokenBudget = 4000, CancellationToken ct = default);
}

// ─── Outline ───────────────────────────────────────────────────────────────────

/// <summary>Returns structural overviews without loading full symbol source.</summary>
public interface IOutlineProvider
{
    Task<IReadOnlyList<SymbolNode>> GetRepoOutlineAsync(string repoKey, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolNode>> GetFileOutlineAsync(string repoKey, string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetFileTreeAsync(string repoKey, string? pathPrefix = null, CancellationToken ct = default);
}

// ─── Analysis ──────────────────────────────────────────────────────────────────

/// <summary>Advanced analysis: PageRank, dead code, blast radius, git diffs.</summary>
public interface ICodeAnalyzer
{
    Task<IReadOnlyList<SearchResult>> GetSymbolImportanceAsync(string repoKey, int topK = 20, CancellationToken ct = default);
    Task<IReadOnlyList<DeadSymbol>> FindDeadCodeAsync(string repoKey, CancellationToken ct = default);
    Task<ChangedSymbols> GetChangedSymbolsAsync(string repoKey, string fromCommit, string toCommit, CancellationToken ct = default);
    Task<IReadOnlyList<Symbol>> GetBlastRadiusAsync(string repoKey, string symbolId, int depth = 3, CancellationToken ct = default);
}

// ─── Security ─────────────────────────────────────────────────────────────────

/// <summary>Determines whether a file should be indexed.</summary>
public interface ISecurityFilter
{
    bool ShouldIndex(string absolutePath, long sizeBytes);
    bool IsSecret(string filePath);
    bool IsBinary(string filePath);
}

// ─── Language Support ─────────────────────────────────────────────────────────

/// <summary>Returns the language name for a given file path, or null if unsupported.</summary>
public interface ILanguageDetector
{
    string? DetectLanguage(string filePath);
    IReadOnlyList<string> SupportedLanguages { get; }
}
