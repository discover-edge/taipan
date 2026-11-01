namespace Taipan.Compiler.Lexing;

public static class TokenFacts
{
    public static readonly IReadOnlyDictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind>
    {
        ["def"] = TokenKind.DefKeyword,
        ["class"] = TokenKind.ClassKeyword,
        ["enum"] = TokenKind.EnumKeyword,
        ["let"] = TokenKind.LetKeyword,
        ["var"] = TokenKind.VarKeyword,
        ["const"] = TokenKind.ConstKeyword,
        ["if"] = TokenKind.IfKeyword,
        ["elif"] = TokenKind.ElifKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["for"] = TokenKind.ForKeyword,
        ["in"] = TokenKind.InKeyword,
        ["match"] = TokenKind.MatchKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["pass"] = TokenKind.PassKeyword,
        ["break"] = TokenKind.BreakKeyword,
        ["continue"] = TokenKind.ContinueKeyword,
        ["and"] = TokenKind.AndKeyword,
        ["or"] = TokenKind.OrKeyword,
        ["not"] = TokenKind.NotKeyword,
        ["is"] = TokenKind.IsKeyword,
        ["some"] = TokenKind.SomeKeyword,
        ["None"] = TokenKind.NoneKeyword,
        ["True"] = TokenKind.TrueKeyword,
        ["False"] = TokenKind.FalseKeyword,
        ["as"] = TokenKind.AsKeyword,
        ["from"] = TokenKind.FromKeyword,
        ["import"] = TokenKind.ImportKeyword,
        ["lambda"] = TokenKind.LambdaKeyword,
        ["try"] = TokenKind.TryKeyword,
        ["except"] = TokenKind.ExceptKeyword,
        ["finally"] = TokenKind.FinallyKeyword,
        ["raise"] = TokenKind.RaiseKeyword,
    };

    private static readonly Dictionary<TokenKind, string> KeywordText =
        Keywords.ToDictionary(pair => pair.Value, pair => pair.Key);

    private static readonly Dictionary<TokenKind, string> PunctuationText = new()
    {
        [TokenKind.Plus] = "+",
        [TokenKind.Minus] = "-",
        [TokenKind.Star] = "*",
        [TokenKind.Slash] = "/",
        [TokenKind.SlashSlash] = "//",
        [TokenKind.Percent] = "%",
        [TokenKind.EqualsEquals] = "==",
        [TokenKind.BangEquals] = "!=",
        [TokenKind.Less] = "<",
        [TokenKind.LessEquals] = "<=",
        [TokenKind.Greater] = ">",
        [TokenKind.GreaterEquals] = ">=",
        [TokenKind.Equals] = "=",
        [TokenKind.PlusEquals] = "+=",
        [TokenKind.MinusEquals] = "-=",
        [TokenKind.StarEquals] = "*=",
        [TokenKind.SlashEquals] = "/=",
        [TokenKind.Arrow] = "->",
        [TokenKind.QuestionQuestion] = "??",
        [TokenKind.Question] = "?",
        [TokenKind.Dot] = ".",
        [TokenKind.Comma] = ",",
        [TokenKind.Colon] = ":",
        [TokenKind.OpenParen] = "(",
        [TokenKind.CloseParen] = ")",
        [TokenKind.OpenBracket] = "[",
        [TokenKind.CloseBracket] = "]",
        [TokenKind.OpenBrace] = "{",
        [TokenKind.CloseBrace] = "}",
        [TokenKind.At] = "@",
    };

    /// <summary>How a token kind is named in TP0100 messages (SPEC §2.1).</summary>
    public static string Display(TokenKind kind) => kind switch
    {
        TokenKind.Name => "name",
        TokenKind.Int => "integer",
        TokenKind.Float => "float",
        TokenKind.String => "string",
        TokenKind.FString => "f-string",
        TokenKind.Newline => "newline",
        TokenKind.Indent => "indent",
        TokenKind.Dedent => "dedent",
        TokenKind.EndOfFile => "end of file",
        _ when KeywordText.TryGetValue(kind, out var keyword) => $"'{keyword}'",
        _ => $"'{PunctuationText[kind]}'",
    };

    /// <summary>Describes a found token in TP0100 messages; names include their text.</summary>
    public static string Describe(Token token) =>
        token.Kind == TokenKind.Name ? $"name '{token.Text}'" : Display(token.Kind);
}
