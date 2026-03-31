using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using TreeSitter;

namespace CodeExplorer.Core.Parsing;

/// <summary>
/// Walks tree-sitter ASTs and extracts symbols with byte offsets.
/// This is the core parsing intelligence — no third-party package does this.
/// </summary>
public sealed class SymbolExtractor
{
    private static readonly ConcurrentDictionary<string, bool> GrammarAvailableByLanguageId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> MissingGrammarLogged = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<SymbolExtractor> _logger;

    public SymbolExtractor(ILogger<SymbolExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts all symbols from source code using tree-sitter.
    /// Falls back to regex-based heuristics if tree-sitter unavailable for language.
    /// </summary>
    public IReadOnlyList<Symbol> Extract(
        string source, string filePath, string language, LanguageSpec spec)
    {
        var symbols = new List<Symbol>();

        if (!TreeSitterLanguageIds.TryGetValue(language, out var tsId))
        {
            ExtractHeuristic(source, filePath, language, spec, symbols);
            AssignParents(symbols);
            return symbols;
        }

        if (!IsGrammarAvailable(tsId))
        {
            if (MissingGrammarLogged.TryAdd(tsId, 0))
            {
                _logger.LogWarning(
                    "tree-sitter native grammar library missing: {Library}. Falling back to heuristic extraction.",
                    GetGrammarLibraryFileName(tsId));
            }

            ExtractHeuristic(source, filePath, language, spec, symbols);
            AssignParents(symbols);
            return symbols;
        }

        try
        {
            // tree-sitter parsing via TreeSitter.DotNet
            // In real implementation: use TreeSitter.Language + Parser
            // Here we implement the full extraction logic with proper byte tracking
            ExtractWithTreeSitter(source, filePath, language, tsId, spec, symbols);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tree-sitter extraction failed for {File}, falling back to heuristic", filePath);
            ExtractHeuristic(source, filePath, language, spec, symbols);
        }

        // Build parent-child relationships
        AssignParents(symbols);

        return symbols;
    }

    private static bool IsGrammarAvailable(string treeSitterLanguageId)
    {
        return GrammarAvailableByLanguageId.GetOrAdd(treeSitterLanguageId, static id =>
        {
            var libraryName = GetGrammarLibraryFileName(id);
            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(libraryName, typeof(Language).Assembly, null);
            }
            catch
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        });
    }

    private static string GetGrammarLibraryFileName(string treeSitterLanguageId)
    {
        var extension = OperatingSystem.IsWindows()
            ? ".dll"
            : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

        return $"tree-sitter-{treeSitterLanguageId}{extension}";
    }

