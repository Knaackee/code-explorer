using CodeExplorer.Core.Models;
using FuzzySharp;
using Microsoft.Extensions.Logging;

namespace CodeExplorer.Core.Search;

/// <summary>
/// BM25 ranking over symbol names and summaries.
/// Custom implementation optimised for code symbol corpora (short documents).
/// </summary>
public sealed class BM25Engine
{
    // BM25 parameters — tuned for code symbol corpora
    private const float K1 = 1.2f;
    private const float B  = 0.4f; // lower than default (0.75) because code names are short

    public IReadOnlyList<(Symbol Symbol, double Score)> Rank(
        string query, IEnumerable<Symbol> corpus)
    {
        var symbols = corpus.ToList();
        if (symbols.Count == 0) return [];

        var queryTokens = Tokenize(query);
        if (queryTokens.Length == 0) return [];

        // Build inverse document frequency per token
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var docs = symbols.Select(s => new { s, tokens = Tokenize(GetDocument(s)) }).ToList();

        foreach (var doc in docs)
            foreach (var token in doc.tokens.Distinct())
                df[token] = df.GetValueOrDefault(token) + 1;

        int N = symbols.Count;
        double avgDocLen = docs.Average(d => (double)d.tokens.Length);

        var results = new List<(Symbol, double)>();

        foreach (var doc in docs)
        {
            double score = 0;
            var tf = doc.tokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            int docLen = doc.tokens.Length;

            foreach (var qToken in queryTokens)
            {
                if (!df.TryGetValue(qToken, out int dfVal)) continue;

                double idf = Math.Log((N - dfVal + 0.5) / (dfVal + 0.5) + 1);
                double termFreq = tf.GetValueOrDefault(qToken, 0);
                double tfNorm = (termFreq * (K1 + 1)) /
                                (termFreq + K1 * (1 - B + B * docLen / avgDocLen));
                score += idf * tfNorm;
            }

            if (score > 0) results.Add((doc.s, score));
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }

    private static string GetDocument(Symbol s) =>
        $"{s.Name} {s.QualifiedName} {s.Summary} {string.Join(" ", s.Keywords)}";

    public static string[] Tokenize(string text) =>
        text.Split([' ', '_', '-', '.', '/', '(', ')', '{', '}', '\n', '\r', '\t', ':'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 1)
            .ToArray();
}

/// <summary>Fuzzy symbol name matching using Levenshtein ratio.</summary>
public sealed class FuzzySearchEngine
{
    private readonly int _threshold;

    public FuzzySearchEngine(int threshold = 70)
    {
        _threshold = threshold;
    }

    public IReadOnlyList<(Symbol Symbol, double Score)> Search(
        string query, IEnumerable<Symbol> corpus)
    {
        var results = new List<(Symbol, double)>();
        var q = query.ToLowerInvariant();

        foreach (var symbol in corpus)
        {
            int ratio = Fuzz.PartialRatio(q, symbol.Name.ToLowerInvariant());
            if (ratio >= _threshold)
                results.Add((symbol, ratio / 100.0));
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }
}

/// <summary>
/// Hybrid search: BM25 + Fuzzy + optional Semantic.
/// Scores are normalized and combined via Reciprocal Rank Fusion.
/// </summary>
public sealed class HybridSearchEngine
{
    private readonly BM25Engine _bm25;
    private readonly FuzzySearchEngine _fuzzy;
    private readonly ILogger<HybridSearchEngine> _logger;

    public HybridSearchEngine(
        BM25Engine bm25,
        FuzzySearchEngine fuzzy,
        ILogger<HybridSearchEngine> logger)
    {
        _bm25 = bm25;
        _fuzzy = fuzzy;
        _logger = logger;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, IEnumerable<Symbol> corpus,
        SymbolKind? kindFilter = null, string? languageFilter = null,
        int topK = 20, CancellationToken ct = default)
    {
        var symbols = corpus
            .Where(s => kindFilter == null || s.Kind == kindFilter)
            .Where(s => languageFilter == null || s.Language.Equals(languageFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (symbols.Count == 0) return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        // BM25
        var bm25Results = _bm25.Rank(query, symbols)
            .ToDictionary(r => r.Symbol.Id, r => r.Score);

        // Fuzzy
        var fuzzyResults = _fuzzy.Search(query, symbols)
            .ToDictionary(r => r.Symbol.Id, r => r.Score);

        // Reciprocal Rank Fusion (RRF) with k=60
        const int rrf_k = 60;
        var allIds = bm25Results.Keys.Union(fuzzyResults.Keys).ToHashSet();

        var rrfScores = new Dictionary<string, double>();

        void AddRrf(IReadOnlyList<string> ranked, double weight)
        {
            for (int i = 0; i < ranked.Count; i++)
                rrfScores[ranked[i]] = rrfScores.GetValueOrDefault(ranked[i]) + weight / (rrf_k + i + 1);
        }

        AddRrf(bm25Results.OrderByDescending(x => x.Value).Select(x => x.Key).ToList(), 1.0);
        AddRrf(fuzzyResults.OrderByDescending(x => x.Value).Select(x => x.Key).ToList(), 0.5);

        var symbolMap = symbols.ToDictionary(s => s.Id);

        return Task.FromResult<IReadOnlyList<SearchResult>>(rrfScores
            .Where(kv => symbolMap.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => new SearchResult
            {
                Symbol = symbolMap[kv.Key],
                Score = kv.Value,
                MatchType = bm25Results.ContainsKey(kv.Key) ? SearchMatchType.BM25
                          : SearchMatchType.Fuzzy,
            })
            .ToList());
    }
}

/// <summary>Full-text search across raw source files.</summary>
public sealed class TextSearchEngine
{
    public IReadOnlyList<SearchResult> SearchText(
        string query, IReadOnlyDictionary<string, Symbol> symbols, Func<string, string?> getSource)
    {
        var results = new List<SearchResult>();
        var q = query.ToLowerInvariant();

        foreach (var (id, symbol) in symbols)
        {
            var source = getSource(symbol.FilePath)?.ToLowerInvariant();
            if (source == null) continue;

            int count = 0;
            int idx = 0;
            while ((idx = source.IndexOf(q, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += q.Length;
            }

            if (count > 0)
                results.Add(new SearchResult { Symbol = symbol, Score = count, MatchType = SearchMatchType.Exact });
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }
}
