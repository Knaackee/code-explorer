# CodeExplorer

Token-efficient .NET library and CLI for codebase exploration via tree-sitter AST parsing.

**Index once. Query cheaply. Let your LLM focus on reasoning, not searching.**

> Originally inspired by the [DotNetCodeMunch](https://github.com/you/DotNetCodeMunch) project.

---

## What it does

Traditional AI agents exploring a codebase open entire files and flood their context window with irrelevant code. CodeExplorer indexes a codebase once using tree-sitter AST parsing, then lets you retrieve exactly the symbols you need — with O(1) byte-offset seeking, BM25 + fuzzy search, and full DI-friendliness.

No LLM required. This is a pure retrieval engine designed to be _consumed_ by an LLM — not to _contain_ one.

| Task | Brute-force | CodeExplorer |
|---|---|---|
| Find a function | ~40,000 tokens | ~200 tokens |
| Understand module API | ~15,000 tokens | ~800 tokens |
| Explore repo structure | ~200,000 tokens | ~2,000 tokens |

---

## Artefacts

| Artefact | Description |
|---|---|
| `CodeExplorer.Core` | NuGet library — all features, DI-friendly, no CLI dependency |
| `cxp` | Self-contained CLI executable (Win/Linux/macOS, no .NET install required) |

---

## Quick Start

### Library

```bash
dotnet add package CodeExplorer.Core
```

```csharp
services.AddCodeExplorer(options =>
{
    options.IndexPath        = "~/.code-index";
    options.MaxFileSizeBytes = 1_000_000;
});

var indexer   = sp.GetRequiredService<ICodeIndexer>();
var retriever = sp.GetRequiredService<ISymbolRetriever>();

await indexer.IndexRepoAsync("fastapi/fastapi");
var results = await retriever.SearchSymbolsAsync("fastapi/fastapi", "authenticate", kind: SymbolKind.Function);
var source  = await retriever.GetSymbolSourceAsync("fastapi/fastapi", results[0].Symbol.Id);
```

### CLI

```bash
cxp index repo fastapi/fastapi
cxp index folder ./my-project

cxp search symbols --repo fastapi/fastapi "authenticate" --kind function
cxp search text    --repo fastapi/fastapi "TODO"

cxp get symbol  --repo fastapi/fastapi "fastapi/security/oauth2.py::OAuth2.authenticate#method"
cxp get context --repo fastapi/fastapi "auth flow" --budget 4000

cxp outline repo  fastapi/fastapi
cxp outline file  fastapi/fastapi --path fastapi/main.py

cxp analyze importance   fastapi/fastapi
cxp analyze dead-code    fastapi/fastapi
cxp analyze blast-radius fastapi/fastapi "fastapi/routing.py::APIRouter.add_api_route#method"

cxp list
cxp invalidate fastapi/fastapi
```

---

## Architecture

```
CodeExplorer/
├── src/
│   ├── CodeExplorer.Core/
│   │   ├── Models/                  # Symbol, CodeIndex, SearchResult, ...
│   │   ├── Parsing/                 # tree-sitter AST + SymbolExtractor + LanguageRegistry
│   │   ├── Storage/                 # IIndexStore + JsonFileIndexStore
│   │   ├── Search/                  # BM25Engine + FuzzySearchEngine + HybridSearchEngine
│   │   ├── Summarizer/              # ISymbolSummarizer + SignatureFallbackSummarizer
│   │   ├── Retrieval/               # IRepositoryClient + OctokitRepositoryClient + LocalFolderClient
│   │   ├── Security/                # ISecurityFilter + DefaultSecurityFilter
│   │   ├── Analysis/                # PageRank + DeadCode + BlastRadius + GitDiff
│   │   ├── DependencyInjection/     # ServiceCollectionExtensions (AddCodeExplorer)
│   │   ├── CodeIndexer.cs           # ICodeIndexer orchestrator
│   │   ├── OutlineProvider.cs       # IOutlineProvider
│   │   └── Interfaces.cs            # All public interfaces
│   └── CodeExplorer.Cli/
│       ├── Commands/                # All Spectre.Console CLI commands
│       └── Program.cs
└── tests/
    ├── CodeExplorer.Core.Tests/     # xUnit + FluentAssertions + NSubstitute
    └── CodeExplorer.Benchmarks/     # BenchmarkDotNet
```

---

## Swappable Components

Every meaningful dependency is behind an interface and registered as a singleton in DI:

| Interface | Default | When to replace |
|---|---|---|
| `ISymbolSummarizer` | `SignatureFallbackSummarizer` | Custom summary extraction logic |
| `IRepositoryClient` | `OctokitRepositoryClient` | Custom GitHub auth, GitLab, Gitea, etc. |
| `IIndexStore` | `JsonFileIndexStore` | Store index in a database |
| `ISecurityFilter` | `DefaultSecurityFilter` | Custom file exclusion rules |
| `ILanguageDetector` | `DefaultLanguageDetector` | Add custom language extensions |

---

## Credits

This project was originally developed as **DotNetCodeMunch** and has been renamed to **CodeExplorer** (`cxp`). The core architecture — tree-sitter AST parsing, BM25/fuzzy search, byte-offset retrieval, and DI-based extensibility — originates from the CodeMunch concept.

---

## Supported Languages

All grammars ship with [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — no extra native libraries needed.

### Programming Languages

| Language | Extensions | Symbol types |
|---|---|---|
| Python | `.py` | function, class, method, constant |
| JavaScript | `.js`, `.jsx`, `.mjs`, `.cjs` | function, class, method, constant |
| TypeScript | `.ts`, `.tsx`, `.mts`, `.cts` | function, class, method, interface, type, enum, constant |
| Go | `.go` | function, method, type, constant |
| Rust | `.rs` | function, type, constant |
| Java | `.java` | class, method, field |
| PHP | `.php` | function, class, method, constant |
| C# | `.cs` | class, interface, enum, method, property, field, constant |
| C | `.c`, `.h` | function, type, constant |
| C++ | `.cpp`, `.hpp`, `.cc`, `.cxx`, `.hh` | function, class, method, type, field |
| Ruby | `.rb`, `.rake`, `.gemspec` | function, class, method, module, constant |
| Swift | `.swift` | function, class, method, type, enum, property |
| Scala | `.scala`, `.sc` | function, class, method, type |
| Haskell | `.hs` | function, type, class |
| Julia | `.jl` | function, type, module, constant |
| OCaml | `.ml`, `.mli` | function, type, module |
| Bash | `.sh`, `.bash` | function |
| Agda | `.agda` | function, type |
| CodeQL | `.ql`, `.qll` | function, class, module |
| SystemVerilog | `.sv`, `.v`, `.svh` | function, method, module |

### Markup, Data & Template Formats

| Language | Extensions | Notes |
|---|---|---|
| HTML | `.html`, `.htm` | Language detection & AST parsing (no symbol extraction) |
| CSS | `.css` | Language detection & AST parsing (no symbol extraction) |
| JSON | `.json` | Language detection & AST parsing (no symbol extraction) |
| TOML | `.toml` | Language detection & AST parsing (no symbol extraction) |
| Razor | `.razor`, `.cshtml` | Language detection (symbols extracted from embedded C#) |

---

## Build & Test

```bash
# Build
dotnet build

# Run all unit tests
dotnet test tests/CodeExplorer.Core.Tests

# Run benchmarks (Release mode required)
dotnet run --project tests/CodeExplorer.Benchmarks -c Release

# Publish self-contained CLI
dotnet publish src/CodeExplorer.Cli -r linux-x64   -c Release --self-contained -p:PublishSingleFile=true
dotnet publish src/CodeExplorer.Cli -r win-x64     -c Release --self-contained -p:PublishSingleFile=true
dotnet publish src/CodeExplorer.Cli -r osx-arm64   -c Release --self-contained -p:PublishSingleFile=true
```

---

## Environment Variables

| Variable | Purpose |
|---|---|
| `GITHUB_TOKEN` | Raises GitHub API rate limits and enables private repos |
| `CODE_INDEX_PATH` | Override default index path (`~/.code-index`) |

---

## License

MIT
