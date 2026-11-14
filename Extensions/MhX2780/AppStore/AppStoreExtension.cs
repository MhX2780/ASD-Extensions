using System.Text;
using System.Text.RegularExpressions;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppStore;

public sealed class AppStoreExtension : IAsdExtension
{
    public string Id => "MhX2780.AppStore";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "appstore", Title = "Apps Store", Glyph = "", Create = () => new StoreView(host) });
    }
}

/// <summary>Featured apps, search, what is installed, what has an update. Install / update / uninstall run winget and show its progress.</summary>
internal sealed class StoreView : Grid
{
    // (name, winget id, what it is)
    private static readonly (string Name, string Id, string About)[] Featured =
    {
        ("Visual Studio 2022 Community", "Microsoft.VisualStudio.2022.Community", "Microsoft's IDE for .NET, C++ and more. A big download."),
        ("Visual Studio Code", "Microsoft.VisualStudioCode", "A light code editor with thousands of extensions."),
        ("Git", "Git.Git", "Version control."),
        ("GitHub CLI", "GitHub.cli", "GitHub from the command line."),
        ("Python 3.13", "Python.Python.3.13", "The Python language and its tools."),
        ("Node.js (LTS)", "OpenJS.NodeJS.LTS", "JavaScript runtime."),
        (".NET SDK 8", "Microsoft.DotNet.SDK.8", "Build .NET apps."),
        ("Windows Terminal", "Microsoft.WindowsTerminal", "Tabs for PowerShell, cmd and more."),
        ("PowerToys", "Microsoft.PowerToys", "Utilities for power users."),
        ("7-Zip", "7zip.7zip", "File archiver."),
        ("Notepad++", "Notepad++.Notepad++", "Text and code editor."),
        ("VLC media player", "VideoLAN.VLC", "Plays almost any video or audio file."),
        ("Google Chrome", "Google.Chrome", "Web browser."),
        ("Mozilla Firefox", "Mozilla.Firefox", "Web browser."),
        ("Tailscale", "Tailscale.Tailscale", "A private network between your devices."),
        ("Docker Desktop", "Docker.DockerDesktop", "Containers on Windows."),
        ("Wireshark", "Wireshark.Wireshark", "Network protocol analyzer."),
        ("WinSCP", "WinSCP.WinSCP", "SFTP and SCP client."),
    };

    private static readonly Regex SafeId = new(@"^[A-Za-z0-9._+\-]+$");

