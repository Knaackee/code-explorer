using CodeExplorer.Cli.Commands;
using CodeExplorer.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        // Only log warnings+ to stderr to avoid polluting stdout MCP pipes
        logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning);
        logging.SetMinimumLevel(LogLevel.Warning);
    })
    .ConfigureServices(services =>
    {
        services.AddCodeExplorer();
    })
    .Build();

// Wire Spectre.Console DI
var registrar = new TypeRegistrar(host.Services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("cxp");
    config.SetApplicationVersion("0.1.3");

    config.AddBranch("index", index =>
    {
        index.SetDescription("Index a repository or local folder");
        index.AddCommand<IndexRepoCommand>("repo")
             .WithDescription("Index a GitHub repository (owner/repo)");
        index.AddCommand<IndexFolderCommand>("folder")
             .WithDescription("Index a local folder");
    });

    config.AddBranch("search", search =>
    {
        search.SetDescription("Search symbols or text");
        search.AddCommand<SearchSymbolsCommand>("symbols")
              .WithDescription("Search symbols by name, kind, or language");
        search.AddCommand<SearchTextCommand>("text")
              .WithDescription("Full-text search across raw source files");
    });

    config.AddBranch("get", get =>
    {
        get.SetDescription("Retrieve symbol source");
        get.AddCommand<GetSymbolCommand>("symbol")
           .WithDescription("Get full source of a symbol by ID");
        get.AddCommand<GetContextCommand>("context")
           .WithDescription("Get token-budgeted ranked context for a query");
    });

    config.AddBranch("outline", outline =>
    {
        outline.SetDescription("Show structural outlines");
        outline.AddCommand<OutlineRepoCommand>("repo")
               .WithDescription("High-level repo overview");
        outline.AddCommand<OutlineFileCommand>("file")
               .WithDescription("Symbol hierarchy for a file");
    });

    config.AddBranch("analyze", analyze =>
    {
        analyze.SetDescription("Advanced code analysis");
        analyze.AddCommand<AnalyzeImportanceCommand>("importance")
               .WithDescription("Show most important symbols by PageRank");
        analyze.AddCommand<AnalyzeDeadCodeCommand>("dead-code")
               .WithDescription("Detect dead code symbols");
        analyze.AddCommand<AnalyzeBlastRadiusCommand>("blast-radius")
               .WithDescription("Show symbols affected if a symbol changes");
    });

    config.AddCommand<ListReposCommand>("list")
          .WithDescription("List all indexed repositories");

    config.AddCommand<InvalidateCacheCommand>("invalidate")
          .WithDescription("Remove cached index for a repository");
});

return app.Run(args);

// ── Spectre DI Glue ───────────────────────────────────────────────────────────

sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceProvider _provider;
    public TypeRegistrar(IServiceProvider provider) => _provider = provider;
    public ITypeResolver Build() => new TypeResolver(_provider);
    public void Register(Type service, Type implementation) { }
    public void RegisterInstance(Type service, object implementation) { }
    public void RegisterLazy(Type service, Func<object> factory) { }
}

sealed class TypeResolver : ITypeResolver
{
    private readonly IServiceProvider _provider;
    public TypeResolver(IServiceProvider provider) => _provider = provider;
    public object? Resolve(Type? type)
    {
        if (type == null) return null;

        // Spectre commands are not explicitly registered in the host container.
        // Fallback to ActivatorUtilities so constructor injection still works.
        return _provider.GetService(type) ?? ActivatorUtilities.CreateInstance(_provider, type);
    }
}
