using CodeExplorer.Core.Models;

namespace CodeExplorer.Core.Summarizer;

// ─── Signature Fallback (no network) ─────────────────────────────────────────

/// <summary>
/// Extracts a summary from the docstring or signature alone.
/// Zero network calls — always available.
/// </summary>
public sealed class SignatureFallbackSummarizer : ISymbolSummarizer
{
    public Task<string> SummarizeAsync(Symbol symbol, string? sourceContext = null, CancellationToken ct = default) =>
        Task.FromResult(ExtractSummary(symbol, sourceContext));

    internal static string ExtractSummary(Symbol symbol, string? sourceContext = null)
    {
        // 1. Try to extract docstring from source context
        if (sourceContext is { Length: > 0 })
        {
            var docstring = TryExtractDocstring(sourceContext, symbol.Language);
            if (!string.IsNullOrWhiteSpace(docstring)) return docstring;
        }

        // 2. Fallback: Signature or kind + name
        return string.IsNullOrWhiteSpace(symbol.Signature)
            ? $"{symbol.Kind} {symbol.QualifiedName}"
            : symbol.Signature;
    }

    private static string? TryExtractDocstring(string source, string language)
    {
        var lines = source.Split('\n');

        return language switch
        {
            "python" => lines.Skip(1)
                             .FirstOrDefault(l => l.TrimStart().StartsWith("\"\"\"") ||
                                                  l.TrimStart().StartsWith("'''"))
                             ?.Trim().TrimStart('"', '\''),

            "javascript" or "typescript" or "java" or "csharp" =>
                lines.FirstOrDefault(l => l.TrimStart().StartsWith("//") ||
                                          l.TrimStart().StartsWith("/*") ||
                                          l.TrimStart().StartsWith("///"))
                     ?.Trim().TrimStart('/', '*', ' '),

            "go" or "rust" =>
                lines.FirstOrDefault(l => l.TrimStart().StartsWith("//"))
                     ?.Trim().TrimStart('/', ' '),

            _ => null
        };
    }
}
