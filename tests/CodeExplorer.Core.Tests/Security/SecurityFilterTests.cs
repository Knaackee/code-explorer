using CodeExplorer.Core.Models;
using CodeExplorer.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeExplorer.Core.Tests.Security;

public sealed class DefaultSecurityFilterTests
{
    private readonly DefaultSecurityFilter _sut = new(
        Options.Create(new CodeExplorerOptions { MaxFileSizeBytes = 1_000_000 }));

    // ── ShouldIndex ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("src/main.py",         100, true)]
    [InlineData("src/utils.js",        100, true)]
    [InlineData("lib/server.go",       100, true)]
    [InlineData("README.md",           100, true)]
    public void ShouldIndex_RegularSourceFiles_ReturnsTrue(string path, long size, bool expected)
    {
        _sut.ShouldIndex(path, size).Should().Be(expected);
    }

    [Theory]
    [InlineData(".env",                100)]
    [InlineData("config/.env.local",   100)]
    [InlineData("certs/server.pem",    100)]
    [InlineData("keys/private.key",    100)]
    [InlineData("secrets.json",        100)]
    public void ShouldIndex_SecretFiles_ReturnsFalse(string path, long size)
    {
        _sut.ShouldIndex(path, size).Should().BeFalse();
    }

    [Theory]
    [InlineData("app.exe",     100)]
    [InlineData("lib.dll",     100)]
    [InlineData("image.png",   100)]
    [InlineData("data.db",     100)]
    [InlineData("archive.zip", 100)]
    public void ShouldIndex_BinaryFiles_ReturnsFalse(string path, long size)
    {
        _sut.ShouldIndex(path, size).Should().BeFalse();
    }

    [Fact]
    public void ShouldIndex_OversizedFile_ReturnsFalse()
    {
        _sut.ShouldIndex("big.py", 2_000_000).Should().BeFalse();
    }

    [Fact]
    public void ShouldIndex_FileAtExactLimit_ReturnsTrue()
    {
        _sut.ShouldIndex("ok.py", 1_000_000).Should().BeTrue();
    }

    [Theory]
    [InlineData("node_modules/lodash/index.js")]
    [InlineData("__pycache__/utils.pyc")]
    [InlineData(".git/COMMIT_EDITMSG")]
    [InlineData("bin/Debug/app.dll")]
    [InlineData("obj/Release/app.exe")]
    [InlineData("dist/bundle.js")]
    public void ShouldIndex_SkippedDirectories_ReturnsFalse(string path)
    {
        _sut.ShouldIndex(path, 100).Should().BeFalse();
    }

    // ── IsSecret ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".env",            true)]
    [InlineData(".env.local",      true)]
    [InlineData("id_rsa",          true)]
    [InlineData("server.pem",      true)]
    [InlineData("keystore.jks",    true)]
    [InlineData("main.py",         false)]
    [InlineData("config.json",     false)]
    public void IsSecret_VariousPaths_ReturnsExpected(string path, bool expected)
    {
        _sut.IsSecret(path).Should().Be(expected);
    }

    // ── IsBinary ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("app.exe",  true)]
    [InlineData("lib.so",   true)]
    [InlineData("img.png",  true)]
    [InlineData("doc.pdf",  true)]
    [InlineData("main.py",  false)]
    [InlineData("app.ts",   false)]
    [InlineData("go.mod",   false)]
    public void IsBinary_VariousPaths_ReturnsExpected(string path, bool expected)
    {
        _sut.IsBinary(path).Should().Be(expected);
    }

    // ── CustomOptions ─────────────────────────────────────────────────────────

    [Fact]
    public void ShouldIndex_WithCustomMaxSize_RespectsNewLimit()
    {
        var filter = new DefaultSecurityFilter(
            Options.Create(new CodeExplorerOptions { MaxFileSizeBytes = 500 }));

        filter.ShouldIndex("big.py", 501).Should().BeFalse();
        filter.ShouldIndex("small.py", 500).Should().BeTrue();
    }
}
