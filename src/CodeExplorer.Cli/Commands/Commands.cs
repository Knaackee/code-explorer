using CodeExplorer.Core;
using CodeExplorer.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace CodeExplorer.Cli.Commands;

// ─── Index ────────────────────────────────────────────────────────────────────

public sealed class IndexRepoSettings : CommandSettings
{
    [CommandArgument(0, "<owner/repo>")]
    [Description("GitHub repository in owner/repo format")]
    public string Repo { get; set; } = string.Empty;

    [CommandOption("--json")]
    [Description("Output as JSON")]
    public bool Json { get; set; }
}

public sealed class IndexRepoCommand : AsyncCommand<IndexRepoSettings>
{
    private readonly ICodeIndexer _indexer;
    public IndexRepoCommand(ICodeIndexer indexer) => _indexer = indexer;

    public override async Task<int> ExecuteAsync(CommandContext ctx, IndexRepoSettings settings)
    {
        CodeIndex? index = null;

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new ElapsedTimeColumn())
            .StartAsync(async progress =>
            {
                var task = progress.AddTask($"Indexing [bold]{settings.Repo}[/]");
                task.IsIndeterminate = true;
                index = await _indexer.IndexRepoAsync(settings.Repo);
                task.Value = 100;
            });

        if (index == null) return 1;

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                repo = index.RepoKey,
                symbols = index.SymbolCount,
                files = index.FileCount,
                duration_seconds = index.IndexDuration.TotalSeconds,
            }));
        }
        else
        {
            var table = new Table().BorderColor(Color.Grey);
            table.AddColumn("Property").AddColumn("Value");
            table.AddRow("Repository", index.RepoKey);
            table.AddRow("Symbols indexed", index.SymbolCount.ToString("N0"));
            table.AddRow("Files indexed", index.FileCount.ToString("N0"));
            table.AddRow("Duration", $"{index.IndexDuration.TotalSeconds:F1}s");
            table.AddRow("Index date", index.IndexedAt.LocalDateTime.ToString("g"));
            AnsiConsole.Write(table);
        }

        return 0;
    }
}

public sealed class IndexFolderSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Absolute or relative path to local folder")]
    public string Path { get; set; } = string.Empty;

    [CommandOption("--json")]
    public bool Json { get; set; }
}

public sealed class IndexFolderCommand : AsyncCommand<IndexFolderSettings>
{
    private readonly ICodeIndexer _indexer;
    public IndexFolderCommand(ICodeIndexer indexer) => _indexer = indexer;

    public override async Task<int> ExecuteAsync(CommandContext ctx, IndexFolderSettings settings)
    {
        var absPath = System.IO.Path.GetFullPath(settings.Path);
        if (!Directory.Exists(absPath))
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {absPath}");
            return 1;
        }

        CodeIndex? index = null;
        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new ElapsedTimeColumn())
            .StartAsync(async progress =>
            {
                var task = progress.AddTask($"Indexing [bold]{absPath}[/]");
                task.IsIndeterminate = true;
                index = await _indexer.IndexFolderAsync(absPath);
                task.Value = 100;
            });

        if (index == null) return 1;

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                repo = index.RepoKey,
                symbols = index.SymbolCount,
                files = index.FileCount,
                duration_seconds = index.IndexDuration.TotalSeconds,
            }));
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Indexed [bold]{index.SymbolCount:N0}[/] symbols in [bold]{index.FileCount:N0}[/] files ({index.IndexDuration.TotalSeconds:F1}s)");
        }

        return 0;
    }
}

// ─── Search ───────────────────────────────────────────────────────────────────

public sealed class SearchSymbolsSettings : CommandSettings
{
    [CommandOption("--repo <repo>")]
    [Description("Repository key (owner/repo or folder name)")]
    public string Repo { get; set; } = string.Empty;

    [CommandArgument(0, "<query>")]
    public string Query { get; set; } = string.Empty;

