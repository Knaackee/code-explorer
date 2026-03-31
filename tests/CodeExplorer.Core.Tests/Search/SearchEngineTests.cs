using CodeExplorer.Core.Models;
using CodeExplorer.Core.Search;
using FluentAssertions;
using Xunit;

namespace CodeExplorer.Core.Tests.Search;

public sealed class BM25EngineTests
{
    private readonly BM25Engine _sut = new();

    private static Symbol MakeSymbol(string name, string summary, SymbolKind kind = SymbolKind.Function) =>
        new()
        {
            Id            = $"test.py::{name}#function",
            FilePath      = "test.py",
            QualifiedName = name,
            Name          = name,
            Kind          = kind,
            Language      = "python",
            Signature     = $"def {name}():",
            Summary       = summary,
            ContentHash   = "abc123",
        };

    [Fact]
    public void Rank_ExactNameMatch_ScoresHighest()
    {
        var corpus = new[]
        {
            MakeSymbol("authenticate", "Validates user credentials"),
            MakeSymbol("authorize",    "Checks user permissions"),
            MakeSymbol("render_page",  "Renders HTML template"),
        };

        var results = _sut.Rank("authenticate", corpus);

        results.Should().NotBeEmpty();
        results[0].Symbol.Name.Should().Be("authenticate");
    }

    [Fact]
    public void Rank_EmptyCorpus_ReturnsEmpty()
    {
        var results = _sut.Rank("anything", []);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsEmpty()
    {
        var corpus = new[] { MakeSymbol("foo", "bar") };
        var results = _sut.Rank("", corpus);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Rank_SummaryMatch_IsRanked()
    {
        var corpus = new[]
        {
            MakeSymbol("process_payment",    "Handles credit card transactions"),
            MakeSymbol("send_notification",  "Sends email alerts to users"),
            MakeSymbol("validate_input",     "Sanitizes and validates user data"),
        };

        var results = _sut.Rank("email notification", corpus);

        results.Should().NotBeEmpty();
        results[0].Symbol.Name.Should().Be("send_notification");
    }

    [Fact]
    public void Rank_ResultsAreOrderedByScore()
    {
        var corpus = new[]
        {
            MakeSymbol("login",          "User authentication and login process"),
            MakeSymbol("logout",         "End user session"),
            MakeSymbol("register",       "Register a new user account"),
            MakeSymbol("validate_token", "Validate authentication token"),
        };

        var results = _sut.Rank("user authentication", corpus);

        results.Should().BeInDescendingOrder(r => r.Score);
    }

    [Fact]
    public void Rank_IrrelevantQuery_ReturnsLowOrNoResults()
    {
        var corpus = new[]
        {
            MakeSymbol("parse_json",   "Parse JSON data"),
            MakeSymbol("write_file",   "Write content to file"),
        };

        var results = _sut.Rank("xyzzy_nonexistent_term", corpus);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Rank_MultipleTokensInQuery_CombinesScores()
    {
        var corpus = new[]
        {
            MakeSymbol("create_user",       "Creates a new user in the database"),
            MakeSymbol("delete_user",       "Removes user from database"),
            MakeSymbol("update_user_email", "Updates user email address"),
        };

        var results = _sut.Rank("create user database", corpus);
        results.Should().NotBeEmpty();
        results[0].Symbol.Name.Should().Be("create_user");
    }
}

public sealed class FuzzySearchEngineTests
{
    private readonly FuzzySearchEngine _sut = new(threshold: 70);

    private static Symbol MakeSymbol(string name) =>
        new()
        {
            Id = $"f.py::{name}#function", FilePath = "f.py",
            QualifiedName = name, Name = name, Kind = SymbolKind.Function,
            Language = "python", Signature = $"def {name}():", ContentHash = "x",
        };

    [Fact]
    public void Search_ExactMatch_ScoresOne()
    {
        var corpus = new[] { MakeSymbol("authenticate") };
        var results = _sut.Search("authenticate", corpus);
        results.Should().ContainSingle();
        results[0].Score.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void Search_Typo_StillMatches()
    {
        var corpus = new[] { MakeSymbol("authenticate") };
        var results = _sut.Search("authentcate", corpus); // missing 'i'
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void Search_CompletelyDifferent_NoMatch()
    {
        var corpus = new[] { MakeSymbol("authenticate") };
        var results = _sut.Search("zzz_xyz_completely_different", corpus);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Search_SubstringMatch_Matches()
    {
        var corpus = new[] { MakeSymbol("validateUserInput") };
        var results = _sut.Search("validate", corpus);
        results.Should().NotBeEmpty();
    }
}

public sealed class BM25TokenizerTests
{
    [Theory]
    [InlineData("hello world",         new[] { "hello", "world" })]
    [InlineData("get_user_by_id",      new[] { "get", "user", "by", "id" })]
    [InlineData("UserService.login",   new[] { "userservice", "login" })]
    [InlineData("",                    new string[] { })]
    public void Tokenize_VariousInputs_ReturnsExpectedTokens(string input, string[] expected)
    {
        var tokens = BM25Engine.Tokenize(input);
        tokens.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Tokenize_IsLowerCase()
    {
        var tokens = BM25Engine.Tokenize("HelloWorld");
        tokens.Should().AllSatisfy(t => t.Should().Be(t.ToLowerInvariant()));
    }

    [Fact]
    public void Tokenize_FiltersShortTokens()
    {
        var tokens = BM25Engine.Tokenize("a b hello");
        tokens.Should().NotContain("a");
        tokens.Should().NotContain("b");
        tokens.Should().Contain("hello");
    }
}
