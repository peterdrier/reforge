using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Reforge;

/// <summary>Lexical features of C# text that predict its Claude token count.</summary>
public readonly record struct LexicalFeatures(
    int IdentifierSubwords, int Keywords, int Punctuation, int Numbers,
    int StringChars, int CommentChars, int IndentedLines);

/// <summary>
/// Keyless token estimate: a linear model over lexical features read from the Roslyn lexer, no
/// intercept, non-negative. Fitted on exact <c>count_tokens</c> counts (claude-opus-5-5) of 3,396 Humans
/// files; about 2% median error on a section total and 10% at worst, so a figure near a threshold is
/// flagged rather than trusted. Lines are read as if the text had LF endings, so a CRLF checkout
/// estimates the same.
/// </summary>
public static partial class TokenEstimator
{
    // Tokens per unit of each feature. Non-ASCII characters were a candidate term and fitted to zero.
    public const double IdentifierSubword = 2.319, Keyword = 1.843, Punctuation = 0.867, Number = 3.067,
        StringChar = 0.408, CommentChar = 0.387, IndentedLine = 1.943;

    public static int Estimate(string text) => Estimate(Measure(text));

    public static int Estimate(LexicalFeatures f) => (int)Math.Round(
        IdentifierSubword * f.IdentifierSubwords + Keyword * f.Keywords + Punctuation * f.Punctuation + Number * f.Numbers
        + StringChar * f.StringChars + CommentChar * f.CommentChars + IndentedLine * f.IndentedLines,
        MidpointRounding.AwayFromZero);

    // Reserved and contextual keywords, counted as keywords wherever they appear as a word.
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do " +
        "double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal " +
        "is lock long namespace new null object operator out override params private protected public readonly ref return sbyte " +
        "sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort " +
        "using virtual void volatile while add alias and async await by descending dynamic equals from get global group init into " +
        "join let managed nameof nint not notnull nuint on or orderby partial record remove required scoped select set unmanaged " +
        "value var when where with yield file").Split(' '), StringComparer.Ordinal);

    public static LexicalFeatures Measure(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        var counts = new Counts();
        var excluded = new List<TextSpan>();
        var skipUntil = -1;
        SyntaxToken previous = default;

        foreach (var token in tree.GetRoot().DescendantTokens(descendIntoTrivia: false))
        {
            var skipped = token.SpanStart < skipUntil;
            if (!skipped)
                foreach (var trivia in token.LeadingTrivia) Trivia(trivia, counts, excluded);

            if (!skipped && !token.IsMissing && token.Span.Length > 0)
            {
                var kind = token.Kind();
                if (kind is SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedVerbatimStringStartToken
                    or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken)
                {
                    // The whole interpolated string, holes included, is one string literal.
                    var span = token.Parent?.Span ?? token.Span;
                    counts.StringChars += LengthWithoutCr(text, span);
                    excluded.Add(span);
                    skipUntil = span.End;
                }
                else if (kind is SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.Utf8StringLiteralToken
                    or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                    or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken)
                {
                    counts.StringChars += LengthWithoutCr(text, token.Span);
                    excluded.Add(token.Span);
                }
                else if (kind is SyntaxKind.NumericLiteralToken) counts.Numbers++;
                else if (kind is SyntaxKind.IdentifierToken || SyntaxFacts.IsKeywordKind(kind)) Word(token.Text, counts);
                // "?." reads as one operator, as "?." does in the lexer the model was fitted with.
                else if (!(kind is SyntaxKind.DotToken && previous.IsKind(SyntaxKind.QuestionToken) && previous.Span.End == token.SpanStart))
                    counts.Punctuation++;
            }

            foreach (var trivia in token.TrailingTrivia)
                if (trivia.SpanStart >= skipUntil) Trivia(trivia, counts, excluded);
            previous = token;
        }

        counts.IndentedLines = IndentedLines(text, excluded);
        return new LexicalFeatures(counts.Subwords, counts.Keywords, counts.Punctuation, counts.Numbers,
            counts.StringChars, counts.CommentChars, counts.IndentedLines);
    }

    private sealed class Counts
    {
        public int Subwords, Keywords, Punctuation, Numbers, StringChars, CommentChars, IndentedLines;
    }

    private static void Word(string word, Counts counts)
    {
        if (Keywords.Contains(word)) counts.Keywords++;
        else counts.Subwords += Math.Max(1, Subword().Count(word));
    }

    private static void Trivia(SyntaxTrivia trivia, Counts counts, List<TextSpan> excluded)
    {
        switch (trivia.Kind())
        {
            case SyntaxKind.SingleLineCommentTrivia:
                counts.CommentChars += trivia.Span.Length;
                break;
            case SyntaxKind.MultiLineCommentTrivia:
            case SyntaxKind.MultiLineDocumentationCommentTrivia:
                counts.CommentChars += trivia.ToString().Count(c => c != '\r');
                excluded.Add(trivia.Span);
                break;
            case SyntaxKind.SingleLineDocumentationCommentTrivia:
                // One "///" comment per line, from the slashes to the line end.
                foreach (var line in trivia.ToFullString().Split('\n'))
                    counts.CommentChars += line.Trim(' ', '\t', '\r').Length;
                break;
            case SyntaxKind.DisabledTextTrivia:
            case SyntaxKind.SkippedTokensTrivia:
            case var _ when trivia.IsDirective:
                Scan(trivia.ToFullString(), counts);
                break;
        }
    }

    /// <summary>A plain scan for text the parser keeps as trivia: directives and disabled code.</summary>
    private static void Scan(string text, Counts counts)
    {
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                counts.CommentChars += text[i..end].TrimEnd('\r').Length;
                i = end;
                continue;
            }
            var m = Lexeme().Match(text, i);
            if (m.Groups["num"].Success) counts.Numbers++;
            else if (m.Groups["word"].Success) Word(m.Value, counts);
            else counts.Punctuation++;
            i += Math.Max(1, m.Length);
        }
    }

    /// <summary>Lines that start with a space or tab, outside multi-line strings and block comments.</summary>
    private static int IndentedLines(string text, List<TextSpan> excluded)
    {
        excluded.Sort((a, b) => a.Start.CompareTo(b.Start));
        int n = 0, next = 0;
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] is ' ' or '\t')
            {
                while (next < excluded.Count && excluded[next].End <= start) next++;
                if (!(next < excluded.Count && excluded[next].Start < start)) n++;
            }
            var eol = text.IndexOf('\n', start);
            if (eol < 0) break;
            start = eol;
        }
        return n;
    }

    private static int LengthWithoutCr(string text, TextSpan span)
    {
        var n = span.Length;
        for (var i = span.Start; i < span.End; i++) if (text[i] == '\r') n--;
        return n;
    }

    [GeneratedRegex("[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|[0-9]+")]
    private static partial Regex Subword();

    [GeneratedRegex(@"\G(?:(?<num>0[xXbB][0-9A-Fa-f_]+[uUlL]*|[0-9][0-9_]*(?:\.[0-9][0-9_]*)?(?:[eE][+-]?[0-9]+)?[fFdDmMuUlL]*)|(?<word>@?[A-Za-z_À-￿][A-Za-z0-9_À-￿]*)|\?\?=|>>=|<<=|=>|==|!=|<=|>=|&&|\|\||\?\?|\?\.|\+\+|--|\+=|-=|\*=|/=|%=|&=|\|=|\^=|::|<<|->|\.\.|.)", RegexOptions.Singleline)]
    private static partial Regex Lexeme();
}