    [CommandOption("--kind <kind>")]
    [Description("Filter by symbol kind: function|class|method|constant|type|interface")]
    public string? Kind { get; set; }

    [CommandOption("--language <lang>")]
    [Description("Filter by language: python|javascript|typescript|go|rust|java|csharp")]
    public string? Language { get; set; }

    [CommandOption("--top <n>")]
    [DefaultValue(10)]
    public int Top { get; set; } = 10;

    [CommandOption("--json")]
    public bool Json { get; set; }
}

public sealed class SearchSymbolsCommand : AsyncCommand<SearchSymbolsSettings>
{
    private readonly ISymbolRetriever _retriever;
    public SearchSymbolsCommand(ISymbolRetriever retriever) => _retriever = retriever;

    public override async Task<int> ExecuteAsync(CommandContext ctx, SearchSymbolsSettings settings)
    {
        SymbolKind? kind = settings.Kind != null && Enum.TryParse<SymbolKind>(settings.Kind, true, out var k) ? k : null;

        var results = await _retriever.SearchSymbolsAsync(
            settings.Repo, settings.Query, kind, settings.Language, settings.Top);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results.Select(r => new
            {
                id = r.Symbol.Id,
                name = r.Symbol.QualifiedName,
                kind = r.Symbol.Kind.ToString().ToLower(),
                language = r.Symbol.Language,
                file = r.Symbol.FilePath,
                summary = r.Symbol.Summary,
                score = r.Score,
            })));
            return 0;
        }

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No symbols found.[/]");
            return 0;
        }

        var table = new Table().BorderColor(Color.Grey).Expand();
        table.AddColumn("Score").AddColumn("Kind").AddColumn("Symbol").AddColumn("File").AddColumn("Summary");

        foreach (var r in results)
        {
            var kindColor = r.Symbol.Kind switch
            {
                SymbolKind.Class or SymbolKind.Interface => "blue",
                SymbolKind.Function or SymbolKind.Method  => "green",
                SymbolKind.Constant or SymbolKind.Type    => "yellow",
                _ => "grey",
            };
            table.AddRow(
                $"{r.Score:F3}",
                $"[{kindColor}]{r.Symbol.Kind.ToString().ToLower()}[/]",
                $"[bold]{Markup.Escape(r.Symbol.QualifiedName)}[/]",
                Markup.Escape(r.Symbol.FilePath),
                Markup.Escape(r.Symbol.Summary.Length > 60 ? r.Symbol.Summary[..60] + "…" : r.Symbol.Summary));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class SearchTextSettings : CommandSettings
{
    [CommandOption("--repo <repo>")] public string Repo { get; set; } = string.Empty;
    [CommandArgument(0, "<query>")] public string Query { get; set; } = string.Empty;
    [CommandOption("--top <n>")] [DefaultValue(10)] public int Top { get; set; } = 10;
    [CommandOption("--json")] public bool Json { get; set; }
}

public sealed class SearchTextCommand : AsyncCommand<SearchTextSettings>
{
    private readonly ISymbolRetriever _retriever;
    public SearchTextCommand(ISymbolRetriever retriever) => _retriever = retriever;

    public override async Task<int> ExecuteAsync(CommandContext ctx, SearchTextSettings settings)
    {
        var results = await _retriever.SearchTextAsync(settings.Repo, settings.Query, settings.Top);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results.Select(r => new
            { id = r.Symbol.Id, file = r.Symbol.FilePath, score = r.Score })));
            return 0;
        }

        foreach (var r in results)
            AnsiConsole.MarkupLine($"[grey]{r.Score,4:F0}x[/]  [bold]{Markup.Escape(r.Symbol.FilePath)}[/]  {Markup.Escape(r.Symbol.QualifiedName)}");

        return 0;
    }
}

// ─── Get ──────────────────────────────────────────────────────────────────────

public sealed class GetSymbolSettings : CommandSettings
{
    [CommandOption("--repo <repo>")] public string Repo { get; set; } = string.Empty;
    [CommandArgument(0, "<symbol-id>")] public string SymbolId { get; set; } = string.Empty;
}

