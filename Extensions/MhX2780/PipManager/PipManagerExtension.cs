using System.Text.Json;
using ASD.Extensibility;
using AsdPython;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PipManager;

public sealed class PipManagerExtension : IAsdExtension
{
    public string Id => "MhX2780.PipManager";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "pip", Title = "Pip Manager", Glyph = "", Create = () => new PipView() });
    }
}

/// <summary>The installed Python packages with Update and Uninstall, a box to install new ones, and the live output of pip.</summary>
internal sealed class PipView : Grid
{
    private sealed record Pkg(string Name, string Version);

    private readonly TextBlock _info = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter the installed packages", MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _install = new() { PlaceholderText = "Package to install, for example: requests   or   numpy==1.26.4", MinWidth = 400 };
    private readonly Button _installButton = new() { Content = "Install" };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _check = new() { Content = "Check for updates" };
    private readonly Button _updateAll = new() { Content = "Update all", Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly ProgressRing _ring = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, Height = 360 };
    private readonly OutputView _output = new() { Height = 170 };
    private readonly List<Pkg> _packages = new();
    private readonly Dictionary<string, string> _outdated = new(StringComparer.OrdinalIgnoreCase);   // name -> newest version
    private PyInfo? _py;
    private bool _busy;
    private bool _loaded;
    private CancellationTokenSource? _cts;