    private static readonly Dictionary<string, string> TreeSitterLanguageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "python",
        ["javascript"] = "javascript",
        ["typescript"] = "typescript",
        ["go"] = "go",
        ["rust"] = "rust",
        ["java"] = "java",
        ["php"] = "php",
        ["csharp"] = "c-sharp",
        ["c"] = "c",
        ["cpp"] = "cpp",
        ["ruby"] = "ruby",
        ["swift"] = "swift",
        ["scala"] = "scala",
        ["haskell"] = "haskell",
        ["julia"] = "julia",
        ["ocaml"] = "ocaml",
        ["bash"] = "bash",
        ["html"] = "html",
        ["css"] = "css",
        ["json"] = "json",
        ["toml"] = "toml",
        ["agda"] = "agda",
        ["ql"] = "ql",
        ["verilog"] = "verilog",
        ["razor"] = "razor",
    };

    private static void ExtractWithTreeSitter(
        string source, string filePath, string language, string treeSitterLanguageId,
        LanguageSpec spec, List<Symbol> symbols)
    {
        // Build reverse map: node type string → SymbolKind
        var nodeTypeMap = new Dictionary<string, SymbolKind>(StringComparer.Ordinal);
        foreach (var (kind, nodeTypes) in spec.NodeTypes)
            foreach (var nt in nodeTypes)
                nodeTypeMap.TryAdd(nt, kind);

        // Node types that represent "containers" for parent tracking
        var containerTypes = new HashSet<string>(StringComparer.Ordinal);
        if (spec.NodeTypes.TryGetValue(SymbolKind.Class, out var classTypes))
            foreach (var ct in classTypes) containerTypes.Add(ct);
        if (spec.NodeTypes.TryGetValue(SymbolKind.Interface, out var ifaceTypes))
            foreach (var it in ifaceTypes) containerTypes.Add(it);
        if (spec.NodeTypes.TryGetValue(SymbolKind.Type, out var typeTypes))
            foreach (var tt in typeTypes) containerTypes.Add(tt);

        using var lang = new Language(treeSitterLanguageId);
        using var parser = new Parser(lang);
        using var tree = parser.Parse(source);

        var relativePath = filePath.Replace('\\', '/');

        WalkNode(tree.RootNode, source, relativePath, language, nodeTypeMap, containerTypes, symbols, parentId: null);
    }

    private static void WalkNode(
        TreeSitter.Node node, string source, string filePath, string language,
        Dictionary<string, SymbolKind> nodeTypeMap,
        HashSet<string> containerTypes,
        List<Symbol> symbols,
        string? parentId)
    {
        if (nodeTypeMap.TryGetValue(node.Type, out var kind))
        {
            var nameNode = node.GetChildForField("name");
            var name = nameNode?.Text ?? "";

            if (!string.IsNullOrEmpty(name))
            {
                // Determine qualified name
                var qualifiedName = parentId != null
                    ? $"{symbols.FirstOrDefault(s => s.Id == parentId)?.Name ?? ""}.{name}"
                    : name;

                // If this is a method-type node inside a class container, upgrade kind
                if (parentId != null && kind == SymbolKind.Function)
                    kind = SymbolKind.Method;

                var id = SymbolIdBuilder.Build(filePath, qualifiedName, kind);

                var startLine = node.StartPosition.Row + 1;  // tree-sitter is 0-based
                var endLine = node.EndPosition.Row + 1;
                var byteStart = (long)node.StartIndex;
                var byteEnd = (long)node.EndIndex;
                var symbolSource = source[node.StartIndex..Math.Min(node.EndIndex, source.Length)];
                var signature = ExtractSignature(symbolSource, kind);
                var docstring = ExtractDocstring(node, source, language);

                var symbol = new Symbol
                {
                    Id = id,
                    FilePath = filePath,
                    QualifiedName = qualifiedName,
                    Name = name,
                    Kind = kind,
                    Language = language,
                    Signature = signature,
                    ByteStart = byteStart,
                    ByteEnd = byteEnd,
                    StartLine = startLine,
                    EndLine = endLine,
                    ContentHash = ComputeHash(symbolSource),
                    ParentId = parentId,
                    Summary = docstring ?? string.Empty,
                };

                symbols.Add(symbol);

                // If this is a container, recurse with this symbol as parent
                if (containerTypes.Contains(node.Type))
                {
                    foreach (var child in node.NamedChildren)
                        WalkNode(child, source, filePath, language, nodeTypeMap, containerTypes, symbols, id);
                    return; // children already walked
                }
            }
        }

        // Recurse into children
        foreach (var child in node.NamedChildren)
            WalkNode(child, source, filePath, language, nodeTypeMap, containerTypes, symbols, parentId);
    }

    private static string? ExtractDocstring(TreeSitter.Node node, string source, string language)
    {
        switch (language)
        {
            case "python":
            {
                // Python docstrings: first expression_statement containing a string in the body
                var body = node.GetChildForField("body");
                if (body != null && body.NamedChildren.Count > 0)
                {
                    var first = body.NamedChildren[0];
                    if (first.Type == "expression_statement" && first.NamedChildren.Count > 0)
                    {
                        var expr = first.NamedChildren[0];
                        if (expr.Type == "string")
                        {
                            var text = expr.Text;
                            return text.Trim('"', '\'', ' ', '\n', '\r');
                        }
                    }
                }
                break;
            }
            default:
            {
                // For most C-style languages: look for a preceding comment sibling
                var prev = node.PreviousSibling;
                if (prev != null && prev.Type is "comment" or "documentation_comment" or "block_comment")
                {
                    var text = prev.Text;
                    return text.TrimStart('/', '*', ' ').TrimEnd('/', '*', ' ').Trim();
                }
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// Regex-based fallback extractor. Produces reasonable results for most languages.
    /// Used when tree-sitter native libs are unavailable for the target platform.
    /// </summary>
    private static void ExtractHeuristic(
        string source, string filePath, string language,
        LanguageSpec spec, List<Symbol> symbols)
    {
        var lines = source.Split('\n');
        var bytes = Encoding.UTF8.GetBytes(source);
        var lineOffsets = ComputeLineOffsets(bytes);

        var patterns = GetHeuristicPatterns(language);

        foreach (var (kind, pattern) in patterns)
        {
            var matches = pattern.Matches(source);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var nameGroup = m.Groups["name"];
                if (!nameGroup.Success) continue;

                var name = nameGroup.Value;
                var lineNum = GetLineNumber(source, m.Index);
                var byteStart = GetByteOffset(source, m.Index);
                var endPos = FindBlockEnd(source, m.Index + m.Length);
                var byteEnd = GetByteOffset(source, endPos);
                var symbolSource = source[m.Index..Math.Min(endPos, source.Length)];
                var signature = ExtractSignature(symbolSource, kind);

                var relativePath = filePath.Replace('\\', '/');
                var qualifiedName = BuildQualifiedName(relativePath, name, symbols, lineNum);
                var id = SymbolIdBuilder.Build(relativePath, qualifiedName, kind);

                symbols.Add(new Symbol
                {
                    Id = id,
                    FilePath = relativePath,
                    QualifiedName = qualifiedName,
                    Name = name,
                    Kind = kind,
                    Language = language,
                    Signature = signature,
                    ByteStart = byteStart,
                    ByteEnd = byteEnd,
                    StartLine = lineNum,
                    EndLine = GetLineNumber(source, endPos),
                    ContentHash = ComputeHash(symbolSource),
                });
            }
        }
    }

    private static Dictionary<SymbolKind, System.Text.RegularExpressions.Regex> GetHeuristicPatterns(string language) =>
        language.ToLowerInvariant() switch
        {
            "python" => new()
            {
                [SymbolKind.Function] = new(@"^def\s+(?<name>\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Class]    = new(@"^class\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "javascript" or "typescript" => new()
            {
                [SymbolKind.Function] = new(@"(?:^|\s)function\s+(?<name>\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Class]    = new(@"^class\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Method]   = new(@"^\s+(?<name>\w+)\s*\([^)]*\)\s*\{", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "go" => new()
            {
                [SymbolKind.Function] = new(@"^func\s+(?<name>\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Method]   = new(@"^func\s+\([^)]+\)\s+(?<name>\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "rust" => new()
            {
                [SymbolKind.Function] = new(@"^(?:pub\s+)?fn\s+(?<name>\w+)\s*[<(]", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^(?:pub\s+)?(?:struct|enum|trait)\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "java" or "csharp" => new()
            {
                [SymbolKind.Class]  = new(@"(?:public|private|protected|internal)?\s*(?:abstract|sealed)?\s*class\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Method] = new(@"(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]]+\s+(?<name>\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "c" or "cpp" => new()
            {
                [SymbolKind.Function] = new(@"^[\w*]+\s+(?<name>\w+)\s*\([^)]*\)\s*\{", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^(?:typedef\s+)?(?:struct|enum|union)\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "ruby" => new()
            {
                [SymbolKind.Function] = new(@"^\s*def\s+(?:self\.)?(?<name>\w+[?!=]?)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Class]    = new(@"^\s*class\s+(?<name>[A-Z]\w*)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Module]   = new(@"^\s*module\s+(?<name>[A-Z]\w*)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "swift" => new()
            {
                [SymbolKind.Function] = new(@"^\s*(?:public\s+|private\s+|internal\s+)?func\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Class]    = new(@"^\s*(?:public\s+|private\s+)?class\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^\s*(?:public\s+|private\s+)?(?:struct|protocol)\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Enum]     = new(@"^\s*(?:public\s+|private\s+)?enum\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "scala" => new()
            {
                [SymbolKind.Function] = new(@"^\s*def\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Class]    = new(@"^\s*(?:case\s+)?(?:class|object)\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^\s*trait\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "haskell" => new()
            {
                [SymbolKind.Function] = new(@"^(?<name>[a-z]\w*)\s+::", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^(?:data|newtype|type)\s+(?<name>[A-Z]\w*)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Class]    = new(@"^class\s+(?<name>[A-Z]\w*)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "julia" => new()
            {
                [SymbolKind.Function] = new(@"^\s*function\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^\s*(?:mutable\s+)?struct\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Module]   = new(@"^\s*module\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "ocaml" => new()
            {
                [SymbolKind.Function] = new(@"^let\s+(?:rec\s+)?(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Type]     = new(@"^type\s+(?<name>\w+)", System.Text.RegularExpressions.RegexOptions.Multiline),
                [SymbolKind.Module]   = new(@"^module\s+(?<name>[A-Z]\w*)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            "bash" => new()
            {
                [SymbolKind.Function] = new(@"^(?:function\s+)?(?<name>\w+)\s*\(\s*\)", System.Text.RegularExpressions.RegexOptions.Multiline),
            },
            _ => []
        };

    private static long[] ComputeLineOffsets(byte[] bytes)
    {
        var offsets = new List<long> { 0L };
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == '\n') offsets.Add(i + 1);
        return [.. offsets];
    }

    private static int GetLineNumber(string source, int charIndex)
    {
        int line = 1;
        for (int i = 0; i < charIndex && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }

    private static long GetByteOffset(string source, int charIndex) =>
        Encoding.UTF8.GetByteCount(source.AsSpan(0, Math.Min(charIndex, source.Length)));

    private static int FindBlockEnd(string source, int start)
    {
        int depth = 0;
        bool inBlock = false;
        for (int i = start; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; inBlock = true; }
            else if (source[i] == '}')
            {
                depth--;
                if (inBlock && depth == 0) return i + 1;
            }
        }
        // Fallback: next empty line or end
        int eol = source.IndexOf("\n\n", start);
        return eol > 0 ? eol : source.Length;
    }

    private static string ExtractSignature(string source, SymbolKind kind)
    {
        var firstLine = source.Split('\n')[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] + "..." : firstLine;
    }

    private static string BuildQualifiedName(string filePath, string name, List<Symbol> existing, int line)
    {
        // Find enclosing class if any
        var enclosing = existing
            .Where(s => s.Kind == SymbolKind.Class && s.StartLine < line)
            .OrderByDescending(s => s.StartLine)
            .FirstOrDefault();

        return enclosing != null ? $"{enclosing.Name}.{name}" : name;
    }

    private static void AssignParents(List<Symbol> symbols)
    {
        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            if (symbol.ParentId != null) continue; // already assigned (tree-sitter path)
            if (symbol.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Field))
                continue;

            var parent = symbols
                .Where(s => s.Kind == SymbolKind.Class
                         && s.StartLine <= symbol.StartLine
                         && s.EndLine >= symbol.EndLine
                         && s.FilePath == symbol.FilePath)
                .OrderByDescending(s => s.StartLine)
                .FirstOrDefault();

            if (parent != null)
            {
                // Reconstruct with ParentId set (init-only property)
                symbols[i] = new Symbol
                {
                    Id = symbol.Id,
                    FilePath = symbol.FilePath,
                    QualifiedName = symbol.QualifiedName,
                    Name = symbol.Name,
                    Kind = symbol.Kind,
                    Language = symbol.Language,
                    Signature = symbol.Signature,
                    ByteStart = symbol.ByteStart,
                    ByteEnd = symbol.ByteEnd,
                    StartLine = symbol.StartLine,
                    EndLine = symbol.EndLine,
                    ContentHash = symbol.ContentHash,
                    ParentId = parent.Id,
                    References = symbol.References,
                    Keywords = symbol.Keywords,
                };
            }
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }
}

/// <summary>Generates stable symbol IDs.</summary>
public static class SymbolIdBuilder
{
    /// <summary>
    /// Builds a stable symbol ID: "relative/path.ext::qualified.name#kind"
    /// Stable as long as path, qualified name, and kind are unchanged.
    /// </summary>
    public static string Build(string relativePath, string qualifiedName, SymbolKind kind) =>
        $"{relativePath}::{qualifiedName}#{kind.ToString().ToLowerInvariant()}";

    public static (string FilePath, string QualifiedName, string Kind) Parse(string id)
    {
        var colonIdx = id.IndexOf("::", StringComparison.Ordinal);
        var hashIdx  = id.LastIndexOf('#');
        return (
            id[..colonIdx],
            id[(colonIdx + 2)..hashIdx],
            id[(hashIdx + 1)..]
        );
    }
}