public sealed class GetSymbolCommand : AsyncCommand<GetSymbolSettings>
{
    private readonly ISymbolRetriever _retriever;
    public GetSymbolCommand(ISymbolRetriever retriever) => _retriever = retriever;

    public override async Task<int> ExecuteAsync(CommandContext ctx, GetSymbolSettings settings)
    {
        var source = await _retriever.GetSymbolSourceAsync(settings.Repo, settings.SymbolId);
        if (source == null)
        {
            AnsiConsole.MarkupLine($"[red]Symbol not found:[/] {settings.SymbolId}");
            return 1;
        }

        // Syntax-highlighted panel
        var panel = new Panel(new Text(source))
        {
            Header = new PanelHeader(Markup.Escape(settings.SymbolId)),
            Border = BoxBorder.Rounded,
        };
        AnsiConsole.Write(panel);
        return 0;
    }
}

public sealed class GetContextSettings : CommandSettings
{
    [CommandOption("--repo <repo>")] public string Repo { get; set; } = string.Empty;
    [CommandArgument(0, "<query>")] public string Query { get; set; } = string.Empty;
    [CommandOption("--budget <tokens>")] [DefaultValue(4000)] public int Budget { get; set; } = 4000;
    [CommandOption("--json")] public bool Json { get; set; }
}

public sealed class GetContextCommand : AsyncCommand<GetContextSettings>
{
    private readonly ISymbolRetriever _retriever;
    public GetContextCommand(ISymbolRetriever retriever) => _retriever = retriever;

    public override async Task<int> ExecuteAsync(CommandContext ctx, GetContextSettings settings)
    {
        var bundle = await _retriever.GetRankedContextAsync(settings.Repo, settings.Query, settings.Budget);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                query = bundle.Query,
                budget_tokens = bundle.BudgetTokens,
                used_tokens = bundle.UsedTokens,
                excluded_count = bundle.ExcludedCount,
                symbols = bundle.Symbols.Select(s => new
                {
                    id = s.Symbol.Id,
                    source = s.Source,
                }),
            }));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Context for:[/] {Markup.Escape(bundle.Query)}");
        AnsiConsole.MarkupLine($"Tokens used: [green]{bundle.UsedTokens:N0}[/] / {bundle.BudgetTokens:N0}  " +
                               $"({bundle.ExcludedCount} symbols excluded)");
        AnsiConsole.WriteLine();

        foreach (var s in bundle.Symbols)
        {
            AnsiConsole.MarkupLine($"[blue]// {Markup.Escape(s.Symbol.Id)}[/]");
            AnsiConsole.WriteLine(s.Source);
            AnsiConsole.WriteLine();
        }

        return 0;
    }
}

// ─── Outline ──────────────────────────────────────────────────────────────────

public sealed class OutlineRepoSettings : CommandSettings
{
    [CommandArgument(0, "<repo>")] public string Repo { get; set; } = string.Empty;
    [CommandOption("--json")] public bool Json { get; set; }
}

public sealed class OutlineRepoCommand : AsyncCommand<OutlineRepoSettings>
{
    private readonly IOutlineProvider _outline;
    public OutlineRepoCommand(IOutlineProvider outline) => _outline = outline;