    public PipView()
    {
        RowSpacing = 10;
        for (var i = 0; i < 6; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Spacing = 8 };
        head.Children.Add(_info);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _refresh, _check, _updateAll, _ring, _cancel } });
        Put(head, 0);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _install, _installButton } }, 1);
        Put(_filter, 2);
        Put(_list, 3);
        Put(_status, 4);
        Put(_output, 5);

        _refresh.Click += async (_, _) => { if (_py == null) await LoadAsync(); else if (!_busy) await ReadPackagesAsync(); };
        _check.Click += async (_, _) => await CheckUpdatesAsync();
        _updateAll.Click += async (_, _) => await UpdateAllAsync();
        _installButton.Click += async (_, _) => await InstallTypedAsync();
        _install.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await InstallTypedAsync(); };
        _filter.TextChanged += (_, _) => Render();
        _cancel.Click += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        };
    }

    private void Put(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        Children.Add(element);
    }

    private void SetBusy(bool busy, string status, bool cancellable = false)
    {
        _busy = busy;
        _refresh.IsEnabled = _check.IsEnabled = _updateAll.IsEnabled = _installButton.IsEnabled = !busy;
        _list.IsEnabled = !busy;
        _ring.IsActive = busy;
        _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = status;
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        SetBusy(true, "Looking for Python...");
        _py = await Task.Run(Py.Find);
        if (_py == null)
        {
            Py.Forget();   // so that Refresh looks again
            SetBusy(false, "Python 3 was not found. Install it from python.org (tick 'Add python.exe to PATH'), then press Refresh.");
            return;
        }
        _info.Text = _py.Version + "   (" + _py.Command + ")";
        await ReadPackagesAsync();
    }

    /// <summary>The JSON array that pip prints (it is on one line; warnings may come before or after it).</summary>
    private static List<(string Name, string Version, string Latest)> ParseList(string text)
    {
        var result = new List<(string Name, string Version, string Latest)>();
        var json = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("[{") && l.EndsWith("}]"));
        if (json == null) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var version = e.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                var latest = e.TryGetProperty("latest_version", out var l) ? l.GetString() ?? "" : "";
                if (name.Length > 0) result.Add((name, version, latest));
            }
        }
        catch { }
        return result;
    }

    private async Task ReadPackagesAsync()
    {
        var py = _py!;
        SetBusy(true, "Reading the installed packages...");
        var (code, text) = await Task.Run(() => Py.RunQuick(py.File, py.Prefix.Concat(new[] { "-m", "pip", "list", "--format=json", "--disable-pip-version-check" }), 90000));
        _packages.Clear();
        foreach (var p in ParseList(text)) _packages.Add(new Pkg(p.Name, p.Version));
        _packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Render();
        SetBusy(false, code == 0 ? _packages.Count + " packages installed." : "pip could not list the packages: " + text.Trim());
    }

    private async Task CheckUpdatesAsync()
    {
        if (_busy || _py == null) return;
        var py = _py;
        SetBusy(true, "Looking for newer versions (this can take a minute)...");
        var (code, text) = await Task.Run(() => Py.RunQuick(py.File, py.Prefix.Concat(new[] { "-m", "pip", "list", "--outdated", "--format=json", "--disable-pip-version-check" }), 240000));
        _outdated.Clear();
        foreach (var p in ParseList(text)) if (p.Latest.Length > 0) _outdated[p.Name] = p.Latest;
        _updateAll.Visibility = _outdated.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
        SetBusy(false, _outdated.Count > 0 ? _outdated.Count + " package(s) can be updated." : code == 0 ? "Everything is up to date." : "Could not check for updates (is there an internet connection?).");
    }

    private async Task UpdateAllAsync()
    {
        var names = _packages.Where(p => _outdated.TryGetValue(p.Name, out var latest) && latest != p.Version).Select(p => p.Name).ToList();
        if (names.Count == 0) return;
        var args = new List<string> { "install", "--upgrade" };
        args.AddRange(names);
        await RunPipAsync("Updating " + names.Count + " packages...", args.ToArray());
    }

    private async Task InstallTypedAsync()
    {
        var parts = _install.Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var args = new List<string> { "install" };
        args.AddRange(parts);
        await RunPipAsync("Installing " + string.Join(' ', parts) + "...", args.ToArray());
    }

    /// <summary>Runs pip with live output, then reads the list again.</summary>
    private async Task RunPipAsync(string title, params string[] pipArgs)
    {
        if (_busy || _py == null) return;
        var py = _py;
        _cts = new CancellationTokenSource();
        SetBusy(true, title, cancellable: true);
        _output.Clear();
        _output.Add("> pip " + string.Join(' ', pipArgs));
        int code;
        try
        {
            var args = py.Prefix.Concat(new[] { "-m", "pip" }).Concat(pipArgs).Concat(new[] { "--disable-pip-version-check", "--no-input" });
            code = await Py.RunAsync(py.File, args, null, "", (isError, line) => _output.AddFromAnyThread(line, isError), _cts.Token, 1800);
        }
        catch (Exception ex)
        {
            _output.Add(ex.Message, true);
            code = -1;
        }
        var result = code switch
        {
            0 => "Done.",
            -2 => "Cancelled.",
            -3 => "Stopped: it took more than 30 minutes.",
            _ => "pip stopped with code " + code + ". See the output below.",
        };
        await ReadPackagesAsync();
        _status.Text = result;
    }

    private void Render()
    {
        _list.Items.Clear();
        var q = _filter.Text.Trim();
        foreach (var p in _packages)
            if (q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) _list.Items.Add(MakeRow(p));
    }

    private FrameworkElement MakeRow(Pkg p)
    {
        _outdated.TryGetValue(p.Name, out var latest);
        var canUpdate = latest != null && latest != p.Version;

        var name = new TextBlock { Text = p.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var version = new TextBlock { Text = canUpdate ? p.Version + "   ->   " + latest : p.Version, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (canUpdate)
        {
            var update = new Button { Content = "Update" };
            update.Click += async (_, _) => await RunPipAsync("Updating " + p.Name + "...", "install", "--upgrade", p.Name);
            buttons.Children.Add(update);
        }
        var remove = new Button { Content = "Uninstall", IsEnabled = !p.Name.Equals("pip", StringComparison.OrdinalIgnoreCase) };
        var confirm = new Button { Content = "Yes, uninstall" };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Uninstall " + p.Name + "?" }, confirm } } };
        remove.Flyout = flyout;
        confirm.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunPipAsync("Removing " + p.Name + "...", "uninstall", "-y", p.Name);
        };
        buttons.Children.Add(remove);

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 4, 4, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(version, 1);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(name);
        row.Children.Add(version);
        row.Children.Add(buttons);
        return row;
    }
}
