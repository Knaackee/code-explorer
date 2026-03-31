using System.Text.Json.Serialization;

namespace CodeExplorer.Core.Models;

/// <summary>Symbol kinds extracted from AST.</summary>
public enum SymbolKind
{
    Function,
    Class,
    Method,
    Constant,
    Type,
    Interface,
    Enum,
    Property,
    Field,
    Module,
    Unknown
}

/// <summary>
/// A single extracted code symbol with metadata and byte-offset for O(1) retrieval.
/// </summary>
public sealed class Symbol
{
    /// <summary>Stable ID: "relative/path.ext::qualified.name#kind"</summary>
    public required string Id { get; init; }

    public required string FilePath { get; init; }
    public required string QualifiedName { get; init; }
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public required string Language { get; init; }

    /// <summary>Function/class signature without body.</summary>
    public required string Signature { get; init; }

    /// <summary>AI-generated or docstring-derived one-line summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Byte offset into the raw cached source file — enables O(1) retrieval.</summary>
    public long ByteStart { get; init; }
    public long ByteEnd { get; init; }

    public int StartLine { get; init; }
    public int EndLine { get; init; }

    /// <summary>SHA256 of symbol source for incremental re-indexing.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Parent symbol ID for nested symbols (e.g. method inside class).</summary>
    public string? ParentId { get; init; }

    /// <summary>Symbol IDs this symbol directly references/imports.</summary>
    public List<string> References { get; init; } = [];

    /// <summary>PageRank-based centrality score (0–1). Higher = more important.</summary>
    public double CentralityScore { get; set; }

    /// <summary>Additional search keywords (e.g. from context providers like dbt).</summary>
    public List<string> Keywords { get; init; } = [];

    [JsonIgnore]
    public int TokenEstimate => (int)((ByteEnd - ByteStart) / 4.0);
}

/// <summary>Full index for a single repository or folder.</summary>
public sealed class CodeIndex
{
    public required string RepoKey { get; init; }
    public required string RepoUrl { get; init; }
    public required bool IsLocal { get; init; }
    public required DateTimeOffset IndexedAt { get; init; }
    public TimeSpan IndexDuration { get; set; }

    public Dictionary<string, Symbol> Symbols { get; init; } = [];

    /// <summary>Map of file path → list of symbol IDs in that file.</summary>
    public Dictionary<string, List<string>> FileSymbols { get; init; } = [];

    /// <summary>Map of file path → SHA256 for incremental updates.</summary>
    public Dictionary<string, string> FileHashes { get; init; } = [];

    /// <summary>Total raw source bytes cached locally.</summary>
    public long TotalBytes { get; set; }

    public int SymbolCount => Symbols.Count;
    public int FileCount => FileSymbols.Count;
}

/// <summary>Lightweight file descriptor from GitHub or filesystem.</summary>
public sealed class RepoFile
{
    public required string Path { get; init; }
    public required string Language { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha { get; init; }
}

/// <summary>Hierarchical symbol node for file/repo outlines.</summary>
public sealed class SymbolNode
{
    public required Symbol Symbol { get; init; }
    public List<SymbolNode> Children { get; init; } = [];
}

/// <summary>A search result with relevance score.</summary>
public sealed class SearchResult
{
    public required Symbol Symbol { get; init; }
    public double Score { get; init; }
    public SearchMatchType MatchType { get; init; }
}

public enum SearchMatchType { Exact, Fuzzy, BM25, Semantic, Hybrid }

/// <summary>Token-budgeted context bundle.</summary>
public sealed class ContextBundle
{
    public required string Query { get; init; }
    public List<SymbolWithSource> Symbols { get; init; } = [];
    public int BudgetTokens { get; init; }
    public int UsedTokens { get; init; }
    public int ExcludedCount { get; init; }
}

public sealed class SymbolWithSource
{
    public required Symbol Symbol { get; init; }
    public required string Source { get; init; }
}

/// <summary>Dead code report entry.</summary>
public sealed class DeadSymbol
{
    public required Symbol Symbol { get; init; }
    public string Reason { get; init; } = "No incoming references detected";
}

/// <summary>Git diff → affected symbols mapping.</summary>
public sealed class ChangedSymbols
{
    public required string FromCommit { get; init; }
    public required string ToCommit { get; init; }
    public List<Symbol> Added { get; init; } = [];
    public List<Symbol> Modified { get; init; } = [];
    public List<Symbol> Deleted { get; init; } = [];
}

/// <summary>Global options injected via DI.</summary>
public sealed class CodeExplorerOptions
{
    public string IndexPath { get; set; } = ResolveDefaultIndexPath();

    public long MaxFileSizeBytes { get; set; } = 1_000_000;
    public int MaxFilesPerRepo { get; set; } = 10_000;
    public TimeSpan StalenessThreshold { get; set; } = TimeSpan.FromDays(7);
    public int BM25TopK { get; set; } = 20;
    public int FuzzyThreshold { get; set; } = 70;

    private static string ResolveDefaultIndexPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CODE_INDEX_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(fromEnv));

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".code-index");
    }
}
