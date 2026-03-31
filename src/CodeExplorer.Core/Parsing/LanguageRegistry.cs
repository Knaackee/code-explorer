using CodeExplorer.Core.Models;

namespace CodeExplorer.Core.Parsing;

/// <summary>Defines how symbols are extracted from a specific language's AST.</summary>
public sealed class LanguageSpec
{
    public required string Name { get; init; }
    public required string[] Extensions { get; init; }

    /// <summary>tree-sitter query patterns per symbol kind.</summary>
    public required IReadOnlyDictionary<SymbolKind, string[]> NodeTypes { get; init; }

    /// <summary>How to extract qualified name (e.g. "ClassName.methodName").</summary>
    public Func<string, string, string>? QualifiedNameBuilder { get; init; }
}

/// <summary>Registry of all supported language specifications.</summary>
public static class LanguageRegistry
{
    public static readonly IReadOnlyDictionary<string, LanguageSpec> All =
        new Dictionary<string, LanguageSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = new LanguageSpec
            {
                Name = "python",
                Extensions = [".py"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition"],
                    [SymbolKind.Class]     = ["class_definition"],
                    [SymbolKind.Method]    = ["function_definition"], // inside class body
                    [SymbolKind.Constant]  = ["assignment"],          // ALL_CAPS convention
                },
            },
            ["javascript"] = new LanguageSpec
            {
                Name = "javascript",
                Extensions = [".js", ".jsx", ".mjs", ".cjs"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_declaration", "arrow_function", "function_expression"],
                    [SymbolKind.Class]     = ["class_declaration", "class_expression"],
                    [SymbolKind.Method]    = ["method_definition"],
                    [SymbolKind.Constant]  = ["lexical_declaration"],
                },
            },
            ["typescript"] = new LanguageSpec
            {
                Name = "typescript",
                Extensions = [".ts", ".tsx", ".mts", ".cts"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_declaration", "arrow_function"],
                    [SymbolKind.Class]     = ["class_declaration"],
                    [SymbolKind.Method]    = ["method_definition"],
                    [SymbolKind.Interface] = ["interface_declaration"],
                    [SymbolKind.Type]      = ["type_alias_declaration"],
                    [SymbolKind.Enum]      = ["enum_declaration"],
                    [SymbolKind.Constant]  = ["lexical_declaration"],
                },
            },
            ["go"] = new LanguageSpec
            {
                Name = "go",
                Extensions = [".go"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_declaration"],
                    [SymbolKind.Method]    = ["method_declaration"],
                    [SymbolKind.Type]      = ["type_declaration"],
                    [SymbolKind.Constant]  = ["const_declaration"],
                },
            },
            ["rust"] = new LanguageSpec
            {
                Name = "rust",
                Extensions = [".rs"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_item"],
                    [SymbolKind.Type]      = ["struct_item", "enum_item", "trait_item"],
                    [SymbolKind.Constant]  = ["const_item"],
                },
            },
            ["java"] = new LanguageSpec
            {
                Name = "java",
                Extensions = [".java"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Class]     = ["class_declaration", "interface_declaration", "enum_declaration"],
                    [SymbolKind.Method]    = ["method_declaration", "constructor_declaration"],
                    [SymbolKind.Field]     = ["field_declaration"],
                },
            },
            ["php"] = new LanguageSpec
            {
                Name = "php",
                Extensions = [".php"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition"],
                    [SymbolKind.Class]     = ["class_declaration", "interface_declaration", "trait_declaration"],
                    [SymbolKind.Method]    = ["method_declaration"],
                    [SymbolKind.Constant]  = ["const_declaration"],
                },
            },
            ["csharp"] = new LanguageSpec
            {
                Name = "csharp",
                Extensions = [".cs"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Class]     = ["class_declaration", "record_declaration"],
                    [SymbolKind.Interface] = ["interface_declaration"],
                    [SymbolKind.Enum]      = ["enum_declaration"],
                    [SymbolKind.Method]    = ["method_declaration", "constructor_declaration"],
                    [SymbolKind.Property]  = ["property_declaration"],
                    [SymbolKind.Field]     = ["field_declaration"],
                    [SymbolKind.Constant]  = ["field_declaration"], // const modifier
                },
            },
            ["c"] = new LanguageSpec
            {
                Name = "c",
                Extensions = [".c", ".h"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition"],
                    [SymbolKind.Type]      = ["struct_specifier", "enum_specifier", "union_specifier", "type_definition"],
                    [SymbolKind.Constant]  = ["preproc_def"],
                },
            },
            ["cpp"] = new LanguageSpec
            {
                Name = "cpp",
                Extensions = [".cpp", ".hpp", ".cc", ".cxx", ".hh"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition"],
                    [SymbolKind.Class]     = ["class_specifier", "struct_specifier"],
                    [SymbolKind.Method]    = ["function_definition"],
                    [SymbolKind.Type]      = ["enum_specifier", "alias_declaration"],
                    [SymbolKind.Field]     = ["field_declaration"],
                },
            },
            ["ruby"] = new LanguageSpec
            {
                Name = "ruby",
                Extensions = [".rb", ".rake", ".gemspec"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["method", "singleton_method"],
                    [SymbolKind.Class]     = ["class"],
                    [SymbolKind.Method]    = ["method", "singleton_method"],
                    [SymbolKind.Module]    = ["module"],
                    [SymbolKind.Constant]  = ["assignment"],
                },
            },
            ["swift"] = new LanguageSpec
            {
                Name = "swift",
                Extensions = [".swift"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_declaration"],
                    [SymbolKind.Class]     = ["class_declaration"],
                    [SymbolKind.Method]    = ["function_declaration"],
                    [SymbolKind.Type]      = ["struct_declaration", "protocol_declaration"],
                    [SymbolKind.Enum]      = ["enum_declaration"],
                    [SymbolKind.Property]  = ["property_declaration"],
                },
            },
            ["scala"] = new LanguageSpec
            {
                Name = "scala",
                Extensions = [".scala", ".sc"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition"],
                    [SymbolKind.Class]     = ["class_definition", "object_definition"],
                    [SymbolKind.Method]    = ["function_definition"],
                    [SymbolKind.Type]      = ["trait_definition", "type_definition"],
                },
            },
            ["haskell"] = new LanguageSpec
            {
                Name = "haskell",
                Extensions = [".hs"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function", "signature"],
                    [SymbolKind.Type]      = ["type_alias", "algebraic_datatype", "newtype"],
                    [SymbolKind.Class]     = ["type_class_declaration"],
                },
            },
            ["julia"] = new LanguageSpec
            {
                Name = "julia",
                Extensions = [".jl"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition", "short_function_definition"],
                    [SymbolKind.Type]      = ["struct_definition", "abstract_definition"],
                    [SymbolKind.Module]    = ["module_definition"],
                    [SymbolKind.Constant]  = ["const_statement"],
                },
            },
            ["ocaml"] = new LanguageSpec
            {
                Name = "ocaml",
                Extensions = [".ml", ".mli"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["value_definition"],
                    [SymbolKind.Type]      = ["type_definition"],
                    [SymbolKind.Module]    = ["module_definition"],
                },
            },
            ["bash"] = new LanguageSpec
            {
                Name = "bash",
                Extensions = [".sh", ".bash"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_definition"],
                },
            },
            ["html"] = new LanguageSpec
            {
                Name = "html",
                Extensions = [".html", ".htm"],
                NodeTypes = new Dictionary<SymbolKind, string[]>(), // markup — no symbol extraction
            },
            ["css"] = new LanguageSpec
            {
                Name = "css",
                Extensions = [".css"],
                NodeTypes = new Dictionary<SymbolKind, string[]>(), // styling — no symbol extraction
            },
            ["json"] = new LanguageSpec
            {
                Name = "json",
                Extensions = [".json"],
                NodeTypes = new Dictionary<SymbolKind, string[]>(), // data format — no symbol extraction
            },
            ["toml"] = new LanguageSpec
            {
                Name = "toml",
                Extensions = [".toml"],
                NodeTypes = new Dictionary<SymbolKind, string[]>(), // config format — no symbol extraction
            },
            ["agda"] = new LanguageSpec
            {
                Name = "agda",
                Extensions = [".agda"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_clause"],
                    [SymbolKind.Type]      = ["data", "record"],
                },
            },
            ["ql"] = new LanguageSpec
            {
                Name = "ql",
                Extensions = [".ql", ".qll"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["predicate"],
                    [SymbolKind.Class]     = ["dataclass"],
                    [SymbolKind.Module]    = ["module"],
                },
            },
            ["verilog"] = new LanguageSpec
            {
                Name = "verilog",
                Extensions = [".sv", ".v", ".svh"],
                NodeTypes = new Dictionary<SymbolKind, string[]>
                {
                    [SymbolKind.Function]  = ["function_declaration"],
                    [SymbolKind.Method]    = ["task_declaration"],
                    [SymbolKind.Module]    = ["module_declaration"],
                },
            },
            ["razor"] = new LanguageSpec
            {
                Name = "razor",
                Extensions = [".razor", ".cshtml"],
                NodeTypes = new Dictionary<SymbolKind, string[]>(), // template — symbols extracted from embedded C#
            },
        };

    public static LanguageSpec? ForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return All.Values.FirstOrDefault(s => s.Extensions.Contains(ext));
    }
}

/// <summary>Default language detector using LanguageRegistry extension map.</summary>
public sealed class DefaultLanguageDetector : ILanguageDetector
{
    public string? DetectLanguage(string filePath) =>
        LanguageRegistry.ForFile(filePath)?.Name;

    public IReadOnlyList<string> SupportedLanguages =>
        LanguageRegistry.All.Keys.ToList();
}