    private readonly IAsdHost _host;
    private readonly TextBox _query = new() { PlaceholderText = "Search apps (for example: Visual Studio, VLC)", MinWidth = 300 };
    private readonly Button _search = new() { Content = "Search" };
    private readonly Button _reload = new() { Content = "Refresh" };
    private readonly CheckBox _window = new() { Content = "Show the installer's own window (not silent)" };
    private readonly Pivot _tabs = new();
    private readonly ListView _discover = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly ListView _installedList = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly ListView _updatesList = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly TextBlock _discoverTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly TextBlock _log = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = 0.8 };
    private readonly Dictionary<string, string> _installed = new(StringComparer.OrdinalIgnoreCase);   // id -> version
    private readonly Dictionary<string, string> _updates = new(StringComparer.OrdinalIgnoreCase);     // id -> newer version
    private readonly List<AppRow> _installedRows = new();
    private readonly List<AppRow> _updateRows = new();
    private readonly List<string> _logLines = new();
    private List<AppRow>? _results;
    private string _resultsFor = "";
    private bool _busy;
    private bool _wingetOk;
    private bool _loaded;
    private CancellationTokenSource? _cts;
    private ProgressDialog? _progress;
    private bool _downloading;

    public StoreView(IAsdHost host)
    {
        _host = host;
        Icons.Init(host.DataFolder);
        RowSpacing = 10;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _query, _search, _reload } });
        top.Children.Add(_window);
        Children.Add(top);

        var discover = new Grid { RowSpacing = 4 };
        discover.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        discover.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_discover, 1);
        discover.Children.Add(_discoverTitle);
        discover.Children.Add(_discover);
        _tabs.Items.Add(new PivotItem { Header = "Discover", Content = discover });
        _tabs.Items.Add(new PivotItem { Header = "Installed", Content = _installedList });
        _tabs.Items.Add(new PivotItem { Header = "Updates", Content = _updatesList });
        Grid.SetRow(_tabs, 1);
        Children.Add(_tabs);

        var progress = new Grid { ColumnSpacing = 10 };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var progressText = new StackPanel { Spacing = 4 };
        progressText.Children.Add(_status);
        progressText.Children.Add(_bar);
        Grid.SetColumn(_cancel, 1);
        progress.Children.Add(progressText);
        progress.Children.Add(_cancel);
        Grid.SetRow(progress, 2);
        Children.Add(progress);

        var expander = new Expander
        {
            Header = "Details (what winget printed)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 160, Content = _log },
        };
        Grid.SetRow(expander, 3);
        Children.Add(expander);

        _search.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await SearchAsync(); };
        _reload.Click += async (_, _) => await LoadStateAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) => await StartAsync();
    }

    // ───────── start ─────────

    private async Task StartAsync()
    {
        if (_loaded) return;
        _loaded = true;
        RenderAll();
        _status.Text = "Looking for winget…";
        _wingetOk = await Winget.IsAvailableAsync();
        if (!_wingetOk)
        {
            _status.Text = "winget was not found on this PC. Install \"App Installer\" from the Microsoft Store (it is part of Windows 10 and 11), then open this page again.";
            _tabs.IsEnabled = _search.IsEnabled = _reload.IsEnabled = false;
            return;
        }
        await LoadStateAsync();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _tabs.IsEnabled = !busy;
        _search.IsEnabled = _reload.IsEnabled = _query.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = busy;
        _status.Text = status;
    }

    private void AddLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > 150) _logLines.RemoveRange(0, _logLines.Count - 150);
        _log.Text = string.Join("\n", _logLines);
    }

    /// <summary>What the installed apps are, and which of them have an update (two winget calls).</summary>
    private async Task LoadStateAsync()
    {
        if (_busy || !_wingetOk) return;
        SetBusy(true, "Reading the installed apps…");
        try
        {
            var (_, list) = await Winget.RunAsync("list " + Winget.Quiet, null, CancellationToken.None);
            var (_, upgrade) = await Winget.RunAsync("upgrade " + Winget.Quiet, null, CancellationToken.None);
            _installed.Clear();
            _updates.Clear();
            _installedRows.Clear();
            _updateRows.Clear();
            foreach (var row in Winget.ParseTable(list)) { _installed[row.Id] = row.Version; _installedRows.Add(row); }
            foreach (var row in Winget.ParseTable(upgrade)) { _updates[row.Id] = row.Available; _updateRows.Add(row); }
            SetBusy(false, $"{_installedRows.Count} apps installed, {_updateRows.Count} with an update.");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Could not read the installed apps: " + ex.Message);
        }
        RenderAll();
    }

    private async Task SearchAsync()
    {
        if (_busy || !_wingetOk) return;
        var text = _query.Text.Replace("\"", "").Trim();
        if (text.Length == 0) { _results = null; RenderAll(); return; }
        SetBusy(true, "Searching for \"" + text + "\"…");
        try
        {
            var (code, output) = await Winget.RunAsync($"search \"{text}\" {Winget.Quiet}", null, CancellationToken.None);
            _results = Winget.ParseTable(output);
            _resultsFor = text;
            AddLog($"search \"{text}\": {_results.Count} result(s), exit code {code}");
            SetBusy(false, _results.Count == 0 ? "Nothing found." : $"{_results.Count} result(s).");
        }
        catch (Exception ex)
        {
            SetBusy(false, "The search failed: " + ex.Message);
        }
        RenderAll();
        _tabs.SelectedIndex = 0;
    }

    // ───────── install, update, uninstall ─────────

    private string ArgumentsFor(string verb, string id)
    {
        var mode = _window.IsChecked == true ? " --interactive" : " --silent";
        return verb switch
        {
            "install" => $"install --id {id} --exact --accept-package-agreements {Winget.Quiet}{mode}",
            "upgrade" => $"upgrade --id {id} --exact --accept-package-agreements {Winget.Quiet}{mode}",
            _ => $"uninstall --id {id} --exact {Winget.Quiet}{mode}",
        };
    }

    private async Task DoAsync(string verb, string id, string name)
    {
        if (_busy || !_wingetOk) return;
        if (!SafeId.IsMatch(id)) { _status.Text = "This app has an id that cannot be used here (" + id + ")."; return; }

        var label = verb switch { "install" => "Installing", "upgrade" => "Updating", _ => "Removing" };
        _cts = new CancellationTokenSource();
        SetBusy(true, $"{label} {name}…");
        AddLog("> winget " + ArgumentsFor(verb, id));
        _downloading = false;
        _progress = new ProgressDialog(XamlRoot, $"{label} {name}", () => _cts?.Cancel());
        _progress.Show();
        int code;
        try
        {
            code = (await Winget.RunAsync(ArgumentsFor(verb, id), line => DispatcherQueue.TryEnqueue(() => OnLine(label, name, line)), _cts.Token)).Code;
        }
        catch (Exception ex)
        {
            AddLog(ex.Message);
            code = -1;
        }

        _progress?.Close();
        _progress = null;
        var done = code == 0;
        var message = code switch
        {
            0 => $"{name}: done.",
            -2 => $"{name}: cancelled.",
            _ => $"{name}: winget stopped with code 0x{(uint)code:X8}. Open \"Details\" to see why.",
        };
        SetBusy(false, message);
        if (done) _host.Notify("Apps Store", message);
        _loaded = true;
        _busy = false;
        await LoadStateAsync();
        _status.Text = message;
    }

    /// <summary>Everything winget prints while it works: a size line moves the bar; the installer's own phase has no percent.</summary>
    private void OnLine(string label, string name, string line)
    {
        var progress = Winget.ParseProgress(line);
        if (progress is { } p)
        {
            _downloading = true;
            _progress?.SetPercent(p.Fraction, p.Text);
            _bar.IsIndeterminate = false;
            _bar.Value = p.Fraction * 100;
            _status.Text = $"{label} {name}: downloading {p.Text}";
            return;
        }
        if (Winget.IsNoise(line)) return;

        var text = line.Trim();
        AddLog(text);
        if (text.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)) _progress?.SetWorking("Starting the download");
        else if (text.Contains("installer hash", StringComparison.OrdinalIgnoreCase)) _progress?.SetWorking("Checking the download");
        if (_downloading && (text.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) || text.Contains("installer hash", StringComparison.OrdinalIgnoreCase)))
        {
            _bar.IsIndeterminate = true;
            _progress?.SetWorking("The installer is working. Windows does not report a percentage for this part.");
            _status.Text = $"{label} {name}: the installer is working (Windows does not report a percentage for this part)…";
        }
        else if (text.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) || text.Contains("Starting package uninstall", StringComparison.OrdinalIgnoreCase))
        {
            _bar.IsIndeterminate = true;
            _progress?.SetWorking("Working. No percentage is available for this part.");
            _status.Text = $"{label} {name}: working (no percentage is available for this part)…";
        }
    }

    // ───────── the lists ─────────

    private void RenderAll()
    {
        // Discover: the search results, or the featured apps
        _discover.Items.Clear();
        if (_results != null)
        {
            _discoverTitle.Text = $"Results for \"{_resultsFor}\"";
            foreach (var r in _results) _discover.Items.Add(MakeRow(r.Name, r.Id, r.Version.Length > 0 ? "version " + r.Version : ""));
        }
        else
        {
            _discoverTitle.Text = "Featured";
            foreach (var (name, id, about) in Featured) _discover.Items.Add(MakeRow(name, id, about));
        }

        _installedList.Items.Clear();
        foreach (var r in _installedRows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) _installedList.Items.Add(MakeRow(r.Name, r.Id, ""));
        _updatesList.Items.Clear();
        foreach (var r in _updateRows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) _updatesList.Items.Add(MakeRow(r.Name, r.Id, ""));
        if (_updateRows.Count == 0 && _loaded && _wingetOk)
            _updatesList.Items.Add(new TextBlock { Text = "Everything winget knows about is up to date.", Opacity = 0.7, Margin = new Thickness(0, 12, 0, 0) });
    }

    private FrameworkElement MakeRow(string name, string id, string about)
    {
        var isInstalled = _installed.TryGetValue(id, out var version);
        var hasUpdate = _updates.TryGetValue(id, out var available);

        var sub = new StringBuilder(id);
        if (isInstalled && version?.Length > 0) sub.Append("  ·  installed ").Append(version);
        else if (isInstalled) sub.Append("  ·  installed");
        if (hasUpdate) sub.Append("  ·  update to ").Append(available);
        if (about.Length > 0) sub.Append('\n').Append(about);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name.Length > 0 ? name : id, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = sub.ToString(), Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (!isInstalled) buttons.Children.Add(Action("Install", "install", id, name, accent: true));
        else
        {
            if (hasUpdate) buttons.Children.Add(Action("Update", "upgrade", id, name, accent: true));
            buttons.Children.Add(Action("Uninstall", "uninstall", id, name, accent: false));
        }

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = "", FontSize = 22, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(IconFor(icon, id, name));
        row.Children.Add(text);
        row.Children.Add(buttons);
        return row;
    }

    /// <summary>The generic icon, replaced by the app's own picture when one is found (looked for when the row is shown).</summary>
    private static FrameworkElement IconFor(FontIcon fallback, string id, string name)
    {
        var holder = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        var image = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        holder.Children.Add(fallback);
        holder.Children.Add(image);
        holder.Loaded += async (_, _) =>
        {
            if (image.Source != null) return;
            var source = await Icons.GetAsync(id, name);
            if (source == null) return;
            image.Source = source;
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        };
        return holder;
    }

    private Button Action(string label, string verb, string id, string name, bool accent)
    {
        var button = new Button { Content = label, MinWidth = 90 };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accentStyle) button.Style = accentStyle;
        button.Click += async (_, _) => await DoAsync(verb, id, name);
        return button;
    }
}
