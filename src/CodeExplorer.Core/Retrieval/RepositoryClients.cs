using CodeExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using Octokit;

namespace CodeExplorer.Core.Retrieval;

/// <summary>GitHub repository client using Octokit.NET.</summary>
public sealed class OctokitRepositoryClient : IRepositoryClient
{
    private readonly GitHubClient _github;
    private readonly ILanguageDetector _languageDetector;
    private readonly ILogger<OctokitRepositoryClient> _logger;

    public OctokitRepositoryClient(
        ILanguageDetector languageDetector,
        ILogger<OctokitRepositoryClient> logger,
        string? token = null)
    {
        _github = new GitHubClient(new ProductHeaderValue("CodeExplorer", "0.1.0"));
        if (token != null)
            _github.Credentials = new Credentials(token);
        _languageDetector = languageDetector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RepoFile>> GetFilesAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching file tree for {Owner}/{Repo}", owner, repo);
        var result = new List<RepoFile>();

        try
        {
            // Get the default branch tree recursively
            var repository = await _github.Repository.Get(owner, repo);
            var branch = repository.DefaultBranch;
            var tree = await _github.Git.Tree.GetRecursive(owner, repo, branch);

            foreach (var item in tree.Tree.Where(t => t.Type == TreeType.Blob))
            {
                var language = _languageDetector.DetectLanguage(item.Path);
                if (language == null) continue;

                result.Add(new RepoFile
                {
                    Path = item.Path,
                    Language = language,
                    SizeBytes = item.Size,
                    Sha = item.Sha,
                });
            }

            _logger.LogInformation("Found {Count} indexable files in {Owner}/{Repo}", result.Count, owner, repo);
        }
        catch (NotFoundException)
        {
            throw new InvalidOperationException($"Repository {owner}/{repo} not found or not accessible");
        }

        return result;
    }

    public async Task<string> GetFileContentAsync(
        string owner, string repo, string path, CancellationToken ct = default)
    {
        var contents = await _github.Repository.Content.GetAllContents(owner, repo, path);
        return contents.FirstOrDefault()?.Content ?? string.Empty;
    }

    public async Task<bool> RepoExistsAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            await _github.Repository.Get(owner, repo);
            return true;
        }
        catch (NotFoundException) { return false; }
    }
}

/// <summary>Local filesystem repository client.</summary>
public sealed class LocalFolderClient : IRepositoryClient
{
    private readonly ILanguageDetector _languageDetector;
    private readonly ISecurityFilter _securityFilter;
    private readonly ILogger<LocalFolderClient> _logger;
    private string _rootPath = string.Empty;

    public LocalFolderClient(
        ILanguageDetector languageDetector,
        ISecurityFilter securityFilter,
        ILogger<LocalFolderClient> logger)
    {
        _languageDetector = languageDetector;
        _securityFilter = securityFilter;
        _logger = logger;
    }

    /// <summary>Set the root path before calling GetFilesAsync with empty owner/repo.</summary>
    public void SetRootPath(string path) => _rootPath = Path.GetFullPath(path);

    public Task<IReadOnlyList<RepoFile>> GetFilesAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        var root = string.IsNullOrEmpty(owner) ? _rootPath : Path.Combine(owner, repo);
        root = Path.GetFullPath(root);

        var files = new List<RepoFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            if (!_securityFilter.ShouldIndex(file, info.Length)) continue;

            var language = _languageDetector.DetectLanguage(file);
            if (language == null) continue;

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files.Add(new RepoFile
            {
                Path = relative,
                Language = language,
                SizeBytes = info.Length,
            });
        }

        _logger.LogInformation("Found {Count} indexable files in {Root}", files.Count, root);
        return Task.FromResult<IReadOnlyList<RepoFile>>(files);
    }

    public Task<string> GetFileContentAsync(
        string owner, string repo, string path, CancellationToken ct = default)
    {
        var root = string.IsNullOrEmpty(owner) ? _rootPath : Path.Combine(owner, repo);
        var full = Path.GetFullPath(Path.Combine(root, path));
        // Path traversal guard
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal detected");
        return File.ReadAllTextAsync(full, ct);
    }

    public Task<bool> RepoExistsAsync(string owner, string repo, CancellationToken ct = default) =>
        Task.FromResult(Directory.Exists(string.IsNullOrEmpty(owner) ? _rootPath : Path.Combine(owner, repo)));
}
