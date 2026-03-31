using CodeExplorer.Core.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace CodeExplorer.Core.Analysis;

/// <summary>
/// Computes PageRank over the symbol dependency graph.
/// Higher centrality = more symbols depend on this symbol = more "important".
/// </summary>
public static class PageRankCalculator
{
    private const double DampingFactor = 0.85;
    private const int MaxIterations = 100;
    private const double Tolerance = 1e-6;

    public static void Calculate(CodeIndex index)
    {
        var symbols = index.Symbols.Values.ToList();
        if (symbols.Count == 0) return;

        int n = symbols.Count;
        var idToIdx = symbols.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);

        // Build adjacency list (outgoing edges)
        var outLinks = new List<int>[n];
        for (int i = 0; i < n; i++) outLinks[i] = [];

        foreach (var (i, symbol) in symbols.Index())
            foreach (var refId in symbol.References)
                if (idToIdx.TryGetValue(refId, out int j))
                    outLinks[i].Add(j);

        // Power iteration
        var rank = Enumerable.Repeat(1.0 / n, n).ToArray();
        var newRank = new double[n];

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            Array.Fill(newRank, (1 - DampingFactor) / n);

            for (int i = 0; i < n; i++)
            {
                if (outLinks[i].Count == 0)
                {
                    // Dangling node: distribute to all
                    double share = DampingFactor * rank[i] / n;
                    for (int j = 0; j < n; j++) newRank[j] += share;
                }
                else
                {
                    double share = DampingFactor * rank[i] / outLinks[i].Count;
                    foreach (int j in outLinks[i]) newRank[j] += share;
                }
            }

            double delta = rank.Zip(newRank, (a, b) => Math.Abs(a - b)).Sum();
            Array.Copy(newRank, rank, n);
            if (delta < Tolerance) break;
        }

        // Normalize to [0, 1]
        double max = rank.Max();
        for (int i = 0; i < n; i++)
            symbols[i].CentralityScore = max > 0 ? rank[i] / max : 0;
    }
}

/// <summary>Dead code detection: symbols with no incoming references.</summary>
public sealed class DeadCodeDetector
{
    public IReadOnlyList<DeadSymbol> Detect(CodeIndex index)
    {
        var allReferenced = index.Symbols.Values
            .SelectMany(s => s.References)
            .ToHashSet();

        return index.Symbols.Values
            .Where(s => !allReferenced.Contains(s.Id)
                     && s.Kind is not SymbolKind.Class and not SymbolKind.Interface) // top-level classes are entry points
            .Select(s => new DeadSymbol { Symbol = s })
            .ToList();
    }
}

/// <summary>Maps git diffs to affected symbols using LibGit2Sharp.</summary>
public sealed class GitDiffAnalyzer
{
    private readonly ILogger<GitDiffAnalyzer> _logger;

    public GitDiffAnalyzer(ILogger<GitDiffAnalyzer> logger)
    {
        _logger = logger;
    }

    public ChangedSymbols Analyze(CodeIndex index, string repoPath, string fromCommit, string toCommit)
    {
        var result = new ChangedSymbols { FromCommit = fromCommit, ToCommit = toCommit };

        try
        {
            using var repo = new Repository(repoPath);
            var from = repo.Lookup<Commit>(fromCommit);
            var to   = repo.Lookup<Commit>(toCommit);

            var diff = repo.Diff.Compare<TreeChanges>(from.Tree, to.Tree);
            var changedPaths = diff.Select(c => c.Path).ToHashSet();

            foreach (var symbol in index.Symbols.Values)
            {
                if (!changedPaths.Contains(symbol.FilePath)) continue;

                var change = diff.FirstOrDefault(c => c.Path == symbol.FilePath);
                if (change == null) continue;

                switch (change.Status)
                {
                    case ChangeKind.Added:    result.Added.Add(symbol); break;
                    case ChangeKind.Deleted:  result.Deleted.Add(symbol); break;
                    default:                  result.Modified.Add(symbol); break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git diff analysis failed for {RepoPath}", repoPath);
        }

        return result;
    }
}

/// <summary>Computes blast radius: symbols transitively affected if a given symbol changes.</summary>
public sealed class BlastRadiusCalculator
{
    public IReadOnlyList<Symbol> Calculate(CodeIndex index, string symbolId, int maxDepth = 3)
    {
        var affected = new HashSet<string>();
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((symbolId, 0));

        // Build reverse dependency map
        var incomingRefs = new Dictionary<string, List<string>>();
        foreach (var sym in index.Symbols.Values)
            foreach (var refId in sym.References)
            {
                if (!incomingRefs.TryGetValue(refId, out var list))
                    incomingRefs[refId] = list = [];
                list.Add(sym.Id);
            }

        while (queue.TryDequeue(out var item))
        {
            if (item.depth > maxDepth) continue;
            if (!affected.Add(item.id)) continue;

            foreach (var caller in incomingRefs.GetValueOrDefault(item.id, []))
                queue.Enqueue((caller, item.depth + 1));
        }

        affected.Remove(symbolId); // exclude self
        return affected
            .Where(id => index.Symbols.ContainsKey(id))
            .Select(id => index.Symbols[id])
            .ToList();
    }
}

/// <summary>Provides all analysis features as a single service.</summary>
public sealed class CodeAnalyzer : ICodeAnalyzer
{
    private readonly IIndexStore _store;
    private readonly DeadCodeDetector _deadCode;
    private readonly BlastRadiusCalculator _blastRadius;
    private readonly GitDiffAnalyzer _gitDiff;
    private readonly ILogger<CodeAnalyzer> _logger;

    public CodeAnalyzer(
        IIndexStore store,
        DeadCodeDetector deadCode,
        BlastRadiusCalculator blastRadius,
        GitDiffAnalyzer gitDiff,
        ILogger<CodeAnalyzer> logger)
    {
        _store = store;
        _deadCode = deadCode;
        _blastRadius = blastRadius;
        _gitDiff = gitDiff;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResult>> GetSymbolImportanceAsync(
        string repoKey, int topK = 20, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        return index.Symbols.Values
            .OrderByDescending(s => s.CentralityScore)
            .Take(topK)
            .Select(s => new SearchResult { Symbol = s, Score = s.CentralityScore, MatchType = SearchMatchType.Exact })
            .ToList();
    }

    public async Task<IReadOnlyList<DeadSymbol>> FindDeadCodeAsync(
        string repoKey, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        return _deadCode.Detect(index);
    }

    public async Task<ChangedSymbols> GetChangedSymbolsAsync(
        string repoKey, string fromCommit, string toCommit, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        return _gitDiff.Analyze(index, repoKey, fromCommit, toCommit);
    }

    public async Task<IReadOnlyList<Symbol>> GetBlastRadiusAsync(
        string repoKey, string symbolId, int depth = 3, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        return _blastRadius.Calculate(index, symbolId, depth);
    }

    private async Task<CodeIndex> RequireAsync(string repoKey, CancellationToken ct)
    {
        var index = await _store.LoadAsync(repoKey, ct);
        if (index == null) throw new InvalidOperationException($"No index for '{repoKey}'");
        return index;
    }
}
