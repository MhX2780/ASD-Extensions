using System.Runtime.InteropServices;
using System.Text;

namespace SqlData;

internal sealed class QueryResult
{
    public List<string> Columns = new();
    public List<string?[]> Rows = new();
    public int Changes;
    public bool Truncated;
    public string? Error;
}

/// <summary>SQLite through winsqlite3.dll, which is part of Windows 10 and 11: nothing to install and nothing to ship with the extension.</summary>
internal sealed class Db : IDisposable
{
    private const string Lib = "winsqlite3";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, IntPtr sql, int bytes, out IntPtr stmt, out IntPtr tail);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_column_type(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int i, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_bind_null(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib, CallingConvention = Cdecl)] private static extern int sqlite3_changes(IntPtr db);

    private static readonly IntPtr Transient = new(-1);   // SQLITE_TRANSIENT: SQLite copies the text
    private IntPtr _db;

    public string Path { get; }
    public bool ReadOnly { get; }

    public Db(string path, bool readOnly = false)
    {
        Path = path;
        ReadOnly = readOnly;
        var rc = sqlite3_open_v2(path, out _db, readOnly ? 1 : 2 | 4, IntPtr.Zero);   // read only, or read-write and create
        if (rc != 0)
        {
            var message = Message();
            Dispose();
            throw new InvalidOperationException(message.Length > 0 ? message : "SQLite could not open the file (code " + rc + ").");
        }
        sqlite3_busy_timeout(_db, 3000);
    }

    private string Message() => _db == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "";

    public void Dispose()
    {
        if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; }
    }

    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string? CellText(IntPtr stmt, int i)
    {
        switch (sqlite3_column_type(stmt, i))
        {
            case 5: return null;                                        // NULL
            case 4: return "(blob, " + sqlite3_column_bytes(stmt, i) + " bytes)";
            default:
                var p = sqlite3_column_text(stmt, i);
                return p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p, sqlite3_column_bytes(stmt, i));
        }
    }

    /// <summary>
    /// Runs one or more statements. The rows of the LAST statement that returns rows are kept (at most <paramref name="maxRows"/>).
    /// <paramref name="args"/> are the ? values of the FIRST statement (text; SQLite converts them to the column's type).
    /// </summary>
    public QueryResult Run(string sql, IReadOnlyList<string?>? args = null, int maxRows = 5000)
    {
        var result = new QueryResult();
        var total = Encoding.UTF8.GetByteCount(sql);
        var buffer = Marshal.StringToCoTaskMemUTF8(sql);
        try
        {
            long pos = 0;
            var first = true;
            while (pos < total)
            {
                var rc = sqlite3_prepare_v2(_db, buffer + (int)pos, (int)(total - pos), out var stmt, out var tail);
                if (rc != 0) { result.Error = Message(); break; }
                pos = tail.ToInt64() - buffer.ToInt64();
                if (stmt == IntPtr.Zero) continue;   // only spaces or a comment

                try
                {
                    if (first && args != null)
                        for (var i = 0; i < args.Count; i++)
                            if (args[i] == null) sqlite3_bind_null(stmt, i + 1);
                            else sqlite3_bind_text(stmt, i + 1, args[i]!, -1, Transient);
                    first = false;

                    var columns = sqlite3_column_count(stmt);
                    var rows = new List<string?[]>();
                    var names = new List<string>();
                    for (var i = 0; i < columns; i++) names.Add(Marshal.PtrToStringUTF8(sqlite3_column_name(stmt, i)) ?? "");
                    var truncated = false;
                    while (true)
                    {
                        var step = sqlite3_step(stmt);
                        if (step == 100)
                        {
                            if (rows.Count >= maxRows) { truncated = true; break; }
                            var row = new string?[columns];
                            for (var i = 0; i < columns; i++) row[i] = CellText(stmt, i);
                            rows.Add(row);
                        }
                        else if (step == 101) break;
                        else { result.Error = Message(); break; }
                    }
                    if (result.Error != null) break;
                    if (columns > 0) { result.Columns = names; result.Rows = rows; result.Truncated = truncated; }
                    else result.Changes += sqlite3_changes(_db);
                }
                finally { sqlite3_finalize(stmt); }
            }
        }
        finally { Marshal.FreeCoTaskMem(buffer); }
        return result;
    }

    /// <summary>Runs one INSERT for every row, in one transaction. Null is stored as NULL. Returns the number of rows, or throws.</summary>
    public int BulkInsert(string insertSql, IEnumerable<string?[]> rows)
    {
        var begin = Run("BEGIN");
        if (begin.Error != null) throw new InvalidOperationException(begin.Error);
        var buffer = Marshal.StringToCoTaskMemUTF8(insertSql);
        IntPtr stmt = IntPtr.Zero;
        var count = 0;
        try
        {
            if (sqlite3_prepare_v2(_db, buffer, Encoding.UTF8.GetByteCount(insertSql), out stmt, out _) != 0) throw new InvalidOperationException(Message());
            foreach (var row in rows)
            {
                sqlite3_reset(stmt);
                sqlite3_clear_bindings(stmt);
                for (var i = 0; i < row.Length; i++)
                    if (row[i] == null) sqlite3_bind_null(stmt, i + 1);
                    else sqlite3_bind_text(stmt, i + 1, row[i]!, -1, Transient);
                if (sqlite3_step(stmt) != 101) throw new InvalidOperationException(Message());
                count++;
            }
            Run("COMMIT");
            return count;
        }
        catch { Run("ROLLBACK"); throw; }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    public string? Scalar(string sql, IReadOnlyList<string?>? args = null)
    {
        var r = Run(sql, args, 1);
        return r.Rows.Count > 0 && r.Rows[0].Length > 0 ? r.Rows[0][0] : null;
    }

    /// <summary>Tables and views (not SQLite's own), with their kind.</summary>
    public List<(string Name, string Kind)> Tables()
    {
        var r = Run("SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name");
        return r.Rows.Select(x => (x[0] ?? "", x[1] ?? "table")).ToList();
    }

    public List<(string Name, string Type)> Columns(string table)
    {
        var r = Run("PRAGMA table_info(" + Q(table) + ")");
        return r.Rows.Select(x => (x[1] ?? "", x[2] ?? "")).ToList();
    }

    /// <summary>The CREATE statements of the whole database (for the AI to know the tables).</summary>
    public string Schema()
    {
        var r = Run("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'");
        return string.Join(";\n", r.Rows.Select(x => x[0]));
    }
}
