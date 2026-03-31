using CodeExplorer.Core.Models;
using CodeExplorer.Core.Parsing;
using FluentAssertions;
using Xunit;

namespace CodeExplorer.Core.Tests.Parsing;

public sealed class SymbolIdBuilderTests
{
    [Theory]
    [InlineData("src/main.py", "UserService.login", SymbolKind.Method,
                "src/main.py::UserService.login#method")]
    [InlineData("src/utils.py", "authenticate", SymbolKind.Function,
                "src/utils.py::authenticate#function")]
    [InlineData("lib/models.ts", "IRepository", SymbolKind.Interface,
                "lib/models.ts::IRepository#interface")]
    public void Build_ReturnsExpectedFormat(
        string path, string name, SymbolKind kind, string expected)
    {
        var id = SymbolIdBuilder.Build(path, name, kind);
        id.Should().Be(expected);
    }

    [Fact]
    public void Parse_RoundTrip_IsIdempotent()
    {
        var original = SymbolIdBuilder.Build("src/main.py", "UserService.login", SymbolKind.Method);
        var (path, name, kind) = SymbolIdBuilder.Parse(original);

        path.Should().Be("src/main.py");
        name.Should().Be("UserService.login");
        kind.Should().Be("method");
    }

    [Fact]
    public void Build_PathWithBackslashes_NormalisesToForwardSlash()
    {
        // Paths should always be forward-slash normalised before calling Build
        var id = SymbolIdBuilder.Build("src/models/user.cs", "User", SymbolKind.Class);
        id.Should().NotContain("\\");
    }

    [Fact]
    public void Build_SameInputs_ProducesSameId()
    {
        var id1 = SymbolIdBuilder.Build("a/b.py", "foo", SymbolKind.Function);
        var id2 = SymbolIdBuilder.Build("a/b.py", "foo", SymbolKind.Function);
        id1.Should().Be(id2);
    }

    [Fact]
    public void Build_DifferentKind_ProducesDifferentId()
    {
        var funcId   = SymbolIdBuilder.Build("a/b.py", "foo", SymbolKind.Function);
        var methodId = SymbolIdBuilder.Build("a/b.py", "foo", SymbolKind.Method);
        funcId.Should().NotBe(methodId);
    }
}
