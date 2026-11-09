namespace PyScript;

internal enum Tok { Keyword, Builtin, String, Number, Comment, Function, Class, Decorator }

/// <summary>Finds the parts of Python code that get a colour: strings, comments, numbers, keywords, built-in names, decorators and the names after def / class. The text uses '\n' for new lines.</summary>
internal static class Highlight
{
    private static readonly HashSet<string> KeywordSet = new(Complete.Keywords);
    private static readonly HashSet<string> BuiltinSet = new(Complete.Builtins);

    public static Windows.UI.Color ColorOf(Tok kind, bool light)
    {
        static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
        if (light)
            return kind switch
            {
                Tok.Keyword => C(175, 0, 219),
                Tok.Builtin => C(121, 94, 38),
                Tok.String => C(163, 21, 21),
                Tok.Number => C(9, 134, 88),
                Tok.Comment => C(0, 128, 0),
                Tok.Function => C(121, 94, 38),
                Tok.Class => C(38, 127, 153),
                _ => C(0, 112, 193),
            };
        return kind switch
        {
            Tok.Keyword => C(197, 134, 192),
            Tok.Builtin => C(220, 220, 170),
            Tok.String => C(206, 145, 120),
            Tok.Number => C(181, 206, 168),
            Tok.Comment => C(106, 153, 85),
            Tok.Function => C(220, 220, 170),
            Tok.Class => C(78, 201, 176),
            _ => C(79, 193, 255),
        };
    }

    private static bool AtLineStart(string s, int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (s[k] == '\n') return true;
            if (s[k] != ' ' && s[k] != '\t') return false;
        }
        return true;
    }

    /// <summary>Reads a string that starts at <paramref name="quote"/> (the prefix, like f or r, starts at <paramref name="start"/>); returns where it ends.</summary>
    private static int ScanString(string s, int start, int quote, List<(int Start, int Length, Tok Kind)> list)
    {
        var n = s.Length;
        var q = s[quote];
        var triple = quote + 2 < n && s[quote + 1] == q && s[quote + 2] == q;
        var j = quote + (triple ? 3 : 1);
        while (j < n)
        {
            if (s[j] == '\\') { j += 2; continue; }
            if (triple)
            {
                if (s[j] == q && j + 2 < n + 0 && s[j + 1] == q && s[j + 2] == q) { j += 3; break; }
                if (s[j] == q && j + 2 == n - 0 - 0 && false) break;
            }
            else
            {
                if (s[j] == q) { j++; break; }
                if (s[j] == '\n') break;   // a string that is not closed ends with its line
            }
            j++;
        }
        j = Math.Min(j, n);
        list.Add((start, j - start, Tok.String));
        return j;
    }

    public static List<(int Start, int Length, Tok Kind)> Scan(string s)
    {
        var list = new List<(int Start, int Length, Tok Kind)>();
        var n = s.Length;
        var i = 0;
        var previousWord = "";   // "def" or "class" give the next name its own colour
        while (i < n)
        {
            var c = s[i];
            if (c == '\n') { previousWord = ""; i++; continue; }
            if (c == '#')
            {
                var end = s.IndexOf('\n', i);
                if (end < 0) end = n;
                list.Add((i, end - i, Tok.Comment));
                i = end;
                continue;
            }
            if (c == '"' || c == '\'') { i = ScanString(s, i, i, list); previousWord = ""; continue; }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                var j = i + 1;
                var hex = c == '0' && j < n && (s[j] == 'x' || s[j] == 'X');
                while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.')) j++;
                if (!hex && j < n && (s[j] == '+' || s[j] == '-') && (s[j - 1] == 'e' || s[j - 1] == 'E'))
                {
                    j++;
                    while (j < n && char.IsDigit(s[j])) j++;
                }
                list.Add((i, j - i, Tok.Number));
                i = j;
                previousWord = "";
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var j = i + 1;
                while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var word = s[i..j];
                if (j < n && (s[j] == '"' || s[j] == '\'') && word.Length <= 2 && word.All(ch => "rRbBfFuU".IndexOf(ch) >= 0))
                {
                    i = ScanString(s, i, j, list);   // r"...", f'...', b"..."
                    previousWord = "";
                    continue;
                }
                Tok? kind = null;
                if (previousWord == "def") kind = Tok.Function;
                else if (previousWord == "class") kind = Tok.Class;
                else if (KeywordSet.Contains(word)) kind = Tok.Keyword;
                else if (BuiltinSet.Contains(word)) kind = Tok.Builtin;
                if (kind != null) list.Add((i, j - i, kind.Value));
                previousWord = word;
                i = j;
                continue;
            }
            if (c == '@' && AtLineStart(s, i))
            {
                var j = i + 1;
                while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.')) j++;
                if (j > i + 1) list.Add((i, j - i, Tok.Decorator));
                i = j;
                previousWord = "";
                continue;
            }
            if (!char.IsWhiteSpace(c)) previousWord = "";
            i++;
        }
        return list;
    }
}
