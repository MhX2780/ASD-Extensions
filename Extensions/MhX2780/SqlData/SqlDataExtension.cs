using ASD.Extensibility;
using AsdAi;
using AsdShared;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SqlData;

public sealed class SqlDataExtension : IAsdExtension
{
    public string Id => "MhX2780.SqlData";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "sqldata", Title = "SQL & Data", Glyph = "", Create = () => new SqlPage(host.DataFolder) });
    }
}

/// <summary>Tables on the left; on the right the SQL, the table as an editable grid, and the AI help. A copy of the file is made before the first change.</summary>
internal sealed class SqlPage : Grid
{
    private const int PageSize = 100;

    private readonly string _folder;
    private readonly List<AiModel> _models;
    private Db? _db;
    private string? _table;
    private int _page;
    private long _total;
    private string _exportSql = "";
    private string? _lastError;
    private bool _backedUp, _loading;
    private CancellationTokenSource? _aiCancel;

    private readonly TextBlock _path = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = "No database open." };
    private readonly ListView _tables = new() { SelectionMode = ListViewSelectionMode.Single, Height = 400 };
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), Height = 120, IsSpellCheckEnabled = false, PlaceholderText = "SELECT * FROM my_table WHERE ...     (Ctrl+Enter runs it)" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _gridHead = new() { Orientation = Orientation.Horizontal };
    private readonly ListView _gridList = new() { SelectionMode = ListViewSelectionMode.None, Height = 380 };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    private readonly Button _prev = new() { Content = "◀", IsEnabled = false };
    private readonly Button _next = new() { Content = "▶", IsEnabled = false };
    private readonly Button _add = new() { Content = "Add row", IsEnabled = false };
    private readonly ComboBox _model = new() { MinWidth = 260 };
    private readonly TextBox _ask = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, PlaceholderText = "Ask in your own words. For example: show the 10 customers who spent the most, or: set the price to 0 where the name is empty" };
    private readonly TextBlock _aiOut = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly List<Button> _aiButtons = new();

    public SqlPage(string folder)
    {
        _folder = folder;
        _models = AiModels.Load(folder);
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Accent(out var accent);

        var open = new Button { Content = "Open..." };
        var create = new Button { Content = "New" };
        var import = new Button { Content = "Import CSV..." };
        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, create } });
        left.Children.Add(import);
        left.Children.Add(_path);
        left.Children.Add(new TextBlock { Text = "Tables", FontWeight = FontWeights.SemiBold });
        left.Children.Add(_tables);

        var run = new Button { Content = "Run", Padding = new Thickness(20, 6, 20, 6) };
        if (accent != null) run.Style = accent;
        var profile = new Button { Content = "Profile columns" };
        var export = new Button { Content = "Export CSV..." };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in new UIElement[] { run, profile, export, _add, _prev, _next, _pageLabel }) bar.Children.Add(b);
        var barScroll = new ScrollViewer { Content = bar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };

        var gridBox = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
        gridBox.Children.Add(_gridHead);
        gridBox.Children.Add(_gridList);
        var gridFrame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.4 },
            Child = new ScrollViewer { Content = gridBox, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };

        var manage = new Button { Content = "AI models..." };
        var write = AiButton("Write SQL", "sql", accent);
        var explain = AiButton("Explain / fix", "explain", null);
        var analyze = AiButton("Analyze data", "analyze", null);
        var ai = new StackPanel { Spacing = 8 };
        ai.Children.Add(new TextBlock { Text = "AI help", FontWeight = FontWeights.SemiBold });
        ai.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _model, manage } });
        ai.Children.Add(_ask);
        ai.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { write, explain, analyze } });
        ai.Children.Add(new TextBlock { Text = "Write SQL puts the SQL in the editor: nothing runs until you press Run. The AI sees the table definitions and the first rows of the open table, not the whole data.", Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        ai.Children.Add(_busy);
        ai.Children.Add(_aiOut);

        var right = new StackPanel { Spacing = 8 };
        foreach (var e in new UIElement[] { _editor, barScroll, _status, gridFrame, ai }) right.Children.Add(e);
        SetColumn(right, 1);
        Children.Add(left);
        Children.Add(right);

        var ctrlEnter = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        ctrlEnter.Invoked += (_, e) => { e.Handled = true; RunSql(); };
        _editor.KeyboardAccelerators.Add(ctrlEnter);

        open.Click += async (_, _) => await OpenAsync();
        create.Click += async (_, _) => await NewAsync();
        import.Click += async (_, _) => await ImportAsync();
        run.Click += (_, _) => RunSql();
        profile.Click += (_, _) => ShowProfile();
        export.Click += async (_, _) => await ExportAsync();
        _add.Click += (_, _) => AddRow();
        _prev.Click += (_, _) => { if (_table != null) Browse(_table, _page - 1); };
        _next.Click += (_, _) => { if (_table != null) Browse(_table, _page + 1); };
        manage.Click += async (_, _) => { await AiModelsDialog.ShowAsync(XamlRoot, _folder, _models, withRole: false); RefreshModels(); };
        _tables.SelectionChanged += (_, _) =>
        {
            if (!_loading && _tables.SelectedItem is ListViewItem { Tag: string t }) { _editor.Text = "SELECT * FROM " + Db.Q(t); Browse(t, 0); }
        };
        Unloaded += (_, _) => { _aiCancel?.Cancel(); _db?.Dispose(); _db = null; };

        RefreshModels();
        try
        {
            var last = System.IO.Path.Combine(folder, "last.txt");
            if (File.Exists(last) && File.ReadAllText(last).Trim() is { Length: > 0 } p && File.Exists(p)) OpenDb(p);
        }
        catch { }
    }

    private static void Accent(out Style? style)
    {
        style = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) ? s as Style : null;
    }

    private Button AiButton(string text, string what, Style? accent)
    {
        var b = new Button { Content = text };
        if (accent != null) b.Style = accent;
        b.Click += async (_, _) => await AiAsync(what);
        _aiButtons.Add(b);
        return b;
    }

    private void Say(string text, bool error = false)
    {
        _status.Text = text;
        if (error) _status.Foreground = new SolidColorBrush(Colors.IndianRed); else _status.ClearValue(TextBlock.ForegroundProperty);
    }

    // ───────── databases ─────────

    private void OpenDb(string path)
    {
        try
        {
            var db = new Db(path);
            var check = db.Run("SELECT count(*) FROM sqlite_master");
            if (check.Error != null) { db.Dispose(); Say("This is not an SQLite database: " + check.Error, true); return; }
            _db?.Dispose();
            _db = db;
            _backedUp = false;
            _table = null;
            _path.Text = path;
            _gridHead.Children.Clear();
            _gridList.Items.Clear();
            try { File.WriteAllText(System.IO.Path.Combine(_folder, "last.txt"), path); } catch { }
            RefreshTables(null);
            Say("Opened. A copy of the file is saved before the first change.");
        }
        catch (Exception ex) { Say("Could not open it: " + ex.Message, true); }
    }

    private async Task OpenAsync()
    {
        var path = await Pick.FileAsync();
        if (path != null) OpenDb(path);
    }

    private async Task NewAsync()
    {
        var path = await Pick.SaveAsync("data", ".db", "SQLite database");
        if (path != null) OpenDb(path);
    }

    private void RefreshTables(string? select)
    {
        if (_db == null) return;
        _loading = true;
        _tables.Items.Clear();
        foreach (var (name, kind) in _db.Tables())
        {
            var item = new ListViewItem { Tag = name, Content = new TextBlock { Text = kind == "view" ? name + "  (view)" : name, TextTrimming = TextTrimming.CharacterEllipsis } };
            _tables.Items.Add(item);
            if (name == select) _tables.SelectedItem = item;
        }
        _loading = false;
    }

    private void BackupOnce()
    {
        if (_backedUp || _db == null || !File.Exists(_db.Path) || new FileInfo(_db.Path).Length == 0) return;
        _backedUp = true;
        try { File.Copy(_db.Path, _db.Path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
    }

    private async Task ImportAsync()
    {
        var csv = await Pick.FileAsync();
        if (csv == null) return;
        if (_db == null) { await NewAsync(); if (_db == null) return; }
        try
        {
            BackupOnce();
            var (table, rows) = Csv.Import(_db, csv);
            RefreshTables(table);
            Browse(table, 0);
            Say($"Imported {rows} row(s) into the new table '{table}'.");
        }
        catch (Exception ex) { Say("Could not import: " + ex.Message, true); }
    }

    private async Task ExportAsync()
    {
        if (_db == null || _exportSql.Length == 0) { Say("Show a table or run a query first.", true); return; }
        var path = await Pick.SaveAsync(_table ?? "result", ".csv", "CSV file");
        if (path == null) return;
        var r = _db.Run(_exportSql, null, 1_000_000);
        if (r.Error != null) { Say(r.Error, true); return; }
        try { File.WriteAllText(path, Csv.Write(r), new System.Text.UTF8Encoding(true)); Say($"Exported {r.Rows.Count} row(s) to {path}."); }
        catch (Exception ex) { Say("Could not write the file: " + ex.Message, true); }
    }

    // ───────── the table and its grid ─────────

    private void Browse(string table, int page)
    {
        if (_db == null) return;
        _table = table;
        _total = long.TryParse(_db.Scalar("SELECT COUNT(*) FROM " + Db.Q(table)), out var n) ? n : 0;
        _page = Math.Clamp(page, 0, (int)Math.Max(0, (_total - 1) / PageSize));
        _exportSql = "SELECT * FROM " + Db.Q(table);
        var editable = true;
        var r = _db.Run($"SELECT rowid AS __rowid__, * FROM {Db.Q(table)} LIMIT {PageSize} OFFSET {_page * PageSize}");
        if (r.Error != null) { editable = false; r = _db.Run($"SELECT * FROM {Db.Q(table)} LIMIT {PageSize} OFFSET {_page * PageSize}"); }   // a view, or a table without rowid
        if (r.Error != null) { Say(r.Error, true); return; }
        ShowGrid(r, editable);
        var from = _total == 0 ? 0 : _page * PageSize + 1;
        _pageLabel.Text = $"Rows {from}–{Math.Min(_total, (_page + 1) * PageSize)} of {_total}";
        _prev.IsEnabled = _page > 0;
        _next.IsEnabled = (_page + 1) * PageSize < _total;
        _add.IsEnabled = editable;
        Say(editable ? "Click a cell to change it: it is saved when you leave it. Type NULL for an empty (NULL) value." : "This is a view or a table without rowid: it can be read, not edited here.");
    }

    private void ShowGrid(QueryResult r, bool editable)
    {
        _gridHead.Children.Clear();
        _gridList.Items.Clear();
        var skip = editable ? 1 : 0;
        var names = r.Columns.Skip(skip).ToList();
        if (editable) _gridHead.Children.Add(new Border { Width = 44 });
        foreach (var name in names) _gridHead.Children.Add(new TextBlock { Text = name, Width = 160, Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

        foreach (var row in r.Rows.Take(editable ? PageSize : 500))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var rowid = editable ? row[0] ?? "" : "";
            if (editable)
            {
                var del = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Width = 38, MinWidth = 0, MinHeight = 0, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0) };
                var yes = new Button { Content = "Yes, delete this row" };
                var fly = new Flyout { Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Delete this row?" }, yes } } };
                del.Flyout = fly;
                yes.Click += (_, _) => { fly.Hide(); DeleteRow(rowid); };
                line.Children.Add(del);
            }
            for (var c = 0; c < names.Count; c++)
            {
                var value = row[c + skip];
                var column = names[c];
                var box = new TextBox { Text = value ?? "", PlaceholderText = value == null ? "NULL" : "", Tag = value, Width = 160, MinHeight = 0, Padding = new Thickness(6, 3, 6, 3), IsReadOnly = !editable };
                if (editable)
                {
                    box.LostFocus += (_, _) => Commit(box, column, rowid);
                    box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(box, column, rowid); e.Handled = true; } };
                }
                line.Children.Add(box);
            }
            _gridList.Items.Add(line);
        }
        if (!editable)
        {
            _pageLabel.Text = r.Rows.Count > 500 ? "first 500 rows shown" : $"{r.Rows.Count} row(s)";
            _prev.IsEnabled = _next.IsEnabled = _add.IsEnabled = false;
        }
    }

    private void Commit(TextBox box, string column, string rowid)
    {
        if (_db == null || _table == null) return;
        var original = box.Tag as string;
        var text = box.Text;
        if (original == null ? text.Length == 0 : text == original) return;
        string? value = text == "NULL" ? null : text;
        BackupOnce();
        var r = _db.Run($"UPDATE {Db.Q(_table)} SET {Db.Q(column)} = ? WHERE rowid = ?", new string?[] { value, rowid });
        if (r.Error != null) { box.Text = original ?? ""; Say("The cell was not changed: " + r.Error, true); return; }
        box.Tag = value;
        box.PlaceholderText = value == null ? "NULL" : "";
        Say("Saved.");
    }

    private void DeleteRow(string rowid)
    {
        if (_db == null || _table == null) return;
        BackupOnce();
        var r = _db.Run($"DELETE FROM {Db.Q(_table)} WHERE rowid = ?", new string?[] { rowid });
        if (r.Error != null) { Say(r.Error, true); return; }
        Browse(_table, _page);
        Say("Row deleted.");
    }

    private void AddRow()
    {
        if (_db == null || _table == null) return;
        BackupOnce();
        var r = _db.Run($"INSERT INTO {Db.Q(_table)} DEFAULT VALUES");
        if (r.Error != null) { Say("A row could not be added (a column that must be filled has no default): " + r.Error, true); return; }
        Browse(_table, int.MaxValue);
        Say("An empty row was added at the end.");
    }

    // ───────── SQL ─────────

    private static bool ReadsOnly(string sql)
    {
        foreach (var raw in sql.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("--")) continue;
            var word = new string(line.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
            return word is "SELECT" or "EXPLAIN" or "WITH";
        }
        return true;
    }

    private void RunSql()
    {
        if (_db == null) { Say("Open or create a database first.", true); return; }
        var sql = _editor.Text.Trim();
        if (sql.Length == 0) return;
        if (!ReadsOnly(sql)) BackupOnce();
        var r = _db.Run(sql);
        _lastError = r.Error;
        if (r.Error != null) { Say(r.Error, true); return; }
        if (r.Columns.Count > 0)
        {
            _exportSql = sql;
            ShowGrid(r, false);
            Say($"{r.Rows.Count} row(s){(r.Truncated ? " (the first 5000)" : "")}. This result is read-only: click a table on the left to edit it.");
            return;
        }
        Say($"Done. {r.Changes} row(s) changed.");
        RefreshTables(_table);
        if (_table != null && _db.Tables().Any(t => t.Name == _table)) Browse(_table, _page);
    }

    private QueryResult? ProfileResult()
    {
        if (_db == null || _table == null) { Say("Click a table on the left first.", true); return null; }
        var sql = SqlAi.ProfileSql(_db, _table);
        var r = _db.Run(sql);
        if (r.Error != null) { Say(r.Error, true); return null; }
        _exportSql = sql;
        return r;
    }

    private void ShowProfile()
    {
        var r = ProfileResult();
        if (r == null) return;
        ShowGrid(r, false);
        Say($"Profile of '{_table}': for every column, how many are filled, empty, different, and the smallest, largest and average value.");
    }

    // ───────── AI ─────────

    private void RefreshModels()
    {
        var keep = (_model.SelectedItem as ComboBoxItem)?.Tag as AiModel;
        var on = _models.Where(m => m.Enabled).ToList();
        _model.Items.Clear();
        foreach (var m in on) _model.Items.Add(new ComboBoxItem { Content = AiModels.Display(m), Tag = m });
        _model.SelectedIndex = on.Count == 0 ? -1 : Math.Max(0, keep == null ? 0 : on.IndexOf(keep));
    }

    private async Task AiAsync(string what)
    {
        if (_db == null) { Say("Open a database first.", true); return; }
        var model = (_model.SelectedItem as ComboBoxItem)?.Tag as AiModel;
        if (model == null) { Say("Add an AI model first (AI models...).", true); return; }
        var key = await AiModels.LoadKeyAsync(_folder, model);
        if (key == null) { Say("There is no API key for " + AiModels.Display(model) + ". Add it under 'AI models...'.", true); return; }
        var ask = _ask.Text.Trim();
        if (what == "sql" && ask.Length == 0) { Say("Write what you want in the box above the buttons.", true); return; }
        if (what == "analyze" && _table == null) { Say("Click a table on the left first.", true); return; }

        _aiCancel?.Cancel();
        var cts = _aiCancel = new CancellationTokenSource();
        _busy.Visibility = Visibility.Visible;
        foreach (var b in _aiButtons) b.IsEnabled = false;
        try
        {
            var context = SqlAi.Context(_db, _table);
            switch (what)
            {
                case "sql":
                    _editor.Text = await SqlAi.WriteSqlAsync(model, key, context, ask, cts.Token);
                    Say("The SQL is in the editor. Read it, then press Run: nothing was changed.");
                    break;
                case "explain":
                    if (_editor.Text.Trim().Length == 0) { Say("Write or paste some SQL in the editor first.", true); break; }
                    _aiOut.Text = await SqlAi.ExplainAsync(model, key, context, _editor.Text.Trim(), _lastError, ask, cts.Token);
                    break;
                default:
                    var profile = ProfileResult();
                    if (profile == null) break;
                    _aiOut.Text = await SqlAi.AnalyzeAsync(model, key, context, SqlAi.Rows(profile, 60), ask, cts.Token);
                    Say("The analysis is below.");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say(ex.Message, true); }
        finally
        {
            _busy.Visibility = Visibility.Collapsed;
            foreach (var b in _aiButtons) b.IsEnabled = true;
        }
    }
}