    public override async Task<int> ExecuteAsync(CommandContext ctx, OutlineRepoSettings settings)
    {
        var nodes = await _outline.GetRepoOutlineAsync(settings.Repo);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(nodes.Select(SerializeNode)));
            return 0;
        }

        var tree = new Tree($"[bold]{Markup.Escape(settings.Repo)}[/]");
        foreach (var node in nodes)
            AddNode(tree.AddNode(FormatSymbol(node.Symbol)), node);

        AnsiConsole.Write(tree);
        return 0;
    }

    private static void AddNode(TreeNode parent, Core.Models.SymbolNode node)
    {
        foreach (var child in node.Children)
            AddNode(parent.AddNode(FormatSymbol(child.Symbol)), child);
    }

    private static string FormatSymbol(Symbol s) =>
        $"[{KindColor(s.Kind)}]{s.Kind.ToString().ToLower()}[/] [bold]{Markup.Escape(s.Name)}[/]  [grey]{Markup.Escape(s.Summary.Length > 50 ? s.Summary[..50] + "…" : s.Summary)}[/]";

    private static string KindColor(SymbolKind k) => k switch
    {
        SymbolKind.Class or SymbolKind.Interface => "blue",
        SymbolKind.Function or SymbolKind.Method => "green",
        _ => "yellow",
    };

    private static object SerializeNode(Core.Models.SymbolNode n) => new
    {
        id = n.Symbol.Id, name = n.Symbol.Name, kind = n.Symbol.Kind.ToString().ToLower(),
        summary = n.Symbol.Summary, children = n.Children.Select(SerializeNode),
    };
}

public sealed class OutlineFileSettings : CommandSettings
{
    [CommandArgument(0, "<repo>")] public string Repo { get; set; } = string.Empty;
    [CommandOption("--path <path>")] public string FilePath { get; set; } = string.Empty;
    [CommandOption("--json")] public bool Json { get; set; }
}

public sealed class OutlineFileCommand : AsyncCommand<OutlineFileSettings>
{
    private readonly IOutlineProvider _outline;
    public OutlineFileCommand(IOutlineProvider outline) => _outline = outline;

    public override async Task<int> ExecuteAsync(CommandContext ctx, OutlineFileSettings settings)
    {
        var nodes = await _outline.GetFileOutlineAsync(settings.Repo, settings.FilePath);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(nodes.Select(n => new
            {
                id = n.Symbol.Id, name = n.Symbol.Name,
                kind = n.Symbol.Kind.ToString().ToLower(),
                line = n.Symbol.StartLine, summary = n.Symbol.Summary,
            })));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(settings.FilePath)}[/]");
        foreach (var node in nodes)
            AnsiConsole.MarkupLine($"  [green]{node.Symbol.Kind.ToString().ToLower()}[/] [bold]{Markup.Escape(node.Symbol.Name)}[/] [grey]:{node.Symbol.StartLine}[/]");

        return 0;
    }
}

// ─── Analyze ──────────────────────────────────────────────────────────────────

public sealed class AnalyzeSettings : CommandSettings
{
    [CommandArgument(0, "<repo>")] public string Repo { get; set; } = string.Empty;
    [CommandOption("--top <n>")] [DefaultValue(20)] public int Top { get; set; } = 20;
    [CommandOption("--json")] public bool Json { get; set; }
}

public sealed class AnalyzeImportanceCommand : AsyncCommand<AnalyzeSettings>
{
    private readonly ICodeAnalyzer _analyzer;
    public AnalyzeImportanceCommand(ICodeAnalyzer analyzer) => _analyzer = analyzer;

    public override async Task<int> ExecuteAsync(CommandContext ctx, AnalyzeSettings settings)
    {
        var results = await _analyzer.GetSymbolImportanceAsync(settings.Repo, settings.Top);
        RenderResults(results, settings.Json, "PageRank Importance");
        return 0;
    }

    private static void RenderResults(IReadOnlyList<Core.Models.SearchResult> results, bool json, string title)
    {
        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results.Select(r => new
            { id = r.Symbol.Id, score = r.Score, summary = r.Symbol.Summary })));
            return;
        }

        var table = new Table().Title(title).BorderColor(Color.Grey).Expand();
        table.AddColumn("Rank").AddColumn("Score").AddColumn("Symbol").AddColumn("Kind").AddColumn("Summary");
        int i = 1;
        foreach (var r in results)
            table.AddRow($"{i++}", $"{r.Score:F4}", Markup.Escape(r.Symbol.QualifiedName),
                r.Symbol.Kind.ToString().ToLower(),
                Markup.Escape(r.Symbol.Summary.Length > 50 ? r.Symbol.Summary[..50] + "…" : r.Symbol.Summary));
        AnsiConsole.Write(table);
    }
}

