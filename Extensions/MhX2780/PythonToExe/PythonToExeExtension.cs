using ASD.Extensibility;
using AsdPython;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PythonToExe;

public sealed class PythonToExeExtension : IAsdExtension
{
    public string Id => "MhX2780.PythonToExe";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "pytoexe", Title = "Python to EXE", Glyph = "", Create = () => new ExeView(host.DataFolder) });
    }
}

/// <summary>Pick a .py file, set a few options, press Build: PyInstaller makes the exe. The log shows what PyInstaller says.</summary>
internal sealed class ExeView : StackPanel
{
    private readonly string _folder;
    private readonly TextBox _script = new() { Header = "Python file (.py)", PlaceholderText = "Choose a .py file" };
    private readonly TextBox _name = new() { Header = "Name of the exe", PlaceholderText = "default: the name of the file" };
    private readonly TextBox _icon = new() { Header = "Icon (.ico, optional)" };
    private readonly TextBox _out = new() { Header = "Output folder", PlaceholderText = "default: a dist folder next to the .py file" };
    private readonly TextBox _extra = new() { Header = "Extra PyInstaller options (optional)", PlaceholderText = "for example: --hidden-import=requests" };
    private readonly CheckBox _one = new() { Content = "One file (--onefile)", IsChecked = true };
    private readonly CheckBox _windowed = new() { Content = "No console window (--windowed)" };
    private readonly CheckBox _clean = new() { Content = "Clean the cache first (--clean)" };
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly Button _browseIcon = new() { Content = "Browse…" };
    private readonly Button _build = new() { Content = "Build exe" };
    private readonly Button _cancel = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _install = new() { Content = "Install PyInstaller", Visibility = Visibility.Collapsed };
    private readonly Button _open = new() { Content = "Show in folder", IsEnabled = false };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly OutputView _log = new() { Height = 240 };
    private CancellationTokenSource? _cts;
    private string? _exe;

    public ExeView(string folder)
    {
        _folder = folder;
        Spacing = 12;
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) _build.Style = accent;

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _one, _windowed, _clean } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _build, _cancel, _install, _open } };
        Children.Add(Line(_script, _browse));
        Children.Add(_name);
        Children.Add(Line(_icon, _browseIcon));
        Children.Add(_out);
        Children.Add(options);
        Children.Add(_extra);
        Children.Add(buttons);
        Children.Add(_status);
        Children.Add(_log);

        _browse.Click += async (_, _) =>
        {
            var path = await FilePick.OpenAsync(".py");
            if (path == null) return;
            _script.Text = path;
            if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = Path.GetFileNameWithoutExtension(path);
        };
        _browseIcon.Click += async (_, _) =>
        {
            var path = await FilePick.OpenAsync(".ico");
            if (path != null) _icon.Text = path;
        };
        _build.Click += async (_, _) => await BuildAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _install.Click += async (_, _) =>
        {
            var py = await Task.Run(Py.Find);
            if (py == null) return;
            var code = await RunPyAsync(py, new[] { "-m", "pip", "install", "--upgrade", "pyinstaller" }, _folder, "Installing PyInstaller…");
            await RefreshAsync();
            if (code != 0) _status.Text = "PyInstaller could not be installed (see the log).";
        };
        _open.Click += (_, _) =>
        {
            if (_exe == null) return;
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select," + _exe);
            System.Diagnostics.Process.Start(start);
        };
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static Grid Line(TextBox box, Button button)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        button.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(button, 1);
        grid.Children.Add(box);
        grid.Children.Add(button);
        return grid;
    }

    private static bool HasPyInstaller(PyInfo py, out string version)
    {
        var (code, text) = Py.RunQuick(py.File, py.Prefix.Concat(new[] { "-m", "PyInstaller", "--version" }), 40000);
        version = text.Trim();
        return code == 0;
    }

    /// <summary>Looks for Python and PyInstaller and says what is missing.</summary>
    private async Task RefreshAsync()
    {
        var py = await Task.Run(Py.Find);
        if (py == null)
        {
            _status.Text = "Python 3 was not found. Install it from python.org (tick 'Add python.exe to PATH'), then open this page again.";
            _build.IsEnabled = false;
            return;
        }
        var has = await Task.Run(() => (HasPyInstaller(py, out var v), v));
        _install.Visibility = has.Item1 ? Visibility.Collapsed : Visibility.Visible;
        _build.IsEnabled = has.Item1;
        _status.Text = has.Item1 ? py.Version + "  ·  PyInstaller " + has.v : py.Version + "  ·  PyInstaller is not installed: press 'Install PyInstaller'.";
    }

    private async Task<int> RunPyAsync(PyInfo py, IEnumerable<string> args, string workDir, string busy)
    {
        _log.Clear();
        _cts = new CancellationTokenSource();
        _build.IsEnabled = _install.IsEnabled = false;
        _cancel.IsEnabled = true;
        _status.Text = busy;
        int code;
        try
        {
            code = await Py.RunAsync(py.File, py.Prefix.Concat(args), workDir, "", (_, line) => _log.AddFromAnyThread(line), _cts.Token);
        }
        catch (Exception ex)
        {
            _log.Add(ex.Message, true);
            code = -1;
        }
        _build.IsEnabled = _install.IsEnabled = true;
        _cancel.IsEnabled = false;
        return code;
    }

    private async Task BuildAsync()
    {
        var script = _script.Text.Trim().Trim('"');
        if (!File.Exists(script)) { _status.Text = "Choose a .py file first."; return; }
        var py = await Task.Run(Py.Find);
        if (py == null) { _status.Text = "Python 3 was not found."; return; }

        var name = string.IsNullOrWhiteSpace(_name.Text) ? Path.GetFileNameWithoutExtension(script) : _name.Text.Trim();
        var dist = string.IsNullOrWhiteSpace(_out.Text) ? Path.Combine(Path.GetDirectoryName(script)!, "dist") : _out.Text.Trim().Trim('"');
        var args = new List<string>
        {
            "-m", "PyInstaller", "--noconfirm", "--name", name, "--distpath", dist,
            "--workpath", Path.Combine(_folder, "build"), "--specpath", Path.Combine(_folder, "spec"),
        };
        if (_one.IsChecked == true) args.Add("--onefile");
        if (_windowed.IsChecked == true) args.Add("--windowed");
        if (_clean.IsChecked == true) args.Add("--clean");
        var icon = _icon.Text.Trim().Trim('"');
        if (icon.Length > 0) { args.Add("--icon"); args.Add(icon); }
        args.AddRange(_extra.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.Add(script);

        _open.IsEnabled = false;
        var code = await RunPyAsync(py, args, Path.GetDirectoryName(script)!, "Building… this can take a minute or two.");
        if (code == 0)
        {
            _exe = _one.IsChecked == true ? Path.Combine(dist, name + ".exe") : Path.Combine(dist, name, name + ".exe");
            var found = File.Exists(_exe);
            _open.IsEnabled = found;
            _status.Text = found ? "Done: " + _exe : "PyInstaller finished, but the exe was not found in " + dist;
        }
        else _status.Text = code == -2 ? "Stopped." : "The build failed (see the log). Exit code " + code;
    }
}
