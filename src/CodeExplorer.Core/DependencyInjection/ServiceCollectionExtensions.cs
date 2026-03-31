using CodeExplorer.Core.Analysis;
using CodeExplorer.Core.Models;
using CodeExplorer.Core.Parsing;
using CodeExplorer.Core.Retrieval;
using CodeExplorer.Core.Search;
using CodeExplorer.Core.Security;
using CodeExplorer.Core.Storage;
using CodeExplorer.Core.Summarizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeExplorer.Core.DependencyInjection;

/// <summary>
/// Fluent builder for CodeExplorer DI configuration.
/// </summary>
public sealed class CodeExplorerBuilder
{
    public readonly IServiceCollection Services;
    internal CodeExplorerBuilder(IServiceCollection services) => Services = services;

    // ── Summarizer ────────────────────────────────────────────────────────────

    /// <summary>Replace the default summarizer with a custom implementation.</summary>
    public CodeExplorerBuilder WithSummarizer<T>() where T : class, ISymbolSummarizer
    {
        Services.Replace(ServiceDescriptor.Singleton<ISymbolSummarizer, T>());
        return this;
    }

    /// <summary>Replace the default summarizer with a pre-built instance.</summary>
    public CodeExplorerBuilder WithSummarizer(ISymbolSummarizer instance)
    {
        Services.Replace(ServiceDescriptor.Singleton(instance));
        return this;
    }

    // ── Repository Client ─────────────────────────────────────────────────────

    /// <summary>Replace the GitHub client with a custom implementation.</summary>
    public CodeExplorerBuilder WithRepositoryClient<T>() where T : class, IRepositoryClient
    {
        Services.Replace(ServiceDescriptor.Singleton<IRepositoryClient, T>());
        return this;
    }

    // ── Index Store ───────────────────────────────────────────────────────────

    /// <summary>Replace the filesystem JSON store with a custom store (e.g. database).</summary>
    public CodeExplorerBuilder WithIndexStore<T>() where T : class, IIndexStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IIndexStore, T>());
        return this;
    }

    public CodeExplorerBuilder WithIndexStore(IIndexStore instance)
    {
        Services.Replace(ServiceDescriptor.Singleton(instance));
        return this;
    }
}

/// <summary>
/// Extension methods for registering CodeExplorer services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all CodeExplorer services with default implementations.
    /// All defaults are replaceable via the returned builder.
    /// </summary>
    /// <example>
    /// // Minimal
    /// services.AddCodeExplorer();
    ///
    /// // With custom summarizer
    /// services.AddCodeExplorer()
    ///     .WithSummarizer(new MySummarizer());
    ///
    /// // Fully configured
    /// services.AddCodeExplorer(opt => opt.IndexPath = "/custom/path")
    ///     .WithIndexStore&lt;MyDatabaseStore&gt;();
    /// </example>
    public static CodeExplorerBuilder AddCodeExplorer(
        this IServiceCollection services,
        Action<CodeExplorerOptions>? configure = null)
    {
        // Options
        if (configure != null)
            services.Configure(configure);
        else
            services.AddOptions<CodeExplorerOptions>();

        // Core parsers (not swappable — pure logic)
        services.TryAddSingleton<SymbolExtractor>();
        services.TryAddSingleton<ILanguageDetector, DefaultLanguageDetector>();

        // Security
        services.TryAddSingleton<ISecurityFilter, DefaultSecurityFilter>();

        // Storage — default: JSON files
        services.TryAddSingleton<IIndexStore, JsonFileIndexStore>();

        // Retrieval clients — default: Octokit + local filesystem
        services.TryAddSingleton<IRepositoryClient>(sp =>
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            var lang = sp.GetRequiredService<ILanguageDetector>();
            var logger = sp.GetRequiredService<ILogger<OctokitRepositoryClient>>();
            return new OctokitRepositoryClient(lang, logger, token);
        });

        services.TryAddSingleton(sp => new LocalFolderClient(
            sp.GetRequiredService<ILanguageDetector>(),
            sp.GetRequiredService<ISecurityFilter>(),
            sp.GetRequiredService<ILogger<LocalFolderClient>>()));

        // Search engines
        services.TryAddSingleton<BM25Engine>();
        services.TryAddSingleton(sp => new FuzzySearchEngine(
            sp.GetRequiredService<IOptions<CodeExplorerOptions>>().Value.FuzzyThreshold));
        services.TryAddSingleton<HybridSearchEngine>();
        services.TryAddSingleton<TextSearchEngine>();

        // Default summarizer: signature/docstring extraction (no network)
        services.TryAddSingleton<ISymbolSummarizer, SignatureFallbackSummarizer>();

        // Analysis
        services.TryAddSingleton<DeadCodeDetector>();
        services.TryAddSingleton<BlastRadiusCalculator>();
        services.TryAddSingleton<GitDiffAnalyzer>();
        services.TryAddSingleton<ICodeAnalyzer, CodeAnalyzer>();

        // Main services
        services.TryAddSingleton<ICodeIndexer, CodeIndexer>();
        services.TryAddSingleton<ISymbolRetriever, SymbolRetriever>();
        services.TryAddSingleton<IOutlineProvider, OutlineProvider>();

        return new CodeExplorerBuilder(services);
    }
}
