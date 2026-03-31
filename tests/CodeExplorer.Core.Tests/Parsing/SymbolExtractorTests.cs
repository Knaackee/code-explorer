using CodeExplorer.Core.Models;
using CodeExplorer.Core.Parsing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeExplorer.Core.Tests.Parsing;

public sealed class SymbolExtractorTests
{
    private readonly SymbolExtractor _sut = new(NullLogger<SymbolExtractor>.Instance);

    // ── Python ────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_Python_SingleFunction_ReturnsOneSymbol()
    {
        const string source = """
            def authenticate(user, password):
                return user == "admin" and password == "secret"
            """;

        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract(source, "auth.py", "python", spec);

        symbols.Should().HaveCount(1);
        symbols[0].Name.Should().Be("authenticate");
        symbols[0].Kind.Should().Be(SymbolKind.Function);
        symbols[0].Language.Should().Be("python");
        symbols[0].FilePath.Should().Be("auth.py");
    }

    [Fact]
    public void Extract_Python_ClassWithMethods_ReturnsClassAndMethods()
    {
        const string source = """
            class UserService:
                def __init__(self, db):
                    self.db = db

                def get_user(self, user_id):
                    return self.db.find(user_id)

                def delete_user(self, user_id):
                    self.db.delete(user_id)
            """;

        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract(source, "services.py", "python", spec);

        symbols.Should().Contain(s => s.Name == "UserService" && s.Kind == SymbolKind.Class);
        symbols.Should().Contain(s => s.Name == "get_user");
        symbols.Should().Contain(s => s.Name == "delete_user");
    }

    [Fact]
    public void Extract_Python_EmptySource_ReturnsEmpty()
    {
        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract("", "empty.py", "python", spec);
        symbols.Should().BeEmpty();
    }

    [Fact]
    public void Extract_Python_OnlyComments_ReturnsEmpty()
    {
        const string source = """
            # This is a comment
            # Another comment
            """;
        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract(source, "comments.py", "python", spec);
        symbols.Should().BeEmpty();
    }

    [Theory]
    [InlineData("def hello(): pass", "hello", SymbolKind.Function)]
    [InlineData("class Foo: pass", "Foo", SymbolKind.Class)]
    public void Extract_Python_SingleLine_ExtractsCorrectly(string source, string expectedName, SymbolKind expectedKind)
    {
        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract(source, "test.py", "python", spec);
        symbols.Should().Contain(s => s.Name == expectedName && s.Kind == expectedKind);
    }

    // ── JavaScript ────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_JavaScript_FunctionDeclaration_ReturnsSymbol()
    {
        const string source = """
            function calculateTotal(items) {
                return items.reduce((sum, item) => sum + item.price, 0);
            }
            """;

        var spec = LanguageRegistry.All["javascript"];
        var symbols = _sut.Extract(source, "cart.js", "javascript", spec);

        symbols.Should().Contain(s => s.Name == "calculateTotal" && s.Kind == SymbolKind.Function);
    }

    [Fact]
    public void Extract_JavaScript_ClassWithMethod_ReturnsMultipleSymbols()
    {
        const string source = """
            class ShoppingCart {
                add(item) {
                    this.items.push(item);
                }
                remove(id) {
                    this.items = this.items.filter(i => i.id !== id);
                }
            }
            """;

        var spec = LanguageRegistry.All["javascript"];
        var symbols = _sut.Extract(source, "cart.js", "javascript", spec);

        symbols.Should().Contain(s => s.Name == "ShoppingCart" && s.Kind == SymbolKind.Class);
        symbols.Should().Contain(s => s.Name == "add" && s.Kind == SymbolKind.Method);
    }

    // ── TypeScript ────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_TypeScript_Interface_ReturnsSymbol()
    {
        const string source = """
            export interface IUserRepository {
                findById(id: string): Promise<User>;
                save(user: User): Promise<void>;
            }
            """;

        var spec = LanguageRegistry.All["typescript"];
        var symbols = _sut.Extract(source, "repos.ts", "typescript", spec);

        symbols.Should().Contain(s => s.Name == "IUserRepository" && s.Kind == SymbolKind.Interface);
    }

