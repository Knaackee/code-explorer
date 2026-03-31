using CodeExplorer.Core.Models;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace CodeExplorer.Core.Tests;

public sealed class OutlineProviderTests
{
    private static CodeIndex MakeIndex()
    {
        var classSymbol = new Symbol
        {
            Id = "f.py::MyClass#class", FilePath = "f.py", QualifiedName = "MyClass",
            Name = "MyClass", Kind = SymbolKind.Class, Language = "python",
            Signature = "class MyClass:", ContentHash = "h1", StartLine = 1, EndLine = 20,
        };

        var method1 = new Symbol
        {
            Id = "f.py::MyClass.do_work#method", FilePath = "f.py", QualifiedName = "MyClass.do_work",
            Name = "do_work", Kind = SymbolKind.Method, Language = "python",
            Signature = "def do_work(self):", ContentHash = "h2", StartLine = 5, EndLine = 10,
            ParentId = "f.py::MyClass#class",
        };

        var method2 = new Symbol
        {
            Id = "f.py::MyClass.cleanup#method", FilePath = "f.py", QualifiedName = "MyClass.cleanup",
            Name = "cleanup", Kind = SymbolKind.Method, Language = "python",
            Signature = "def cleanup(self):", ContentHash = "h3", StartLine = 12, EndLine = 18,
            ParentId = "f.py::MyClass#class",
        };

        var topFunc = new Symbol
        {
            Id = "f.py::main#function", FilePath = "f.py", QualifiedName = "main",
            Name = "main", Kind = SymbolKind.Function, Language = "python",
            Signature = "def main():", ContentHash = "h4", StartLine = 22, EndLine = 30,
        };

        return new CodeIndex
        {
            RepoKey = "test", RepoUrl = "test", IsLocal = true, IndexedAt = DateTimeOffset.UtcNow,
            Symbols = new()
            {
                [classSymbol.Id] = classSymbol,
                [method1.Id] = method1,
                [method2.Id] = method2,
                [topFunc.Id] = topFunc,
            },
            FileSymbols = new()
            {
                ["f.py"] = [classSymbol.Id, method1.Id, method2.Id, topFunc.Id],
            },
        };
    }

    private static OutlineProvider CreateProvider(CodeIndex index)
    {
        var store = Substitute.For<IIndexStore>();
        store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(index);
        return new OutlineProvider(store);
    }

    [Fact]
    public async Task GetFileOutlineAsync_ReturnsHierarchicalTree()
    {
        var index = MakeIndex();
        var sut = CreateProvider(index);

        var outline = await sut.GetFileOutlineAsync("test", "f.py");

        // Should have 2 top-level nodes: MyClass and main
        outline.Should().HaveCount(2);
        outline.Should().Contain(n => n.Symbol.Name == "MyClass");
        outline.Should().Contain(n => n.Symbol.Name == "main");
    }

    [Fact]
    public async Task GetFileOutlineAsync_ClassHasMethodChildren()
    {
        var index = MakeIndex();
        var sut = CreateProvider(index);

        var outline = await sut.GetFileOutlineAsync("test", "f.py");

        var classNode = outline.First(n => n.Symbol.Name == "MyClass");
        classNode.Children.Should().HaveCount(2);
        classNode.Children.Should().Contain(c => c.Symbol.Name == "do_work");
        classNode.Children.Should().Contain(c => c.Symbol.Name == "cleanup");
    }

    [Fact]
    public async Task GetFileOutlineAsync_NonExistentFile_ReturnsEmpty()
    {
        var index = MakeIndex();
        var sut = CreateProvider(index);

        var outline = await sut.GetFileOutlineAsync("test", "nonexistent.py");

        outline.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileTreeAsync_ReturnsAllFilePaths()
    {
        var index = MakeIndex();
        var sut = CreateProvider(index);

        var tree = await sut.GetFileTreeAsync("test");

        tree.Should().ContainSingle(f => f == "f.py");
    }
}
