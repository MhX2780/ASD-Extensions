using System.Globalization;
using System.Text;

namespace SqlData;

internal static class Csv
{
    /// <summary>The separator that is used most in the first line (outside quotes): comma, semicolon or tab.</summary>
    public static char Guess(string text)
    {
        int comma = 0, semicolon = 0, tab = 0;
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && (ch == '\n' || ch == '\r')) break;
            else if (!quoted && ch == ',') comma++;
            else if (!quoted && ch == ';') semicolon++;
            else if (!quoted && ch == '\t') tab++;
        }
        return tab > comma && tab >= semicolon ? '\t' : semicolon > comma ? ';' : ',';
    }

    public static List<string[]> Parse(string text, char separator)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = false; }
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) { quoted = true; any = true; }
            else if (ch == separator) { row.Add(field.ToString()); field.Clear(); any = true; }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
                row.Clear(); field.Clear(); any = false;
            }
            else { field.Append(ch); any = true; }
        }
        if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static string Escape(string? value, char separator)
    {
        if (value == null) return "";
        return value.IndexOfAny(new[] { separator, '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string Write(QueryResult r, char separator = ',')
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, r.Columns.Select(c => Escape(c, separator))));
        foreach (var row in r.Rows) sb.AppendLine(string.Join(separator, row.Select(c => Escape(c, separator))));
        return sb.ToString();
    }

    private static string CleanName(string raw, int index, HashSet<string> used)
    {
        var name = raw.Trim().Trim('﻿');
        if (name.Length == 0) name = "col" + (index + 1);
        var unique = name;
        for (var n = 2; !used.Add(unique.ToLowerInvariant()); n++) unique = name + "_" + n;
        return unique;
    }

    /// <summary>Reads a CSV file (the first line = the column names) into a NEW table. Returns the table name and the number of rows.</summary>
    public static (string Table, int Rows) Import(Db db, string path)
    {
        var text = File.ReadAllText(path);
        var rows = Parse(text, Guess(text));
        if (rows.Count == 0) throw new InvalidOperationException("The file is empty.");
        var used = new HashSet<string>();
        var names = rows[0].Select((h, i) => CleanName(h, i, used)).ToList();
        var data = rows.Skip(1).ToList();

        // a column is INTEGER or REAL when every filled value is a number
        var types = new List<string>();
        for (var c = 0; c < names.Count; c++)
        {
            bool ints = true, reals = true, any = false;
            foreach (var row in data.Take(3000))
            {
                var v = c < row.Length ? row[c].Trim() : "";
                if (v.Length == 0) continue;
                any = true;
                if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) ints = false;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) reals = false;
                if (!reals) break;
            }
            types.Add(!any ? "TEXT" : ints ? "INTEGER" : reals ? "REAL" : "TEXT");
        }

        var baseName = new string(System.IO.Path.GetFileNameWithoutExtension(path).Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (baseName.Length == 0) baseName = "imported";
        var existing = db.Tables().Select(t => t.Name.ToLowerInvariant()).ToHashSet();
        var table = baseName;
        for (var n = 2; existing.Contains(table.ToLowerInvariant()); n++) table = baseName + "_" + n;

        var create = db.Run("CREATE TABLE " + Db.Q(table) + " (" + string.Join(", ", names.Select((n, i) => Db.Q(n) + " " + types[i])) + ")");
        if (create.Error != null) throw new InvalidOperationException(create.Error);
        var insert = "INSERT INTO " + Db.Q(table) + " VALUES (" + string.Join(",", names.Select(_ => "?")) + ")";
        var count = db.BulkInsert(insert, data.Select(row =>
        {
            var values = new string?[names.Count];
            for (var c = 0; c < values.Length; c++)
            {
                var v = c < row.Length ? row[c] : "";
                values[c] = v.Length == 0 && types[c] != "TEXT" ? null : v;   // an empty number is NULL, an empty text stays empty
            }
            return values;
        }));
        return (table, count);
    }
}
