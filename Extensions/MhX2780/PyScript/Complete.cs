using System.Text.RegularExpressions;

namespace PyScript;

internal sealed record Suggestion(string Label, string Insert, string Kind)
{
    public override string ToString() => Label + "    " + Kind;
}

internal sealed record Problem(int Line, int Col, string Message, string Source)
{
    public override string ToString() => $"Line {Line}: {Message}   [{Source}]";
}

/// <summary>What to suggest at the caret: keywords, built-ins, snippets, names used in the file. (Members of a module come from Python itself, see the window.)</summary>
internal static class Complete
{
    private static readonly string[] Keywords =
        "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield True False None".Split(' ');

    private static readonly string[] Builtins =
        "abs all any bin bool bytes callable chr dict dir divmod enumerate filter float format frozenset getattr hasattr hash hex id input int isinstance issubclass iter len list map max min next object oct open ord pow print range repr reversed round set setattr slice sorted str sum super tuple type vars zip".Split(' ');

    // new lines inside a snippet are '\r': that is what a TextBox uses
    private static readonly (string Label, string Text)[] Snippets =
    {
        ("def", "def name(args):\r    pass\r"),
        ("class", "class Name:\r    def __init__(self):\r        pass\r"),
        ("for", "for item in items:\r    pass\r"),
        ("while", "while condition:\r    pass\r"),
        ("if", "if condition:\r    pass\r"),
        ("try", "try:\r    pass\rexcept Exception as e:\r    print(e)\r"),
        ("with", "with open('file.txt') as f:\r    data = f.read()\r"),
        ("main", "if __name__ == '__main__':\r    main()\r"),
    };

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The word being typed before the caret, and the name before a dot ("os.pa" -> prefix "pa", owner "os").</summary>
    public static (string Prefix, string? Owner) Context(string text, int caret)
    {
        var end = Math.Min(caret, text.Length);
        var start = end;
        while (start > 0 && IsWord(text[start - 1])) start--;
        var prefix = text[start..end];

        string? owner = null;
        if (start > 0 && text[start - 1] == '.')
        {
            var dot = start - 1;
            var from = dot;
            while (from > 0 && IsWord(text[from - 1])) from--;
            if (from < dot) owner = text[from..dot];
        }
        return (prefix, owner);
    }

    public static List<Suggestion> Local(string text, string prefix)
    {
        var result = new List<Suggestion>();
        if (prefix.Length == 0) return result;
        bool Match(string s) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !s.Equals(prefix, StringComparison.Ordinal);

        foreach (var s in Snippets)
            if (s.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(new Suggestion(s.Label, s.Text, "snippet"));
        foreach (var k in Keywords) if (Match(k)) result.Add(new Suggestion(k, k, "keyword"));
        foreach (var b in Builtins) if (Match(b)) result.Add(new Suggestion(b, b, "built-in"));

        var seen = new HashSet<string>(result.Select(r => r.Label));
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, "[A-Za-z_][A-Za-z0-9_]*"))
            if (Match(m.Value) && seen.Add(m.Value)) result.Add(new Suggestion(m.Value, m.Value, "in this file"));
        return result.Take(40).ToList();
    }
}
