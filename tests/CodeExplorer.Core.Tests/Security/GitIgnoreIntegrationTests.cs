using CodeExplorer.Core.Models;
using CodeExplorer.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeExplorer.Core.Tests.Security;

public sealed class GitIgnoreIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public GitIgnoreIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CodeExplorer-gitignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private DefaultSecurityFilter CreateFilter()
    {
        return new DefaultSecurityFilter(Options.Create(new CodeExplorerOptions()));
    }

    [Fact]
    public void LoadGitIgnore_IgnoresMatchingFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.log\nbuild/\n");

        var filter = CreateFilter();
        filter.LoadGitIgnore(_tempDir);

        filter.IsGitIgnored("app.log").Should().BeTrue();
        filter.IsGitIgnored("build/output.js").Should().BeTrue();
        filter.IsGitIgnored("src/main.py").Should().BeFalse();
    }

    [Fact]
    public void LoadGitIgnore_NoFile_DoesNotThrow()
    {
        var filter = CreateFilter();
        var act = () => filter.LoadGitIgnore(_tempDir);
        act.Should().NotThrow();
    }

    [Fact]
    public void LoadGitIgnore_CommentsAndEmptyLines_AreSkipped()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "# comment\n\n*.tmp\n");

        var filter = CreateFilter();
        filter.LoadGitIgnore(_tempDir);

        filter.IsGitIgnored("data.tmp").Should().BeTrue();
        filter.IsGitIgnored("main.py").Should().BeFalse();
    }

    [Fact]
    public void ShouldIndex_GitIgnoredFile_ReturnsFalse()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "*.generated.cs\n");

        var filter = CreateFilter();
        filter.LoadGitIgnore(_tempDir);

        filter.ShouldIndex("MyClass.generated.cs", 100).Should().BeFalse();
        filter.ShouldIndex("MyClass.cs", 100).Should().BeTrue();
    }
}
