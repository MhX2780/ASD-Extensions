using ASD.Extensibility;
using AsdShared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace EnvEditor;

public sealed class EnvEditorExtension : IAsdExtension
{
    public string Id => "MhX2780.EnvEditor";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "env", Title = "Env & PATH", Glyph = "", Create = () => new EnvView(host.DataFolder) });
    }
}

/// <summary>
/// The user's environment variables (HKEY_CURRENT_USER\Environment: no administrator needed) in two tabs: all variables, and the PATH as a list.
/// Values are read and written without expanding %NAME%, so nothing is lost.
/// </summary>
internal sealed class EnvView : StackPanel
{
    private static readonly SolidColorBrush Red = new(Windows.UI.Color.FromArgb(255, 235, 87, 87));

    private readonly string _dataFolder;
    private readonly ToggleButton _tabVars = new() { Content = "Variables", IsChecked = true };
    private readonly ToggleButton _tabPath = new() { Content = "PATH" };
    private readonly StackPanel _varsPanel = new() { Spacing = 10 };
    private readonly StackPanel _pathPanel = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    private readonly TextBlock _note = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    // variables
    private readonly ListView _vars = new() { SelectionMode = ListViewSelectionMode.None, Height = 300 };
    private readonly Button _newVar = new() { Content = "New variable" };
    private readonly TextBlock _varsStatus = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _machine = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, IsTextSelectionEnabled = true };

    // PATH
    private readonly List<string> _entries = new();
    private readonly ListView _pathList = new() { SelectionMode = ListViewSelectionMode.None, Height = 300 };
    private readonly TextBox _addEntry = new() { PlaceholderText = "A folder to add to the PATH", MinWidth = 360 };
    private readonly Button _add = new() { Content = "Add" };
    private readonly Button _browse = new() { Content = "Browse..." };
    private readonly Button _removeMissing = new() { Content = "Remove missing folders" };
    private readonly Button _savePath = new() { Content = "Save the PATH" };
    private readonly Button _reloadPath = new() { Content = "Undo my changes" };
    private readonly TextBlock _pathStatus = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private bool _pathDirty;

    public EnvView(string dataFolder)
    {
        _dataFolder = dataFolder;
        Spacing = 12;
        _note.Text = "These are the variables of your Windows account. Programs that are already open keep the old values until you open them again.";

        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _tabVars, _tabPath } });
        Children.Add(_note);

        var machine = new Expander
        {
            Header = "System variables (read only)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 200, Content = _machine },
        };
        _varsPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _newVar } });
        _varsPanel.Children.Add(_vars);
        _varsPanel.Children.Add(_varsStatus);
        _varsPanel.Children.Add(machine);

        _pathPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _addEntry, _add, _browse } });
        _pathPanel.Children.Add(_pathList);
        _pathPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _savePath, _reloadPath, _removeMissing } });
        _pathPanel.Children.Add(_pathStatus);

        Children.Add(_varsPanel);
        Children.Add(_pathPanel);

        _tabVars.Click += (_, _) => SelectTab(true);
        _tabPath.Click += (_, _) => SelectTab(false);
        _newVar.Click += async (_, _) => await EditVarAsync(null, "");
        _add.Click += (_, _) => AddEntry(_addEntry.Text);
        _addEntry.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) AddEntry(_addEntry.Text); };
        _browse.Click += async (_, _) =>
        {
            var folder = await Pick.FolderAsync();
            if (folder != null) AddEntry(folder);
        };
        _removeMissing.Click += (_, _) =>
        {
            var before = _entries.Count;
            _entries.RemoveAll(e => !Exists(e));
            if (_entries.Count != before) { _pathDirty = true; _pathStatus.Text = (before - _entries.Count) + " missing folder(s) removed from the list. Press Save the PATH to keep it."; }
            RenderPath();
        };
        _savePath.Click += (_, _) => SavePath();
        _reloadPath.Click += (_, _) => { LoadPath(); _pathStatus.Text = "The PATH was read again."; };

        Loaded += (_, _) => { LoadVars(); LoadPath(); LoadMachine(); };
    }

    private void SelectTab(bool vars)
    {
        _tabVars.IsChecked = vars;
        _tabPath.IsChecked = !vars;
        _varsPanel.Visibility = vars ? Visibility.Visible : Visibility.Collapsed;
        _pathPanel.Visibility = vars ? Visibility.Collapsed : Visibility.Visible;
    }

    private static RegistryKey OpenUserEnv() => Registry.CurrentUser.CreateSubKey("Environment", true)!;

    /// <summary>Deleting a value that does not exist still tells every program that the environment changed.</summary>
    private static void Broadcast()
    {
        try { Environment.SetEnvironmentVariable("ASD_ENV_REFRESH", null, EnvironmentVariableTarget.User); }
        catch { }
    }

    // ───────── variables ─────────

    private void LoadVars()
    {
        _vars.Items.Clear();
        try
        {
            using var key = OpenUserEnv();
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (name.Length == 0 || name.Equals("Path", StringComparison.OrdinalIgnoreCase)) continue;   // the PATH has its own tab
                var kind = key.GetValueKind(name);
                if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString) continue;
                var value = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                _vars.Items.Add(MakeVarRow(name, value));
            }
            _varsStatus.Text = _vars.Items.Count == 0 ? "You have no variables of your own yet." : _vars.Items.Count + " variable(s).";
        }
        catch (Exception ex) { _varsStatus.Text = "Could not read the variables: " + ex.Message; }
    }

    private void LoadMachine()
    {
        try
        {
            var lines = new List<string>();
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine))
                lines.Add(e.Key + " = " + e.Value);
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            _machine.Text = string.Join("\n", lines);
        }
        catch { _machine.Text = ""; }
    }

    private FrameworkElement MakeVarRow(string name, string value)
    {
        var shown = value.Length > 140 ? value[..140] + "..." : value;
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = shown, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var edit = new Button { Content = "Edit" };
        edit.Click += async (_, _) => await EditVarAsync(name, value);
        var delete = new Button { Content = "Delete" };
        var confirm = new Button { Content = "Yes, delete it" };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Delete " + name + "?" }, confirm } } };
        delete.Flyout = flyout;
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            try
            {
                using var key = OpenUserEnv();
                key.DeleteValue(name, false);
                Broadcast();
            }
            catch (Exception ex) { _varsStatus.Text = "Could not delete: " + ex.Message; return; }
            LoadVars();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { edit, delete } };

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 4, 4, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        row.Children.Add(text);
        row.Children.Add(buttons);
        return row;
    }

    private async Task EditVarAsync(string? oldName, string oldValue)
    {
        var nameBox = new TextBox { Header = "Name", Text = oldName ?? "", MinWidth = 420 };
        var valueBox = new TextBox { Header = "Value (you can use %OTHER_VARIABLE%)", Text = oldValue, MinWidth = 420 };
        var error = new TextBlock { Foreground = Red, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = oldName == null ? "New variable" : "Edit variable",
            Content = new StackPanel { Spacing = 10, Children = { nameBox, valueBox, error } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var n = nameBox.Text.Trim();
            if (n.Length == 0 || n.Contains('=')) { error.Text = "The name cannot be empty or contain ="; args.Cancel = true; }
            else if (n.Equals("Path", StringComparison.OrdinalIgnoreCase)) { error.Text = "Use the PATH tab for the PATH."; args.Cancel = true; }
        };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception ex) { _varsStatus.Text = ex.Message; return; }
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var value = valueBox.Text;
        try
        {
            using var key = OpenUserEnv();
            key.SetValue(name, value, value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            if (oldName != null && !oldName.Equals(name, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(oldName, false);
            Broadcast();
            _varsStatus.Text = "Saved " + name + ".";
        }
        catch (Exception ex) { _varsStatus.Text = "Could not save: " + ex.Message; }
        LoadVars();
    }

    // ───────── PATH ─────────

    private static bool Exists(string entry)
    {
        try { return System.IO.Directory.Exists(Environment.ExpandEnvironmentVariables(entry)); }
        catch { return false; }
    }

    private void LoadPath()
    {
        _entries.Clear();
        try
        {
            using var key = OpenUserEnv();
            var raw = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) _entries.Add(part);
        }
        catch (Exception ex) { _pathStatus.Text = "Could not read the PATH: " + ex.Message; }
        _pathDirty = false;
        RenderPath();
    }

    private void AddEntry(string text)
    {
        var entry = text.Trim().Trim('"');
        if (entry.Length == 0) return;
        if (_entries.Any(e => e.TrimEnd('\\').Equals(entry.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) { _pathStatus.Text = "This folder is already in the PATH."; return; }
        _entries.Add(entry);
        _pathDirty = true;
        _addEntry.Text = "";
        _pathStatus.Text = "Added to the list. Press Save the PATH to keep it.";
        RenderPath();
    }

    private void RenderPath()
    {
        _pathList.Items.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _entries.Count; i++)
        {
            var index = i;
            var entry = _entries[i];
            var duplicate = !seen.Add(entry.TrimEnd('\\'));
            var missing = !Exists(entry);

            var text = new TextBlock { Text = entry, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var flag = new TextBlock
            {
                Text = missing ? "folder not found" : duplicate ? "duplicate" : "",
                Foreground = missing ? Red : null,
                Opacity = 0.8, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            };
            var up = new Button { Content = "Up", IsEnabled = index > 0 };
            up.Click += (_, _) => Move(index, -1);
            var down = new Button { Content = "Down", IsEnabled = index < _entries.Count - 1 };
            down.Click += (_, _) => Move(index, 1);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { _entries.RemoveAt(index); _pathDirty = true; RenderPath(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { up, down, remove } };

            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 2, 4, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(flag, 1);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(text);
            row.Children.Add(flag);
            row.Children.Add(buttons);
            _pathList.Items.Add(row);
        }
        _savePath.IsEnabled = _pathDirty;
        _reloadPath.IsEnabled = _pathDirty;
        if (_pathStatus.Text.Length == 0) _pathStatus.Text = _entries.Count + " folder(s) in your PATH. Windows looks at them from the top, so the order matters.";
    }

    private void Move(int index, int step)
    {
        var target = index + step;
        if (target < 0 || target >= _entries.Count) return;
        (_entries[index], _entries[target]) = (_entries[target], _entries[index]);
        _pathDirty = true;
        RenderPath();
    }

    private void SavePath()
    {
        try
        {
            using var key = OpenUserEnv();
            var old = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            var backup = System.IO.Path.Combine(_dataFolder, "path-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            System.IO.File.WriteAllText(backup, old);
            key.SetValue("Path", string.Join(';', _entries), RegistryValueKind.ExpandString);
            Broadcast();
            _pathDirty = false;
            _pathStatus.Text = "Saved. Programs that are already open keep the old PATH. The old value was saved to " + backup;
        }
        catch (Exception ex) { _pathStatus.Text = "Could not save the PATH: " + ex.Message; }
        RenderPath();
    }
}
