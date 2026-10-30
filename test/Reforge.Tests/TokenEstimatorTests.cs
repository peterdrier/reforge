namespace Reforge.Tests;

/// <summary>The lexical features the token estimate is built from, on small fixtures.</summary>
public class TokenEstimatorTests
{
    [Fact]
    public void Identifiers_SplitIntoSubwords_KeywordsCountedApart()
    {
        // FooBarBaz = 3, HTTPServer2 = HTTP + Server + 2; "var" and "value" count as keywords.
        var f = TokenEstimator.Measure("class FooBarBaz { int HTTPServer2; void M() { var value = 1; } }");

        Assert.Equal(3 + 3 + 1, f.IdentifierSubwords);
        Assert.Equal(5, f.Keywords); // class int void var value
        Assert.Equal(1, f.Numbers);
    }

    [Fact]
    public void Punctuation_CountsOperators_WithConditionalAccessAsOne()
    {
        var f = TokenEstimator.Measure("class C { int? M(C c) => c?.M(c) ?? 0; }");

        // { ? ( ) => ?. ( ) ?? ; }
        Assert.Equal(11, f.Punctuation);
    }

    [Fact]
    public void Strings_CountWholeLiterals_HolesIncluded()
    {
        var f = TokenEstimator.Measure("class C { string A = \"abc\"; string B(int b) => $\"a{b}c\"; char D = 'x'; }");

        Assert.Equal("\"abc\"".Length + "$\"a{b}c\"".Length + "'x'".Length, f.StringChars);
        Assert.Equal(1 + 1 + 1 + 1 + 1, f.IdentifierSubwords); // C A B b D: the hole's b is string text
    }

    [Fact]
    public void Comments_CountCharacters_PerDocLine()
    {
        const string source = "/// <summary>Hi.</summary>\n/// More.\nclass C { /* block */ } // tail\n";
        var f = TokenEstimator.Measure(source);

        Assert.Equal("/// <summary>Hi.</summary>".Length + "/// More.".Length + "/* block */".Length + "// tail".Length, f.CommentChars);
    }

    [Fact]
    public void IndentedLines_SkipBlankLines_AndLinesInsideStrings()
    {
        const string source = "class C\n{\n    int x;\n\n    \n    string s = @\"a\n    b\";\n}\n";
        var f = TokenEstimator.Measure(source);

        // "    int x;", the whitespace-only line, and "    string s"; not "    b" inside the literal.
        Assert.Equal(3, f.IndentedLines);
    }

    [Fact]
    public void CrlfText_MeasuresAsLf()
    {
        const string source = "class C\n{\n    /* a\n       b */\n    string s = @\"x\n  y\";\n}\n";

        Assert.Equal(TokenEstimator.Measure(source), TokenEstimator.Measure(source.Replace("\n", "\r\n")));
    }

    [Fact]
    public void Estimate_IsTheWeightedSum()
    {
        var f = new LexicalFeatures(10, 10, 10, 10, 10, 10, 10);
        var expected = 10 * (TokenEstimator.IdentifierSubword + TokenEstimator.Keyword + TokenEstimator.Punctuation + TokenEstimator.Number
            + TokenEstimator.StringChar + TokenEstimator.CommentChar + TokenEstimator.IndentedLine);

        Assert.Equal((int)Math.Round(expected, MidpointRounding.AwayFromZero), TokenEstimator.Estimate(f));
        Assert.Equal(0, TokenEstimator.Estimate("\n\n"));
    }
}
