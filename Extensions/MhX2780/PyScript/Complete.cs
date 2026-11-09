using System.Text.RegularExpressions;

namespace PyScript;

/// <param name="Kind">class, function, variable, string, number, module, keyword, built-in, snippet</param>
/// <param name="Detail">shown at the right of the bubble: the type and value of a variable, the arguments of a function...</param>
internal sealed record Suggestion(string Label, string Insert, string Kind, string Detail = "")
{
    public override string ToString() => Label;
}

internal sealed record Problem(int Line, int Col, string Message, string Source)
{
    public override string ToString() => $"Line {Line}: {Message}   [{Source}]";
}

/// <summary>What to suggest at the caret: the names the person made in the file (with their type and value), snippets, built-ins, keywords. (Members of a module come from Python itself, see the window.)</summary>
internal static class Complete
{
    internal static readonly string[] Keywords =
        "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield True False None".Split(' ');

    internal static readonly string[] Builtins =
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

    private static string Trunc(string s, int max = 24) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static bool IsStringStart(string v)
    {
        var i = 0;
        while (i < v.Length && i < 2 && "rRbBfFuU".IndexOf(v[i]) >= 0) i++;
        return i < v.Length && (v[i] == '"' || v[i] == '\'');
    }

    /// <summary>What a value written after "name =" is: its kind for the badge and the text shown at the right of the bubble.</summary>
    private static (string Kind, string Detail) Classify(string v)
    {
        if (v.Length == 0) return ("variable", "");
        if (IsStringStart(v)) return ("string", "str · " + Trunc(v));
        if (Regex.IsMatch(v, @"^-?\d[\d_]*$")) return ("number", "int · " + Trunc(v));
        if (Regex.IsMatch(v, @"^-?(\d[\d_]*)?\.\d*([eE][+-]?\d+)?$") || Regex.IsMatch(v, @"^-?\d[\d_]*[eE][+-]?\d+$")) return ("number", "float · " + Trunc(v));
        if (v is "True" or "False") return ("variable", "bool · " + v);
        if (v == "None") return ("variable", "None");
        if (v.StartsWith('[')) return ("variable", "list · " + Trunc(v));
        if (v.StartsWith('{')) return ("variable", (v.Contains(':') ? "dict · " : "set · ") + Trunc(v));
        if (v.StartsWith('(')) return ("variable", "tuple · " + Trunc(v));
        if (v.StartsWith("input(")) return ("string", "str · input()");
        foreach (var (call, type, kind) in new[] { ("str(", "str", "string"), ("int(", "int", "number"), ("float(", "float", "number"), ("len(", "int", "number"), ("list(", "list", "variable"), ("dict(", "dict", "variable"), ("set(", "set", "variable") })
            if (v.StartsWith(call)) return (kind, type + " · " + Trunc(v));
        return ("variable", "= " + Trunc(v));
    }

    /// <summary>The code with the inside of strings and comments replaced by spaces (same length, same lines), so that words in a string are not taken for names.</summary>
    internal static string Blank(string text)
    {
        var chars = text.ToCharArray();
        foreach (var (start, length, kind) in Highlight.Scan(text))
            if (kind is Tok.String or Tok.Comment)
                for (var k = start; k < start + length && k < chars.Length; k++)
                    if (chars[k] != '\n') chars[k] = ' ';
        return new string(chars);
    }