    // ── Go ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_Go_FunctionAndMethod_ReturnsBoth()
    {
        const string source = """
            func NewServer(port int) *Server {
                return &Server{port: port}
            }

            func (s *Server) Start() error {
                return http.ListenAndServe(fmt.Sprintf(":%d", s.port), s.router)
            }
            """;

        var spec = LanguageRegistry.All["go"];
        var symbols = _sut.Extract(source, "server.go", "go", spec);

        symbols.Should().Contain(s => s.Name == "NewServer" && s.Kind == SymbolKind.Function);
        symbols.Should().Contain(s => s.Name == "Start" && s.Kind == SymbolKind.Method);
    }

    // ── Rust ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_Rust_PublicFunction_ReturnsSymbol()
    {
        const string source = """
            pub fn parse_config(path: &str) -> Result<Config, Error> {
                let content = std::fs::read_to_string(path)?;
                Ok(toml::from_str(&content)?)
            }
            """;

        var spec = LanguageRegistry.All["rust"];
        var symbols = _sut.Extract(source, "config.rs", "rust", spec);

        symbols.Should().Contain(s => s.Name == "parse_config" && s.Kind == SymbolKind.Function);
    }

    // ── CSharp ────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_CSharp_ClassAndMethod_ReturnsBoth()
    {
        const string source = """
            public class OrderService
            {
                public async Task<Order> CreateOrderAsync(CreateOrderRequest request)
                {
                    var order = new Order(request.CustomerId);
                    await _repository.SaveAsync(order);
                    return order;
                }
            }
            """;

        var spec = LanguageRegistry.All["csharp"];
        var symbols = _sut.Extract(source, "OrderService.cs", "csharp", spec);

        symbols.Should().Contain(s => s.Name == "OrderService" && s.Kind == SymbolKind.Class);
        symbols.Should().Contain(s => s.Name == "CreateOrderAsync" && s.Kind == SymbolKind.Method);
    }

    // ── Symbol ID ─────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_SymbolId_HasCorrectFormat()
    {
        const string source = "def my_func(): pass";
        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract(source, "mod/utils.py", "python", spec);

        symbols.Should().ContainSingle();
        var id = symbols[0].Id;
        id.Should().Contain("::");
        id.Should().Contain("#");
        id.Should().StartWith("mod/utils.py");
        id.Should().EndWith("#function");
    }

    [Fact]
    public void Extract_ByteOffsets_AreNonNegativeAndOrdered()
    {
        const string source = """
            def first(): pass
            def second(): pass
            def third(): pass
            """;

        var spec = LanguageRegistry.All["python"];
        var symbols = _sut.Extract(source, "f.py", "python", spec)
                          .OrderBy(s => s.ByteStart).ToList();

        symbols.Should().HaveCount(3);
        for (int i = 0; i < symbols.Count; i++)
        {
            symbols[i].ByteStart.Should().BeGreaterOrEqualTo(0);
            symbols[i].ByteEnd.Should().BeGreaterOrEqualTo(symbols[i].ByteStart);
        }

        // Offsets should be increasing
        symbols.Zip(symbols.Skip(1))
            .Should().AllSatisfy(pair => pair.First.ByteStart.Should().BeLessThan(pair.Second.ByteStart));
    }

    [Fact]
    public void Extract_ContentHash_IsDeterministic()
    {
        const string source = "def foo(): pass";
        var spec = LanguageRegistry.All["python"];

        var symbols1 = _sut.Extract(source, "f.py", "python", spec);
        var symbols2 = _sut.Extract(source, "f.py", "python", spec);

        symbols1[0].ContentHash.Should().Be(symbols2[0].ContentHash);
    }

    [Fact]
    public void Extract_ContentHash_DiffersWhenContentChanges()
    {
        var spec = LanguageRegistry.All["python"];
        var s1 = _sut.Extract("def foo(): pass", "f.py", "python", spec);
        var s2 = _sut.Extract("def foo(): return 42", "f.py", "python", spec);

        s1[0].ContentHash.Should().NotBe(s2[0].ContentHash);
    }
}
