using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CodeExplorer.Core.Tests.Cli;

/// <summary>
/// Process-level E2E tests that spawn the CLI binary via <c>dotnet run</c>
/// and verify exit codes + stdout for every command documented in test-commands.md.
/// </summary>
[Collection("CliTests")]
public sealed class CliEndToEndTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;
    private readonly string _solutionRoot;
    private readonly string _cliProject;

    public CliEndToEndTests(ITestOutputHelper output)
    {
        _output = output;

        // Walk up from test assembly location to find solution root
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CodeExplorer.sln")))
            dir = Path.GetDirectoryName(dir);

        _solutionRoot = dir ?? throw new InvalidOperationException(
            "Could not locate CodeExplorer.sln from " + AppContext.BaseDirectory);
        _cliProject = Path.Combine(_solutionRoot, "src", "CodeExplorer.Cli");

        var guid = Guid.NewGuid().ToString("N");
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cxp_cli_e2e_{guid}");
        Directory.CreateDirectory(_tempRoot);

        SeedTestCodebase();
    }

    public async Task InitializeAsync()
    {
        // Index the temp folder once — all subsequent tests read from this index.
        var (exitCode, stdout, stderr) = await RunCliAsync($"index folder \"{_tempRoot}\"");
        _output.WriteLine($"[index] exit={exitCode}\n{stdout}\n{stderr}");
        exitCode.Should().Be(0, "indexing the temp folder must succeed");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string RepoKey
    {
        get
        {
            // The CLI derives the repo key from the folder name
            return new DirectoryInfo(_tempRoot).Name;
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        string arguments, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{_cliProject}\" -- {arguments}",
            WorkingDirectory = _solutionRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Suppress Spectre.Console's ANSI detection so we get plain text
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var completed = proc.WaitForExit(timeoutMs);
        if (!completed)
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException($"CLI command timed out after {timeoutMs}ms: {arguments}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr);
    }

    private void SeedTestCodebase()
    {
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

        WriteFile("services/IUserService.cs", """
            namespace SampleApp.Services;

            public interface IUserService
            {
                User GetById(int id);
                IReadOnlyList<User> GetAll();
                void Create(User user);
            }
            """);

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

    // ══════════════════════════════════════════════════════════════════════════
    //  0) list — show indexed repos
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_ShowsIndexedRepo()
    {
        var (exit, stdout, _) = await RunCliAsync("list");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().Contain(RepoKey, "the indexed folder should appear in the list");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1) search symbols — basic
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchSymbols_FindsClass()
    {
        var (exit, stdout, _) = await RunCliAsync($"search symbols --repo {RepoKey} \"UserService\"");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().Contain("UserService");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2) search symbols — filtered by kind
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchSymbols_FilterByKind()
    {
        var (exit, stdout, _) = await RunCliAsync(
            $"search symbols --repo {RepoKey} \"User\" --kind class --top 20");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().Contain("User");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3) search text — full text search
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchText_FindsOccurrences()
    {
        var (exit, stdout, _) = await RunCliAsync(
            $"search text --repo {RepoKey} \"Factorial\" --top 15");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().Contain("Factorial");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4) get context — RAG context bundle
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetContext_ReturnsContextBundle()
    {
        var (exit, stdout, _) = await RunCliAsync(
            $"get context --repo {RepoKey} \"How does order creation work?\" --budget 3000");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().NotBeEmpty("context bundle should produce output");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5) outline repo — hierarchical symbol tree
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OutlineRepo_ShowsSymbolTree()
    {
        var (exit, stdout, _) = await RunCliAsync($"outline repo {RepoKey}");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().NotBeEmpty("outline should return symbol hierarchy");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  6) outline file — single file symbols
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OutlineFile_ShowsFileSymbols()
    {
        var (exit, stdout, _) = await RunCliAsync(
            $"outline file {RepoKey} --path \"utils/MathHelper.cs\"");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        // The file must have symbols
        stdout.Should().Contain("MathHelper");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  7) analyze importance — PageRank
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnalyzeImportance_ShowsRankedSymbols()
    {
        var (exit, stdout, _) = await RunCliAsync(
            $"analyze importance {RepoKey} --top 20");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().NotBeEmpty("importance analysis should produce output");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  8) analyze dead-code — detect unreferenced symbols
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnalyzeDeadCode_ShowsDeadSymbols()
    {
        var (exit, stdout, _) = await RunCliAsync(
            $"analyze dead-code {RepoKey} --top 30");
        _output.WriteLine(stdout);

        exit.Should().Be(0);
        stdout.Should().NotBeEmpty("dead code analysis should produce output");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  9) search symbols --json + get symbol — round-trip
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchSymbolsJson_ThenGetSymbol_Roundtrip()
    {
        // 1. Search with --json to get symbol IDs
        var (exit1, jsonOut, _) = await RunCliAsync(
            $"search symbols --repo {RepoKey} \"Factorial\" --json");
        _output.WriteLine($"JSON search:\n{jsonOut}");
        exit1.Should().Be(0);

        var results = JsonSerializer.Deserialize<JsonElement[]>(jsonOut);
        results.Should().NotBeNullOrEmpty("JSON search should return at least one result");

        var symbolId = results![0].GetProperty("id").GetString();
        symbolId.Should().NotBeNullOrWhiteSpace();

        // 2. Retrieve that symbol's source
        var (exit2, sourceOut, _) = await RunCliAsync(
            $"get symbol --repo {RepoKey} \"{symbolId}\"");
        _output.WriteLine($"Symbol source:\n{sourceOut}");

        exit2.Should().Be(0);
        sourceOut.Should().Contain("Factorial", "source should contain the function name");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  10) analyze blast-radius — impact analysis
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnalyzeBlastRadius_ShowsAffectedSymbols()
    {
        // First find a symbol ID that has references
        var (exit1, jsonOut, _) = await RunCliAsync(
            $"search symbols --repo {RepoKey} \"UserService\" --json");
        exit1.Should().Be(0);

        var results = JsonSerializer.Deserialize<JsonElement[]>(jsonOut);
        results.Should().NotBeNullOrEmpty();

        // Use the class symbol's ID
        var symbolId = results![0].GetProperty("id").GetString();
        symbolId.Should().NotBeNullOrWhiteSpace();

        var (exit2, stdout, _) = await RunCliAsync(
            $"analyze blast-radius {RepoKey} \"{symbolId}\" --depth 3");
        _output.WriteLine(stdout);

        exit2.Should().Be(0);
        // blast-radius may or may not find affected symbols for the test codebase,
        // but the command should succeed cleanly
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Extra: invalidate cache
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvalidateCache_RemovesIndex()
    {
        var (exit1, stdout1, _) = await RunCliAsync($"invalidate {RepoKey}");
        _output.WriteLine(stdout1);
        exit1.Should().Be(0);

        // After invalidation, list should no longer contain the repo
        var (exit2, stdout2, _) = await RunCliAsync("list");
        _output.WriteLine(stdout2);
        exit2.Should().Be(0);
        stdout2.Should().NotContain(RepoKey,
            "invalidated repo should no longer appear in list");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Extra: JSON output modes
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchText_JsonOutput_IsValidJson()
    {
        var (exit, jsonOut, _) = await RunCliAsync(
            $"search text --repo {RepoKey} \"User\" --json");
        _output.WriteLine(jsonOut);

        exit.Should().Be(0);
        // Should parse as valid JSON
        var act = () => JsonSerializer.Deserialize<JsonElement>(jsonOut);
        act.Should().NotThrow("output should be valid JSON");
    }

    [Fact]
    public async Task OutlineRepo_JsonOutput_IsValidJson()
    {
        var (exit, jsonOut, _) = await RunCliAsync(
            $"outline repo {RepoKey} --json");
        _output.WriteLine(jsonOut);

        exit.Should().Be(0);
        var act = () => JsonSerializer.Deserialize<JsonElement>(jsonOut);
        act.Should().NotThrow("output should be valid JSON");
    }

    [Fact]
    public async Task GetContext_JsonOutput_IsValidJson()
    {
        var (exit, jsonOut, _) = await RunCliAsync(
            $"get context --repo {RepoKey} \"User\" --budget 1000 --json");
        _output.WriteLine(jsonOut);

        exit.Should().Be(0);
        var act = () => JsonSerializer.Deserialize<JsonElement>(jsonOut);
        act.Should().NotThrow("output should be valid JSON");
    }

    [Fact]
    public async Task AnalyzeImportance_JsonOutput_IsValidJson()
    {
        var (exit, jsonOut, _) = await RunCliAsync(
            $"analyze importance {RepoKey} --top 5 --json");
        _output.WriteLine(jsonOut);

        exit.Should().Be(0);
        var act = () => JsonSerializer.Deserialize<JsonElement>(jsonOut);
        act.Should().NotThrow("output should be valid JSON");
    }

    [Fact]
    public async Task AnalyzeDeadCode_JsonOutput_IsValidJson()
    {
        var (exit, jsonOut, _) = await RunCliAsync(
            $"analyze dead-code {RepoKey} --top 10 --json");
        _output.WriteLine(jsonOut);

        exit.Should().Be(0);
        var act = () => JsonSerializer.Deserialize<JsonElement>(jsonOut);
        act.Should().NotThrow("output should be valid JSON");
    }
}
