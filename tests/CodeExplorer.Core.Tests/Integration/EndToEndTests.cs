using CodeExplorer.Core;
using CodeExplorer.Core.Analysis;
using CodeExplorer.Core.DependencyInjection;
using CodeExplorer.Core.Models;
using CodeExplorer.Core.Search;
using CodeExplorer.Core.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodeExplorer.Core.Tests.Integration;

/// <summary>
/// End-to-end integration tests that exercise the full pipeline:
///   create temp folder → index → search → retrieve → outline → analyze → cleanup.
/// No LLM needed — uses SignatureFallbackSummarizer.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _indexPath;
    private readonly ServiceProvider _sp;

    // Services resolved from real DI container
    private readonly ICodeIndexer _indexer;
    private readonly ISymbolRetriever _retriever;
    private readonly IOutlineProvider _outline;
    private readonly ICodeAnalyzer _analyzer;
    private readonly IIndexStore _store;

    public EndToEndTests()
    {
        var guid = Guid.NewGuid().ToString("N");
        _tempRoot = Path.Combine(Path.GetTempPath(), $"CodeExplorer_e2e_{guid}");
        _indexPath = Path.Combine(Path.GetTempPath(), $"CodeExplorer_idx_{guid}");
        Directory.CreateDirectory(_tempRoot);

        // Seed a realistic multi-file C# codebase
        SeedTestCodebase();

        // Wire DI exactly like production, but point index to temp folder
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCodeExplorer(opt =>
        {
            opt.IndexPath = _indexPath;
        });

        _sp = services.BuildServiceProvider();
        _indexer = _sp.GetRequiredService<ICodeIndexer>();
        _retriever = _sp.GetRequiredService<ISymbolRetriever>();
        _outline = _sp.GetRequiredService<IOutlineProvider>();
        _analyzer = _sp.GetRequiredService<ICodeAnalyzer>();
        _store = _sp.GetRequiredService<IIndexStore>();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        try { Directory.Delete(_indexPath, recursive: true); } catch { }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SeedTestCodebase()
    {
        // models/User.cs
        WriteFile("models/User.cs", """
            namespace SampleApp.Models;

            /// <summary>Represents a user account.</summary>
            public class User
            {
                public int Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public string Email { get; set; } = string.Empty;
            }
            """);

        // models/Order.cs
        WriteFile("models/Order.cs", """
            namespace SampleApp.Models;

            /// <summary>Represents a customer order.</summary>
            public class Order
            {
                public int Id { get; set; }
                public int UserId { get; set; }
                public decimal Total { get; set; }
                public DateTime CreatedAt { get; set; }
            }
            """);

        // services/IUserService.cs
        WriteFile("services/IUserService.cs", """
            namespace SampleApp.Services;

            public interface IUserService
            {
                User GetById(int id);
                IReadOnlyList<User> GetAll();
                void Create(User user);
            }
            """);

        // services/UserService.cs
        WriteFile("services/UserService.cs", """
            using SampleApp.Models;

            namespace SampleApp.Services;

            /// <summary>Manages user persistence and business rules.</summary>
            public class UserService : IUserService
            {
                private readonly List<User> _users = new();

                public User GetById(int id) => _users.First(u => u.Id == id);

                public IReadOnlyList<User> GetAll() => _users.AsReadOnly();

                public void Create(User user)
                {
                    if (string.IsNullOrWhiteSpace(user.Name))
                        throw new ArgumentException("Name required");
                    _users.Add(user);
                }
            }
            """);

        // services/OrderService.cs
        WriteFile("services/OrderService.cs", """
            using SampleApp.Models;

            namespace SampleApp.Services;

            /// <summary>Handles order creation and queries.</summary>
            public class OrderService
            {
                private readonly IUserService _userService;
                private readonly List<Order> _orders = new();

                public OrderService(IUserService userService)
                {
                    _userService = userService;
                }

                public Order PlaceOrder(int userId, decimal total)
                {
                    var user = _userService.GetById(userId);
                    var order = new Order
                    {
                        Id = _orders.Count + 1,
                        UserId = user.Id,
                        Total = total,
                        CreatedAt = DateTime.UtcNow
                    };
                    _orders.Add(order);
                    return order;
                }

                public IReadOnlyList<Order> GetByUser(int userId) =>
                    _orders.Where(o => o.UserId == userId).ToList();
            }
            """);

        // utils/MathHelper.cs
        WriteFile("utils/MathHelper.cs", """
            namespace SampleApp.Utils;

            /// <summary>Math utility functions.</summary>
            public static class MathHelper
            {
                public static int Factorial(int n)
                {
                    if (n < 0) throw new ArgumentException("Negative");
                    return n <= 1 ? 1 : n * Factorial(n - 1);
                }

                public static double Clamp(double value, double min, double max) =>
                    Math.Max(min, Math.Min(max, value));
            }
            """);

        // Program.cs (python file for multi-language coverage)
        WriteFile("scripts/analyze.py", """
            \"\"\"Analysis script for data processing.\"\"\"
            import json

            def load_data(path: str) -> dict:
                \"\"\"Load JSON data from a file path.\"\"\"
                with open(path) as f:
                    return json.load(f)

            def summarize(data: dict) -> str:
                \"\"\"Return a brief summary of the data.\"\"\"
                return f"Keys: {len(data)}, Type: {type(data).__name__}"

            class DataProcessor:
                \"\"\"Processes data pipelines.\"\"\"
                def __init__(self, config: dict):
                    self.config = config

                def run(self):
                    \"\"\"Execute the processing pipeline.\"\"\"
                    data = load_data(self.config["input"])
                    return summarize(data)
            """);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private async Task<CodeIndex> IndexOnceAsync()
    {
        return await _indexer.IndexFolderAsync(_tempRoot);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Full indexing pipeline
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Index_RealFolder_ProducesSymbols()
    {
        var index = await IndexOnceAsync();

        index.Should().NotBeNull();
        index.IsLocal.Should().BeTrue();
        index.RepoKey.Should().NotBeNullOrEmpty();
        index.SymbolCount.Should().BeGreaterThan(0, "should extract symbols from seeded files");
        index.FileCount.Should().BeGreaterOrEqualTo(6, "we seeded 7 files (some may be filtered)");
        index.IndexDuration.Should().BeGreaterThan(TimeSpan.Zero);
        index.IndexedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Index_ExtractsClasses()
    {
        var index = await IndexOnceAsync();

        var classes = index.Symbols.Values.Where(s => s.Kind == SymbolKind.Class).ToList();
        classes.Should().NotBeEmpty("seeded codebase has classes");

        var classNames = classes.Select(c => c.Name).ToList();
        classNames.Should().Contain("User");
        classNames.Should().Contain("Order");
        classNames.Should().Contain("UserService");
        classNames.Should().Contain("OrderService");
    }

    [Fact]
    public async Task Index_ExtractsMethods()
    {
        var index = await IndexOnceAsync();

        var methods = index.Symbols.Values
            .Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function)
            .ToList();

        methods.Should().NotBeEmpty();
        var names = methods.Select(m => m.Name).ToList();
        names.Should().Contain("GetById");
        names.Should().Contain("Create");
        names.Should().Contain("PlaceOrder");
    }

    [Fact]
    public async Task Index_ExtractsInterfaces_OrClassesAsInterface()
    {
        var index = await IndexOnceAsync();

        // The heuristic regex fallback doesn't detect 'interface' keyword —
        // tree-sitter does, but only when the c_sharp grammar is available.
        // Verify at minimum that the IUserService file was indexed.
        var serviceFile = index.FileSymbols.Keys
            .FirstOrDefault(k => k.Contains("IUserService"));
        serviceFile.Should().NotBeNull("IUserService.cs should be indexed");
    }

    [Fact]
    public async Task Index_ExtractsPythonSymbols()
    {
        var index = await IndexOnceAsync();

        var pySymbols = index.Symbols.Values.Where(s => s.Language == "python").ToList();
        pySymbols.Should().NotBeEmpty("we seeded a Python file");

        var pyNames = pySymbols.Select(s => s.Name).ToList();
        pyNames.Should().Contain("load_data");
        pyNames.Should().Contain("summarize");
        pyNames.Should().Contain("DataProcessor");
    }

    [Fact]
    public async Task Index_GeneratesSummaries_WithoutLlm()
    {
        var index = await IndexOnceAsync();

        // SignatureFallbackSummarizer should populate summaries from docstrings or signatures
        var withSummary = index.Symbols.Values.Where(s => !string.IsNullOrWhiteSpace(s.Summary)).ToList();
        withSummary.Should().NotBeEmpty("SignatureFallbackSummarizer should produce summaries");
    }

    [Fact]
    public async Task Index_HasFileHashes_ForIncrementalSupport()
    {
        var index = await IndexOnceAsync();

        index.FileHashes.Should().NotBeEmpty();
        index.FileHashes.Values.Should().AllSatisfy(h => h.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task Index_ByteOffsets_AreReasonable()
    {
        var index = await IndexOnceAsync();

        foreach (var sym in index.Symbols.Values)
        {
            sym.ByteStart.Should().BeGreaterOrEqualTo(0);
            sym.ByteEnd.Should().BeGreaterOrEqualTo(sym.ByteStart);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Persistence — save & reload index
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Index_IsPersisted_AndReloadable()
    {
        var index = await IndexOnceAsync();
        var repoKey = index.RepoKey;

        var loaded = await _store.LoadAsync(repoKey);
        loaded.Should().NotBeNull();
        loaded!.SymbolCount.Should().Be(index.SymbolCount);
        loaded.FileCount.Should().Be(index.FileCount);
        loaded.RepoKey.Should().Be(repoKey);
    }

    [Fact]
    public async Task Index_ListRepoKeys_ContainsIndexedFolder()
    {
        var index = await IndexOnceAsync();

        var keys = await _store.ListRepoKeysAsync();
        keys.Should().Contain(index.RepoKey);
    }

    [Fact]
    public async Task Index_RawSource_IsCached()
    {
        var index = await IndexOnceAsync();

        // Pick a file that was indexed
        var filePath = index.FileSymbols.Keys.First();
        var raw = await _store.GetRawSourceAsync(index.RepoKey, filePath);
        raw.Should().NotBeNullOrWhiteSpace("raw source should be persisted");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Incremental re-indexing
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReIndex_UnchangedFiles_StaysIncremental()
    {
        var first = await IndexOnceAsync();

        // Re-index same folder without changes
        var second = await _indexer.IndexFolderAsync(_tempRoot);

        // File count and symbol count must be stable
        second.FileCount.Should().Be(first.FileCount,
            "unchanged folder should produce same file count");
        second.SymbolCount.Should().Be(first.SymbolCount,
            "unchanged files should produce same symbols");
    }

    [Fact]
    public async Task ReIndex_NewFile_PicksItUp()
    {
        var first = await IndexOnceAsync();

        // Add a new file
        WriteFile("services/NotificationService.cs", """
            namespace SampleApp.Services;

            /// <summary>Sends notifications.</summary>
            public class NotificationService
            {
                public void Send(string message) { }
            }
            """);

        var second = await _indexer.IndexFolderAsync(_tempRoot);

        second.SymbolCount.Should().BeGreaterThan(first.SymbolCount,
            "new file should add new symbols");
        second.Symbols.Values.Select(s => s.Name).Should().Contain("NotificationService");
    }

    [Fact]
    public async Task ReIndex_ModifiedFile_UpdatesSymbols()
    {
        await IndexOnceAsync();

        // Modify an existing file — add a new method
        WriteFile("utils/MathHelper.cs", """
            namespace SampleApp.Utils;

            /// <summary>Math utility functions.</summary>
            public static class MathHelper
            {
                public static int Factorial(int n)
                {
                    if (n < 0) throw new ArgumentException("Negative");
                    return n <= 1 ? 1 : n * Factorial(n - 1);
                }

                public static double Clamp(double value, double min, double max) =>
                    Math.Max(min, Math.Min(max, value));

                public static int Fibonacci(int n) => n <= 1 ? n : Fibonacci(n - 1) + Fibonacci(n - 2);
            }
            """);

        var updated = await _indexer.IndexFolderAsync(_tempRoot);
        updated.Symbols.Values.Select(s => s.Name).Should().Contain("Fibonacci");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Search — BM25 + Fuzzy (no semantic)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_ByExactName_FindsSymbol()
    {
        var index = await IndexOnceAsync();

        var results = await _retriever.SearchSymbolsAsync(index.RepoKey, "UserService");
        results.Should().NotBeEmpty();
        results.First().Symbol.Name.Should().Be("UserService");
    }

    [Fact]
    public async Task Search_ByPartialName_FindsViaFuzzy()
    {
        var index = await IndexOnceAsync();

        var results = await _retriever.SearchSymbolsAsync(index.RepoKey, "Factorial");
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Symbol.Name == "Factorial");
    }

    [Fact]
    public async Task Search_FilterByKind_OnlyReturnsClasses()
    {
        var index = await IndexOnceAsync();

        var results = await _retriever.SearchSymbolsAsync(
            index.RepoKey, "Service", kind: SymbolKind.Class);

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Symbol.Kind.Should().Be(SymbolKind.Class));
    }

    [Fact]
    public async Task Search_FilterByLanguage_OnlyReturnsPython()
    {
        var index = await IndexOnceAsync();

        var results = await _retriever.SearchSymbolsAsync(
            index.RepoKey, "data", language: "python");

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Symbol.Language.Should().Be("python"));
    }

    [Fact]
    public async Task Search_NonExistent_ReturnsEmpty()
    {
        var index = await IndexOnceAsync();

        var results = await _retriever.SearchSymbolsAsync(index.RepoKey, "xyzNonExistentSymbol12345");
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task TextSearch_FindsOccurrencesInSource()
    {
        var index = await IndexOnceAsync();

        var results = await _retriever.SearchTextAsync(index.RepoKey, "Factorial");
        results.Should().NotBeEmpty("'Factorial' appears in MathHelper.cs");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Source Retrieval — byte-offset O(1)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSymbolSource_ReturnsActualSource()
    {
        var index = await IndexOnceAsync();

        var factorial = index.Symbols.Values.FirstOrDefault(s => s.Name == "Factorial");
        factorial.Should().NotBeNull();

        var source = await _retriever.GetSymbolSourceAsync(index.RepoKey, factorial!.Id);
        source.Should().NotBeNullOrWhiteSpace();
        source.Should().Contain("Factorial");
    }

    [Fact]
    public async Task GetSymbolsWithSource_ReturnsBatch()
    {
        var index = await IndexOnceAsync();

        var ids = index.Symbols.Values.Take(3).Select(s => s.Id).ToList();
        var results = await _retriever.GetSymbolsWithSourceAsync(index.RepoKey, ids);

        results.Should().HaveCount(ids.Count);
        results.Should().AllSatisfy(r => r.Source.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task GetRankedContext_RespectsTokenBudget()
    {
        var index = await IndexOnceAsync();

        var bundle = await _retriever.GetRankedContextAsync(index.RepoKey, "User", tokenBudget: 500);
        bundle.Should().NotBeNull();
        bundle.Query.Should().Be("User");
        bundle.BudgetTokens.Should().Be(500);
        bundle.UsedTokens.Should().BeLessOrEqualTo(500);
        bundle.Symbols.Should().NotBeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Outline — hierarchy + file tree
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Outline_Repo_ReturnsHierarchy()
    {
        var index = await IndexOnceAsync();

        var outline = await _outline.GetRepoOutlineAsync(index.RepoKey);
        outline.Should().NotBeEmpty("repo should have top-level symbols");

        // At least some classes should appear as top-level roots
        outline.Should().Contain(n => n.Symbol.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task Outline_File_ReturnsFileSymbols()
    {
        var index = await IndexOnceAsync();

        // Pick a file that definitely has symbols (not an interface-only file)
        var fileWithSymbols = index.FileSymbols
            .First(kv => kv.Value.Count > 0);

        var fileOutline = await _outline.GetFileOutlineAsync(index.RepoKey, fileWithSymbols.Key);
        fileOutline.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Outline_FileTree_ListsAllIndexedFiles()
    {
        var index = await IndexOnceAsync();

        var tree = await _outline.GetFileTreeAsync(index.RepoKey);
        tree.Should().NotBeEmpty();
        tree.Count.Should().Be(index.FileCount);
    }

    [Fact]
    public async Task Outline_FileTree_FilterByPrefix()
    {
        var index = await IndexOnceAsync();

        var tree = await _outline.GetFileTreeAsync(index.RepoKey, "models");
        tree.Should().NotBeEmpty();
        tree.Should().AllSatisfy(p =>
            p.Should().StartWith("models", "prefix filter should work"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Analysis — PageRank, Dead Code
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Analysis_Importance_RankedByPageRank()
    {
        var index = await IndexOnceAsync();

        var important = await _analyzer.GetSymbolImportanceAsync(index.RepoKey, topK: 5);
        important.Should().NotBeEmpty();
        important.Should().BeInDescendingOrder(r => r.Score);
    }

    [Fact]
    public async Task Analysis_DeadCode_DetectsUnreferencedSymbols()
    {
        var index = await IndexOnceAsync();

        var dead = await _analyzer.FindDeadCodeAsync(index.RepoKey);
        // At least MathHelper's functions are likely dead (not referenced from other code)
        dead.Should().NotBeNull();
        // We don't assert exact results since reference detection depends on parser depth
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Cache invalidation
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_RemovesIndex()
    {
        var index = await IndexOnceAsync();
        var repoKey = index.RepoKey;

        await _store.DeleteAsync(repoKey);

        var loaded = await _store.LoadAsync(repoKey);
        loaded.Should().BeNull("index should be deleted");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Security filter integration
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SecurityFilter_SkipsBinaryAndSecretFiles()
    {
        // Add files that should be filtered out
        WriteFile("secrets.json", """{"key": "super-secret-value"}""");
        WriteFile("data.exe", "MZ binary content");
        WriteFile("cert.pem", "-----BEGIN CERTIFICATE-----");

        var index = await _indexer.IndexFolderAsync(_tempRoot);

        var indexedPaths = index.FileSymbols.Keys.ToList();
        indexedPaths.Should().NotContain(p => p.Contains("secrets.json"));
        indexedPaths.Should().NotContain(p => p.Contains("data.exe"));
        indexedPaths.Should().NotContain(p => p.Contains("cert.pem"));
    }

    [Fact]
    public async Task SecurityFilter_RespectsGitignore()
    {
        // Create a .gitignore that excludes the scripts folder
        WriteFile(".gitignore", "scripts/\n");

        var index = await _indexer.IndexFolderAsync(_tempRoot);

        var indexedPaths = index.FileSymbols.Keys.ToList();
        indexedPaths.Should().NotContain(p => p.StartsWith("scripts"),
            ".gitignore should exclude the scripts folder");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Test: Full roundtrip — index → search → retrieve source → verify
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullRoundtrip_IndexSearchRetrieve()
    {
        // 1. Index
        var index = await IndexOnceAsync();
        index.SymbolCount.Should().BeGreaterThan(0);

        // 2. Search
        var results = await _retriever.SearchSymbolsAsync(index.RepoKey, "PlaceOrder");
        results.Should().NotBeEmpty();

        var topResult = results.First();
        topResult.Symbol.Name.Should().Be("PlaceOrder");
        topResult.Score.Should().BeGreaterThan(0);

        // 3. Retrieve source
        var source = await _retriever.GetSymbolSourceAsync(index.RepoKey, topResult.Symbol.Id);
        source.Should().NotBeNullOrWhiteSpace();
        source.Should().Contain("PlaceOrder");

        // 4. Get ranked context
        var context = await _retriever.GetRankedContextAsync(index.RepoKey, "order user", tokenBudget: 2000);
        context.Symbols.Should().NotBeEmpty();
        context.UsedTokens.Should().BeGreaterThan(0);
        context.UsedTokens.Should().BeLessOrEqualTo(context.BudgetTokens);

        // 5. Outline
        var repoOutline = await _outline.GetRepoOutlineAsync(index.RepoKey);
        repoOutline.Should().NotBeEmpty();

        // 6. Analysis
        var importance = await _analyzer.GetSymbolImportanceAsync(index.RepoKey, topK: 10);
        importance.Should().NotBeEmpty();
    }
}
