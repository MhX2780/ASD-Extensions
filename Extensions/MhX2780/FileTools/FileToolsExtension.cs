using System.Diagnostics;
using System.Security.Cryptography;
using ASD.Extensibility;
using AsdShared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FileTools;

public sealed class FileToolsExtension : IAsdExtension
{
    public string Id => "MhX2780.FileTools";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "files", Title = "File Tools", Glyph = "", Create = () => new FileView() });
    }
}

/// <summary>One folder, three tools: rename many files, find duplicates, see the sizes.</summary>
internal sealed class FileView : StackPanel
{
    private static readonly SolidColorBrush Red = new(Windows.UI.Color.FromArgb(255, 235, 87, 87));

    private sealed class RenameItem
    {
        public string Full = "";
        public string NewName = "";
        public string? Problem;
    }

    private sealed class DupFile
    {
        public string Full = "";
        public DateTime Modified;
        public CheckBox? Box;
    }

    private sealed class DupGroup
    {
        public long Size;
        public List<DupFile> Files = new();
    }

    private string? _folder;
    private readonly TextBox _folderBox = new() { IsReadOnly = true, PlaceholderText = "Choose a folder", MinWidth = 420 };
    private readonly Button _choose = new() { Content = "Choose folder" };
    private readonly ToggleButton _tabRename = new() { Content = "Rename files", IsChecked = true };
    private readonly ToggleButton _tabDups = new() { Content = "Duplicates" };
    private readonly ToggleButton _tabSizes = new() { Content = "Folder sizes" };
    private readonly StackPanel _renamePanel = new() { Spacing = 10 };
    private readonly StackPanel _dupPanel = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly StackPanel _sizePanel = new() { Spacing = 10, Visibility = Visibility.Collapsed };

    // rename
    private readonly TextBox _find = new() { Header = "Find in the name", Width = 200 };
    private readonly TextBox _replace = new() { Header = "Replace with", Width = 200 };
    private readonly TextBox _prefix = new() { Header = "Add before the name", Width = 200 };
    private readonly TextBox _suffix = new() { Header = "Add after the name", Width = 200 };
    private readonly TextBox _mask = new() { Header = "Only files like", Text = "*", Width = 200 };
    private readonly CheckBox _number = new() { Content = "Replace the names with numbers (001, 002, ...)" };
    private readonly CheckBox _lowerExt = new() { Content = "Make the extensions lower case" };
    private readonly CheckBox _sub = new() { Content = "Include subfolders" };
    private readonly Button _preview = new() { Content = "Preview" };
    private readonly Button _apply = new() { Content = "Rename", IsEnabled = false };
    private readonly Button _undo = new() { Content = "Undo the last rename", IsEnabled = false };
    private readonly ListView _renameList = new() { SelectionMode = ListViewSelectionMode.None, Height = 280 };
    private readonly TextBlock _renameStatus = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private List<RenameItem> _plan = new();
    private readonly List<(string From, string To)> _undoList = new();

    // duplicates
    private readonly CheckBox _dupSub = new() { Content = "Include subfolders", IsChecked = true };
    private readonly Button _dupScan = new() { Content = "Find duplicates" };
    private readonly Button _dupCancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly Button _dupSelect = new() { Content = "Tick all but the oldest in each group" };
    private readonly Button _dupDelete = new() { Content = "Move the ticked files to the Recycle Bin" };
    private readonly ListView _dupList = new() { SelectionMode = ListViewSelectionMode.None, Height = 330 };
    private readonly TextBlock _dupStatus = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly List<DupGroup> _groups = new();
    private CancellationTokenSource? _dupCts;

    // sizes
    private readonly Button _sizeScan = new() { Content = "Measure" };
    private readonly Button _sizeCancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly ListView _sizeList = new() { SelectionMode = ListViewSelectionMode.None, Height = 380 };
    private readonly TextBlock _sizeStatus = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _sizeCts;

