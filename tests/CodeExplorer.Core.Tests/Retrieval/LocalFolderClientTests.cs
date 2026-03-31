using CodeExplorer.Core.Models;
using CodeExplorer.Core.Parsing;
using CodeExplorer.Core.Retrieval;
using CodeExplorer.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeExplorer.Core.Tests.Retrieval;

public sealed class LocalFolderClientTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFolderClient _sut;

    public LocalFolderClientTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CodeExplorer-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var opts = Options.Create(new CodeExplorerOptions());
        var security = new DefaultSecurityFilter(opts);
        _sut = new LocalFolderClient(
            new DefaultLanguageDetector(),
            security,
            NullLogger<LocalFolderClient>.Instance);
        _sut.SetRootPath(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task GetFilesAsync_FindsSupportedFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "main.py"), "def hello(): pass");
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a code file");

        var files = await _sut.GetFilesAsync(string.Empty, string.Empty);

        files.Should().ContainSingle(f => f.Path == "main.py");
        files.Should().NotContain(f => f.Path == "readme.txt");
    }

    [Fact]
    public async Task GetFilesAsync_SkipsBinDirectories()
    {
        var binDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "app.py"), "x = 1");
        File.WriteAllText(Path.Combine(_tempDir, "src.py"), "y = 2");

        var files = await _sut.GetFilesAsync(string.Empty, string.Empty);

        files.Should().NotContain(f => f.Path.Contains("bin"));
        files.Should().ContainSingle(f => f.Path == "src.py");
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsContent()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.py"), "def test(): return 42");

        var content = await _sut.GetFileContentAsync(string.Empty, string.Empty, "test.py");

        content.Should().Contain("def test()");
    }

    [Fact]
    public async Task GetFileContentAsync_PathTraversal_Throws()
    {
        var act = async () => await _sut.GetFileContentAsync(string.Empty, string.Empty, "../../../etc/passwd");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task RepoExistsAsync_ExistingDir_ReturnsTrue()
    {
        var result = await _sut.RepoExistsAsync(string.Empty, string.Empty);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RepoExistsAsync_NonExistentDir_ReturnsFalse()
    {
        var client = new LocalFolderClient(
            new DefaultLanguageDetector(),
            new DefaultSecurityFilter(Options.Create(new CodeExplorerOptions())),
            NullLogger<LocalFolderClient>.Instance);
        client.SetRootPath(Path.Combine(_tempDir, "nonexistent"));

        var result = await client.RepoExistsAsync(string.Empty, string.Empty);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetFilesAsync_SubDirectories_UsesRelativePaths()
    {
        var subDir = Path.Combine(_tempDir, "src", "lib");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "utils.py"), "def util(): pass");

        var files = await _sut.GetFilesAsync(string.Empty, string.Empty);

        files.Should().ContainSingle(f => f.Path == "src/lib/utils.py");
    }
}
