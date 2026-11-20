using System.Text;
using System.Text.RegularExpressions;
using AsdAi;

namespace SqlData;

/// <summary>What the AI is told about the database, and the three things it is asked: write SQL, explain SQL, analyze data. It never runs anything: SQL it writes goes to the editor for the person to read and run.</summary>
internal static class SqlAi
{
    private const string SqlSystem =
        "You are an expert in SQLite. Reply with ONLY the SQL: no explanation, no markdown fences. Use only tables and columns that exist in the schema. " +
        "Put names in double quotes. When the request changes data, write the exact UPDATE, INSERT or DELETE with a WHERE clause that matches only the intended rows, and put a SELECT with the same WHERE in a comment line before it.";

    private const string TalkSystem =
        "You are a careful data analyst and SQLite expert helping a person who works in a small database tool. Be concrete and short. " +
        "Write any SQL in a ```sql block. Reply in the language of the person's request (English when there is none). Do not invent columns or values.";

    /// <summary>A SQL query that gives, for every column of the table: filled, empty (NULL), different values, smallest, largest and average of the numbers.</summary>
    public static string ProfileSql(Db db, string table)
    {
        var parts = db.Columns(table).Take(40).Select(c =>
        {
            var q = Db.Q(c.Name);
            return $"SELECT '{c.Name.Replace("'", "''")}' AS \"column\", '{c.Type.Replace("'", "''")}' AS \"type\", COUNT({q}) AS \"filled\", COUNT(*) - COUNT({q}) AS \"empty (NULL)\", " +
                   $"COUNT(DISTINCT {q}) AS \"different\", MIN({q}) AS \"smallest\", MAX({q}) AS \"largest\", " +
                   $"ROUND(AVG(CASE WHEN typeof({q}) IN ('integer','real') THEN {q} END), 3) AS \"average\" FROM {Db.Q(table)}";
        }).ToList();
        return parts.Count == 0 ? "SELECT 'no columns'" : string.Join("\nUNION ALL\n", parts);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public static string Rows(QueryResult r, int count)
    {
        var sb = new StringBuilder(string.Join(" | ", r.Columns)).AppendLine();
        foreach (var row in r.Rows.Take(count)) sb.AppendLine(string.Join(" | ", row.Select(c => c == null ? "NULL" : Clip(c.Replace("\n", " "), 60))));
        return sb.ToString();
    }

    /// <summary>The schema, and the first rows of the table that is open (short, so the call stays small).</summary>
    public static string Context(Db db, string? table)
    {
        var sb = new StringBuilder("=== DATABASE SCHEMA (SQLite) ===\n").AppendLine(Clip(db.Schema(), 6000));
        if (table != null)
        {
            sb.AppendLine().AppendLine($"=== THE TABLE THAT IS OPEN: {table} ===");
            var sample = db.Run("SELECT * FROM " + Db.Q(table) + " LIMIT 8");
            if (sample.Error == null) sb.AppendLine(Rows(sample, 8));
        }
        return sb.ToString();
    }

    public static string CleanSql(string answer)
    {
        var lines = answer.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```")) lines.RemoveAt(0);
        var fence = lines.FindLastIndex(l => l.TrimStart().StartsWith("```"));
        if (fence >= 0) lines = lines.Take(fence).ToList();
        return string.Join("\n", lines).Trim();
    }

    public static async Task<string> WriteSqlAsync(AiModel m, string key, string context, string request, CancellationToken ct)
        => CleanSql(await AiModels.AskAsync(m, key, SqlSystem, context + "\n=== WHAT THE PERSON WANTS ===\n" + request, 1500, ct));

    public static Task<string> ExplainAsync(AiModel m, string key, string context, string sql, string? error, string request, CancellationToken ct)
        => AiModels.AskAsync(m, key, TalkSystem,
            context + "\n=== THE SQL IN THE EDITOR ===\n" + sql + (error != null ? "\n\n=== THE ERROR SQLite GAVE ===\n" + error : "") +
            (request.Length > 0 ? "\n\n=== THE PERSON ASKS ===\n" + request : "") +
            "\n\nExplain what this SQL does in simple words." + (error != null ? " Then explain the cause of the error and give the corrected SQL." : " Point out mistakes or risks if there are any."), 2000, ct);

    public static Task<string> AnalyzeAsync(AiModel m, string key, string context, string profile, string request, CancellationToken ct)
        => AiModels.AskAsync(m, key, TalkSystem,
            context + "\n=== COLUMN PROFILE (filled, empty, different values, smallest, largest, average) ===\n" + profile +
            (request.Length > 0 ? "\n=== THE PERSON ASKS ===\n" + request : "") +
            "\n\nAnalyze this table: the key facts, data-quality problems (empty values, duplicates, outliers, odd formats, mixed types) and what to check or clean next. " +
            "When you suggest a change, give the SQL, and say which rows it touches.", 2500, ct);
}