    private static IEnumerable<string> ImportNames(string list, bool fromImport)
    {
        foreach (var raw in list.Split(','))
        {
            var part = raw.Trim().Trim('(', ')').Trim();
            if (part.Length == 0 || part == "*") continue;
            var asAt = part.IndexOf(" as ", StringComparison.Ordinal);
            var name = asAt >= 0 ? part[(asAt + 4)..].Trim() : fromImport ? part : part.Split('.')[0];
            if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) yield return name;
        }
    }

    private static IEnumerable<string> TargetNames(string list)
    {
        foreach (var raw in list.Split(','))
        {
            var name = raw.Trim().Trim('(', ')', '[', ']', '*').Trim();
            if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) yield return name;
        }
    }

    /// <param name="all">true = also when nothing is typed yet (Ctrl+Space)</param>
    public static List<Suggestion> Local(string text, string prefix, bool all = false)
    {
        var result = new List<Suggestion>();
        if (prefix.Length == 0 && !all) return result;
        bool Fits(string s) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !s.Equals(prefix, StringComparison.Ordinal);

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');   // a TextBox keeps '\r'; the patterns below need '\n'
        var clean = Blank(text);

        // what the person made: name -> kind and the text at the right of the bubble
        var info = new Dictionary<string, (string Kind, string Detail)>();
        void Set(string name, string kind, string detail)
        {
            if (info.TryGetValue(name, out var old) && (old.Kind is "class" or "function") && kind is not ("class" or "function")) return;
            info[name] = (kind, detail);
        }

        foreach (Match m in Regex.Matches(clean, @"^[ \t]*import[ \t]+([^\n]+)", RegexOptions.Multiline))
            foreach (var name in ImportNames(m.Groups[1].Value, false)) Set(name, "module", "import");
        foreach (Match m in Regex.Matches(clean, @"^[ \t]*from[ \t]+[\w.]+[ \t]+import[ \t]+([^\n]+)", RegexOptions.Multiline))
            foreach (var name in ImportNames(m.Groups[1].Value, true)) Set(name, "module", "imported");
        foreach (Match m in Regex.Matches(clean, @"^[ \t]*class[ \t]+([A-Za-z_]\w*)", RegexOptions.Multiline))
            Set(m.Groups[1].Value, "class", "");
        foreach (Match m in Regex.Matches(clean, @"^[ \t]*(?:async[ \t]+)?def[ \t]+([A-Za-z_]\w*)[ \t]*\(([^)\n]*)", RegexOptions.Multiline))
        {
            var args = m.Groups[2].Value.Trim();
            Set(m.Groups[1].Value, "function", Trunc("(" + args + ")", 30));
            foreach (var part in args.Split(','))
            {
                var name = part.Split('=')[0].Split(':')[0].Trim().TrimStart('*').Trim();
                if (name.Length > 0 && name != "self" && name != "cls" && Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) Set(name, "variable", "parameter");
            }
        }
        foreach (Match m in Regex.Matches(clean, @"^[ \t]*(?:async[ \t]+)?for[ \t]+(.+?)[ \t]+in\b", RegexOptions.Multiline))
            foreach (var name in TargetNames(m.Groups[1].Value)) Set(name, "variable", "loop variable");
        foreach (Match m in Regex.Matches(clean, @"^[ \t]*(?:async[ \t]+)?(?:with|except)\b[^\n]*?\bas[ \t]+([A-Za-z_]\w*)", RegexOptions.Multiline))
            Set(m.Groups[1].Value, "variable", "as");
        foreach (Match m in Regex.Matches(clean, @"^[ \t]*([A-Za-z_]\w*)[ \t]*(?::[^=\n]+)?=(?!=)(.*)$", RegexOptions.Multiline))
        {
            var g = m.Groups[2];
            var length = g.Value.TrimEnd().Length;
            var (kind, detail) = Classify(text.Substring(g.Index, length).Trim());   // the value is read from the real text (the cleaned one has no strings)
            Set(m.Groups[1].Value, kind, detail);
        }

        var seen = new HashSet<string>();
        foreach (Match m in Regex.Matches(clean, "[A-Za-z_][A-Za-z0-9_]*"))
        {
            var name = m.Value;
            if (!Fits(name) || !seen.Add(name) || Keywords.Contains(name)) continue;
            result.Add(info.TryGetValue(name, out var known) ? new Suggestion(name, name, known.Kind, known.Detail) : new Suggestion(name, name, "variable"));
        }
        foreach (var s in Snippets)
            if (s.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(new Suggestion(s.Label, s.Text, "snippet"));
        foreach (var b in Builtins) if (Fits(b) && seen.Add(b)) result.Add(new Suggestion(b, b, "built-in"));
        foreach (var k in Keywords) if (Fits(k)) result.Add(new Suggestion(k, k, "keyword"));
        return result.Take(30).ToList();
    }
}
