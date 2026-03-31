using CodeExplorer.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodeExplorer.Core;

public sealed class OutlineProvider : IOutlineProvider
{
    private readonly IIndexStore _store;

    public OutlineProvider(IIndexStore store) => _store = store;

    public async Task<IReadOnlyList<SymbolNode>> GetRepoOutlineAsync(string repoKey, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        return BuildHierarchy(index.Symbols.Values);
    }

    public async Task<IReadOnlyList<SymbolNode>> GetFileOutlineAsync(
        string repoKey, string filePath, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        var fileSymbols = index.FileSymbols.TryGetValue(filePath, out var ids)
            ? ids.Select(id => index.Symbols.TryGetValue(id, out var s) ? s : null).OfType<Symbol>()
            : [];
        return BuildHierarchy(fileSymbols);
    }

    public async Task<IReadOnlyList<string>> GetFileTreeAsync(
        string repoKey, string? pathPrefix = null, CancellationToken ct = default)
    {
        var index = await RequireAsync(repoKey, ct);
        return index.FileSymbols.Keys
            .Where(p => pathPrefix == null || p.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p)
            .ToList();
    }

    private static IReadOnlyList<SymbolNode> BuildHierarchy(IEnumerable<Symbol> symbols)
    {
        var all = symbols.OrderBy(s => s.StartLine).ToList();
        var nodes = all.ToDictionary(s => s.Id, s => new SymbolNode { Symbol = s });
        var roots = new List<SymbolNode>();

        foreach (var node in nodes.Values)
        {
            if (node.Symbol.ParentId != null && nodes.TryGetValue(node.Symbol.ParentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        return roots;
    }

    private async Task<CodeIndex> RequireAsync(string repoKey, CancellationToken ct)
    {
        var index = await _store.LoadAsync(repoKey, ct);
        if (index == null) throw new InvalidOperationException($"No index for '{repoKey}'");
        return index;
    }
}