public sealed class AnalyzeDeadCodeCommand : AsyncCommand<AnalyzeSettings>
{
    private readonly ICodeAnalyzer _analyzer;
    public AnalyzeDeadCodeCommand(ICodeAnalyzer analyzer) => _analyzer = analyzer;

    public override async Task<int> ExecuteAsync(CommandContext ctx, AnalyzeSettings settings)
    {
        var dead = await _analyzer.FindDeadCodeAsync(settings.Repo);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(dead.Select(d => new
            { id = d.Symbol.Id, file = d.Symbol.FilePath, reason = d.Reason })));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Dead code:[/] {dead.Count} symbols with no incoming references");
        foreach (var d in dead.Take(settings.Top))
            AnsiConsole.MarkupLine($"  [red]dead[/] [grey]{Markup.Escape(d.Symbol.FilePath)}[/]::[bold]{Markup.Escape(d.Symbol.QualifiedName)}[/]");

        return 0;
    }
}

public sealed class BlastRadiusSettings : CommandSettings
{
    [CommandArgument(0, "<repo>")] public string Repo { get; set; } = string.Empty;
    [CommandArgument(1, "<symbol-id>")] public string SymbolId { get; set; } = string.Empty;
    [CommandOption("--depth <d>")] [DefaultValue(3)] public int Depth { get; set; } = 3;
    [CommandOption("--json")] public bool Json { get; set; }
}

public sealed class AnalyzeBlastRadiusCommand : AsyncCommand<BlastRadiusSettings>
{
    private readonly ICodeAnalyzer _analyzer;
    public AnalyzeBlastRadiusCommand(ICodeAnalyzer analyzer) => _analyzer = analyzer;

    public override async Task<int> ExecuteAsync(CommandContext ctx, BlastRadiusSettings settings)
    {
        var affected = await _analyzer.GetBlastRadiusAsync(settings.Repo, settings.SymbolId, settings.Depth);

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(affected.Select(s => new
            { id = s.Id, name = s.QualifiedName, file = s.FilePath })));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Blast radius[/] of [blue]{Markup.Escape(settings.SymbolId)}[/]: {affected.Count} affected symbols (depth {settings.Depth})");
        foreach (var s in affected)
            AnsiConsole.MarkupLine($"  [yellow]→[/] {Markup.Escape(s.QualifiedName)}  [grey]{Markup.Escape(s.FilePath)}[/]");

        return 0;
    }
}

// ─── List / Invalidate ────────────────────────────────────────────────────────

public sealed class ListReposCommand : AsyncCommand
{
    private readonly IIndexStore _store;
    public ListReposCommand(IIndexStore store) => _store = store;

    public override async Task<int> ExecuteAsync(CommandContext ctx)
    {
        var keys = await _store.ListRepoKeysAsync();
        if (keys.Count == 0) { AnsiConsole.MarkupLine("[grey]No repositories indexed.[/]"); return 0; }

        foreach (var key in keys)
            AnsiConsole.MarkupLine($"  [green]•[/] {Markup.Escape(key)}");

        return 0;
    }
}

public sealed class InvalidateCacheSettings : CommandSettings
{
    [CommandArgument(0, "<repo>")] public string Repo { get; set; } = string.Empty;
}

public sealed class InvalidateCacheCommand : AsyncCommand<InvalidateCacheSettings>
{
    private readonly IIndexStore _store;
    public InvalidateCacheCommand(IIndexStore store) => _store = store;

    public override async Task<int> ExecuteAsync(CommandContext ctx, InvalidateCacheSettings settings)
    {
        await _store.DeleteAsync(settings.Repo);
        AnsiConsole.MarkupLine($"[green]✓[/] Cache invalidated for [bold]{Markup.Escape(settings.Repo)}[/]");
        return 0;
    }
}
