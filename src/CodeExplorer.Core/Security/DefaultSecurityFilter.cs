using CodeExplorer.Core.Models;
using Ignore;
using Microsoft.Extensions.Options;

namespace CodeExplorer.Core.Security;

/// <summary>
/// Filters files before indexing to prevent path traversal, secret exposure,
/// binary indexing, and oversized file processing.
/// </summary>
public sealed class DefaultSecurityFilter : ISecurityFilter
{
    private readonly CodeExplorerOptions _options;
    private Ignore.Ignore? _gitignore;

    private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".env.local", ".env.production", ".env.staging",
        "secrets.json", "appsettings.secrets.json",
        "id_rsa", "id_ed25519", "id_ecdsa",
    };

    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".cert", ".crt", ".der",
        ".jks", ".keystore",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".bin",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
        ".mp3", ".mp4", ".wav", ".ogg",
        ".zip", ".tar", ".gz", ".bz2", ".7z", ".rar",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".pyc", ".pyo", ".class", ".o", ".a",
        ".wasm", ".woff", ".woff2", ".ttf", ".eot",
        ".db", ".sqlite", ".mdf",
    };

    private static readonly HashSet<string> AlwaysSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "__pycache__", ".pytest_cache",
        "bin", "obj", "dist", "build", "out",
        ".vs", ".vscode", ".idea",
        "vendor", "packages",
    };

    public DefaultSecurityFilter(IOptions<CodeExplorerOptions> options)
    {
        _options = options.Value;
    }

    public bool ShouldIndex(string absolutePath, long sizeBytes)
    {
        if (sizeBytes > _options.MaxFileSizeBytes) return false;
        if (IsBinary(absolutePath)) return false;
        if (IsSecret(absolutePath)) return false;
        if (IsInSkippedDirectory(absolutePath)) return false;
        if (HasPathTraversal(absolutePath)) return false;
        if (IsGitIgnored(absolutePath)) return false;
        return true;
    }

    /// <summary>Load .gitignore rules from a root directory.</summary>
    public void LoadGitIgnore(string rootPath)
    {
        var gitignorePath = Path.Combine(rootPath, ".gitignore");
        if (!File.Exists(gitignorePath)) return;

        _gitignore = new Ignore.Ignore();
        foreach (var line in File.ReadAllLines(gitignorePath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                _gitignore.Add(trimmed);
        }
    }

    public bool IsGitIgnored(string filePath)
    {
        if (_gitignore == null) return false;
        var relative = filePath.Replace('\\', '/');
        return _gitignore.IsIgnored(relative);
    }

    public bool IsSecret(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath);
        return SecretFileNames.Contains(fileName) || SecretExtensions.Contains(ext);
    }

    public bool IsBinary(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return BinaryExtensions.Contains(ext);
    }

    private static bool IsInSkippedDirectory(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(p => AlwaysSkipDirs.Contains(p));
    }

    private static bool HasPathTraversal(string path)
    {
        var normalized = Path.GetFullPath(path);
        return path.Contains("..") || normalized != path && path.Contains("..");
    }
}