    public FileView()
    {
        Spacing = 12;
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _folderBox, _choose } });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _tabRename, _tabDups, _tabSizes } });

        _renamePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _find, _replace, _prefix, _suffix } });
        _renamePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _mask, _number, _lowerExt, _sub } });
        _renamePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _preview, _apply, _undo } });
        _renamePanel.Children.Add(_renameList);
        _renamePanel.Children.Add(_renameStatus);

        _dupPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _dupScan, _dupCancel, _dupSub } });
        _dupPanel.Children.Add(_dupList);
        _dupPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _dupSelect, _dupDelete } });
        _dupPanel.Children.Add(_dupStatus);

        _sizePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _sizeScan, _sizeCancel } });
        _sizePanel.Children.Add(_sizeList);
        _sizePanel.Children.Add(_sizeStatus);

        Children.Add(_renamePanel);
        Children.Add(_dupPanel);
        Children.Add(_sizePanel);

        // deleting needs a second click
        var confirm = new Button { Content = "Yes, move them" };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Move the ticked files to the Recycle Bin?" }, confirm } } };
        _dupDelete.Flyout = flyout;
        confirm.Click += (_, _) => { flyout.Hide(); DeleteTicked(); };

        _choose.Click += async (_, _) =>
        {
            var path = await Pick.FolderAsync();
            if (path == null) return;
            _folder = path;
            _folderBox.Text = path;
        };
        _tabRename.Click += (_, _) => SelectTab(_tabRename);
        _tabDups.Click += (_, _) => SelectTab(_tabDups);
        _tabSizes.Click += (_, _) => SelectTab(_tabSizes);

        _preview.Click += async (_, _) => await PreviewRenameAsync();
        _apply.Click += (_, _) => ApplyRename();
        _undo.Click += (_, _) => UndoRename();
        _dupScan.Click += async (_, _) => await ScanDuplicatesAsync();
        _dupCancel.Click += (_, _) => _dupCts?.Cancel();
        _dupSelect.Click += (_, _) => TickAllButOldest();
        _sizeScan.Click += async (_, _) => await MeasureAsync();
        _sizeCancel.Click += (_, _) => _sizeCts?.Cancel();
        Unloaded += (_, _) => { _dupCts?.Cancel(); _sizeCts?.Cancel(); };
    }

    private void SelectTab(ToggleButton on)
    {
        _tabRename.IsChecked = on == _tabRename;
        _tabDups.IsChecked = on == _tabDups;
        _tabSizes.IsChecked = on == _tabSizes;
        _renamePanel.Visibility = on == _tabRename ? Visibility.Visible : Visibility.Collapsed;
        _dupPanel.Visibility = on == _tabDups ? Visibility.Visible : Visibility.Collapsed;
        _sizePanel.Visibility = on == _tabSizes ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (unit == 0 ? value.ToString("0") : value.ToString("0.#")) + " " + units[unit];
    }

    private static void Reveal(string path)
    {
        try
        {
            var argument = System.IO.Directory.Exists(path) ? "\"" + path + "\"" : "/select,\"" + path + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = argument });
        }
        catch { }
    }

    // ───────── rename ─────────

    private static List<RenameItem> BuildPlan(string folder, string find, string replace, string prefix, string suffix, string mask, bool number, bool lowerExt, bool sub)
    {
        var plan = new List<RenameItem>();
        var options = new EnumerationOptions { RecurseSubdirectories = sub, IgnoreInaccessible = true };
        var files = System.IO.Directory.EnumerateFiles(folder, mask.Length == 0 ? "*" : mask, options).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counter = 1;
        foreach (var file in files)
        {
            var dir = System.IO.Path.GetDirectoryName(file)!;
            var oldName = System.IO.Path.GetFileName(file);
            var ext = System.IO.Path.GetExtension(file);
            var stem = System.IO.Path.GetFileNameWithoutExtension(file);
            if (find.Length > 0) stem = stem.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
            if (number) stem = (counter++).ToString("D3");
            stem = prefix + stem + suffix;
            if (lowerExt) ext = ext.ToLowerInvariant();
            var newName = stem + ext;
            if (newName == oldName) continue;

            var item = new RenameItem { Full = file, NewName = newName };
            var target = System.IO.Path.Combine(dir, newName);
            if (stem.Length == 0 || newName.IndexOfAny(invalid) >= 0) item.Problem = "not a valid name";
            else if (!taken.Add(target)) item.Problem = "two files would get this name";
            else if (System.IO.File.Exists(target) && !string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) item.Problem = "a file with this name exists";
            plan.Add(item);
        }
        return plan;
    }

    private async Task PreviewRenameAsync()
    {
        if (_folder == null) { _renameStatus.Text = "Choose a folder first."; return; }
        var folder = _folder;
        var find = _find.Text;
        var replace = _replace.Text;
        var prefix = _prefix.Text;
        var suffix = _suffix.Text;
        var mask = _mask.Text.Trim();
        var number = _number.IsChecked == true;
        var lowerExt = _lowerExt.IsChecked == true;
        var sub = _sub.IsChecked == true;
        _renameStatus.Text = "Reading the folder...";
        try { _plan = await Task.Run(() => BuildPlan(folder, find, replace, prefix, suffix, mask, number, lowerExt, sub)); }
        catch (Exception ex) { _renameStatus.Text = "Could not read the folder: " + ex.Message; return; }

        _renameList.Items.Clear();
        foreach (var item in _plan.Take(1000))
        {
            var oldName = System.IO.Path.GetFileName(item.Full);
            var text = new TextBlock { Text = oldName + "   ->   " + item.NewName + (item.Problem != null ? "      (" + item.Problem + ")" : ""), TextTrimming = TextTrimming.CharacterEllipsis };
            if (item.Problem != null) text.Foreground = Red;
            _renameList.Items.Add(text);
        }
        var ok = _plan.Count(p => p.Problem == null);
        _apply.IsEnabled = ok > 0;
        _renameStatus.Text = _plan.Count == 0
            ? "Nothing would change."
            : ok + " file(s) will be renamed" + (_plan.Count > ok ? ", " + (_plan.Count - ok) + " skipped because of a problem" : "") + (_plan.Count > 1000 ? " (the first 1000 are listed)" : "") + ".";
    }

    private void ApplyRename()
    {
        var done = 0;
        var failed = 0;
        _undoList.Clear();
        foreach (var item in _plan.Where(p => p.Problem == null))
        {
            try
            {
                var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Full)!, item.NewName);
                System.IO.File.Move(item.Full, target);
                _undoList.Add((target, item.Full));
                done++;
            }
            catch { failed++; }
        }
        _plan = new List<RenameItem>();
        _renameList.Items.Clear();
        _apply.IsEnabled = false;
        _undo.IsEnabled = _undoList.Count > 0;
        _renameStatus.Text = done + " file(s) renamed" + (failed > 0 ? ", " + failed + " could not be renamed (in use?)" : "") + ". You can undo this.";
    }

    private void UndoRename()
    {
        var done = 0;
        for (var i = _undoList.Count - 1; i >= 0; i--)
        {
            try { System.IO.File.Move(_undoList[i].From, _undoList[i].To); done++; }
            catch { }
        }
        _renameStatus.Text = done + " of " + _undoList.Count + " file(s) got their old name back.";
        _undoList.Clear();
        _undo.IsEnabled = false;
    }

    // ───────── duplicates ─────────

    private async Task ScanDuplicatesAsync()
    {
        if (_folder == null) { _dupStatus.Text = "Choose a folder first."; return; }
        var folder = _folder;
        var sub = _dupSub.IsChecked == true;
        _dupCts?.Cancel();
        var cts = _dupCts = new CancellationTokenSource();
        var ct = cts.Token;
        var queue = DispatcherQueue;
        _dupScan.IsEnabled = false;
        _dupCancel.Visibility = Visibility.Visible;
        _groups.Clear();
        _dupList.Items.Clear();
        _dupStatus.Text = "Looking at the files...";

        try
        {
            var found = await Task.Run(async () =>
            {
                var options = new EnumerationOptions { RecurseSubdirectories = sub, IgnoreInaccessible = true };
                var bySize = new Dictionary<long, List<string>>();
                var seen = 0;
                foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var length = new System.IO.FileInfo(file).Length;
                        if (length > 0)
                        {
                            if (!bySize.TryGetValue(length, out var list)) bySize[length] = list = new List<string>();
                            list.Add(file);
                        }
                    }
                    catch { }
                    if (++seen % 500 == 0) { var n = seen; queue.TryEnqueue(() => _dupStatus.Text = "Looking at the files... " + n); }
                }

                var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
                var total = candidates.Sum(kv => kv.Value.Count);
                var hashed = 0;
                var groups = new List<DupGroup>();
                foreach (var kv in candidates)
                {
                    var byHash = new Dictionary<string, List<string>>();
                    foreach (var file in kv.Value)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            await using var stream = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite, 81920, true);
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                            if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<string>();
                            list.Add(file);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        hashed++;
                        if (hashed % 20 == 0) { var h = hashed; queue.TryEnqueue(() => _dupStatus.Text = "Comparing the contents... " + h + " of " + total); }
                    }
                    foreach (var same in byHash.Values.Where(v => v.Count > 1))
                        groups.Add(new DupGroup { Size = kv.Key, Files = same.Select(f => new DupFile { Full = f, Modified = System.IO.File.GetLastWriteTime(f) }).ToList() });
                }
                return groups.OrderByDescending(g => g.Size * (g.Files.Count - 1)).ToList();
            }, ct);

            _groups.AddRange(found);
            RenderDups();
            var wasted = _groups.Sum(g => g.Size * (g.Files.Count - 1));
            _dupStatus.Text = _groups.Count == 0 ? "No duplicates found." : _groups.Count + " group(s) of identical files, " + FormatSize(wasted) + " could be freed.";
        }
        catch (OperationCanceledException) { _dupStatus.Text = "Cancelled."; }
        catch (Exception ex) { _dupStatus.Text = "The search failed: " + ex.Message; }
        finally
        {
            _dupScan.IsEnabled = true;
            _dupCancel.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderDups()
    {
        _dupList.Items.Clear();
        foreach (var group in _groups)
        {
            _dupList.Items.Add(new TextBlock
            {
                Text = group.Files.Count + " identical files, " + FormatSize(group.Size) + " each",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 2),
            });
            foreach (var file in group.Files)
            {
                file.Box = new CheckBox { Content = file.Full + "   (" + file.Modified.ToString("yyyy-MM-dd HH:mm") + ")", IsChecked = false };
                _dupList.Items.Add(file.Box);
            }
        }
    }

    private void TickAllButOldest()
    {
        foreach (var group in _groups)
        {
            var oldest = group.Files.OrderBy(f => f.Modified).First();
            foreach (var file in group.Files)
                if (file.Box != null) file.Box.IsChecked = !ReferenceEquals(file, oldest);
        }
    }

    private void DeleteTicked()
    {
        var moved = 0;
        long freed = 0;
        var skipped = 0;
        foreach (var group in _groups)
        {
            var ticked = group.Files.Where(f => f.Box?.IsChecked == true).ToList();
            if (ticked.Count == 0) continue;
            if (ticked.Count == group.Files.Count) { skipped++; continue; }   // never remove every copy
            foreach (var file in ticked)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    group.Files.Remove(file);
                    moved++;
                    freed += group.Size;
                }
                catch { }
            }
        }
        _groups.RemoveAll(g => g.Files.Count < 2);
        RenderDups();
        _dupStatus.Text = moved + " file(s) moved to the Recycle Bin (" + FormatSize(freed) + ")" + (skipped > 0 ? ". " + skipped + " group(s) skipped: at least one copy must stay." : ".");
    }

    // ───────── sizes ─────────

    private static long DirectorySize(string path, CancellationToken ct)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(path, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new System.IO.FileInfo(file).Length; }
                catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return total;
    }

    private async Task MeasureAsync()
    {
        if (_folder == null) { _sizeStatus.Text = "Choose a folder first."; return; }
        var folder = _folder;
        _sizeCts?.Cancel();
        var cts = _sizeCts = new CancellationTokenSource();
        var ct = cts.Token;
        var queue = DispatcherQueue;
        _sizeScan.IsEnabled = false;
        _sizeCancel.Visibility = Visibility.Visible;
        _sizeList.Items.Clear();
        _sizeStatus.Text = "Measuring...";
        try
        {
            var rows = await Task.Run(() =>
            {
                var result = new List<(string Name, string Full, long Size)>();
                var entries = System.IO.Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
                var done = 0;
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    long size;
                    if (System.IO.Directory.Exists(entry)) size = DirectorySize(entry, ct);
                    else { try { size = new System.IO.FileInfo(entry).Length; } catch { size = 0; } }
                    result.Add((System.IO.Path.GetFileName(entry), entry, size));
                    done++;
                    var d = done;
                    queue.TryEnqueue(() => _sizeStatus.Text = "Measuring... " + d + " of " + entries.Count);
                }
                return result.OrderByDescending(r => r.Size).Take(300).ToList();
            }, ct);

            var max = rows.Count > 0 ? Math.Max(1, rows[0].Size) : 1;
            foreach (var row in rows) _sizeList.Items.Add(MakeSizeRow(row.Name, row.Full, row.Size, max));
            _sizeStatus.Text = rows.Count == 0 ? "The folder is empty." : "Total: " + FormatSize(rows.Sum(r => r.Size)) + " in the biggest " + rows.Count + " items.";
        }
        catch (OperationCanceledException) { _sizeStatus.Text = "Cancelled."; }
        catch (Exception ex) { _sizeStatus.Text = "Could not measure: " + ex.Message; }
        finally
        {
            _sizeScan.IsEnabled = true;
            _sizeCancel.Visibility = Visibility.Collapsed;
        }
    }

    private static FrameworkElement MakeSizeRow(string name, string full, long size, long max)
    {
        var title = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var amount = new TextBlock { Text = FormatSize(size), Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        var open = new Button { Content = "Show" };
        open.Click += (_, _) => Reveal(full);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = size * 100.0 / max };

        var row = new Grid { ColumnSpacing = 12, RowSpacing = 2, Padding = new Thickness(4, 4, 4, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(amount, 1);
        Grid.SetColumn(open, 2);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 3);
        row.Children.Add(title);
        row.Children.Add(amount);
        row.Children.Add(open);
        row.Children.Add(bar);
        return row;
    }
}
