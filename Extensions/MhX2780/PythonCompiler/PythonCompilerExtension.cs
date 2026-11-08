using System.Text;
using ASD.Extensibility;
using AsdPython;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PythonCompiler;

public sealed class PythonCompilerExtension : IAsdExtension
{
    public string Id => "MhX2780.PythonCompiler";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "pycompiler", Title = "Python Compiler", Glyph = "", Create = () => new CompilerView(host.DataFolder) });
    }
}

/// <summary>Code box, input box, Run / Check / Stop, and the output. The code is kept between runs in the extension's data folder.</summary>
internal sealed class CompilerView : StackPanel
{
    private readonly string _folder;
    private readonly TextBox _code = new() { Header = "Python code", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 300, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas") };
    private readonly TextBox _input = new() { Header = "Input for input() (one line for each call)", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 76, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas") };
    private readonly OutputView _output = new() { Height = 220 };
    private readonly Button _run = new() { Content = "Run" };
    private readonly Button _check = new() { Content = "Check syntax" };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _clear = new() { Content = "Clear output" };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _cts;

    public CompilerView(string folder)
    {
        _folder = folder;
        Spacing = 12;
        var saved = Path.Combine(folder, "last.py");
        _code.Text = File.Exists(saved) ? File.ReadAllText(saved) : "print('Hello from ASD')";
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) _run.Style = accent;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _run, _check, _stop, _clear } };
        Children.Add(_code);
        Children.Add(_input);
        Children.Add(buttons);
        Children.Add(_status);
        Children.Add(_output);

        _code.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Tab) return;
            e.Handled = true;   // Tab writes 4 spaces instead of leaving the box
            var at = _code.SelectionStart;
            _code.SelectedText = "    ";
            _code.SelectionStart = at + 4;
            _code.SelectionLength = 0;
        };
        _run.Click += async (_, _) => await ExecuteAsync(false);
        _check.Click += async (_, _) => await ExecuteAsync(true);
        _stop.Click += (_, _) => _cts?.Cancel();
        _clear.Click += (_, _) => _output.Clear();
        Loaded += async (_, _) => await ShowPythonAsync();
        Unloaded += (_, _) => Save();
    }

    private string Normal => _code.Text.Replace("\r\n", "\n").Replace('\r', '\n');

    private void Save()
    {
        try { File.WriteAllText(Path.Combine(_folder, "last.py"), Normal, new UTF8Encoding(false)); } catch { }
    }

    private async Task ShowPythonAsync()
    {
        var py = await Task.Run(Py.Find);
        _status.Text = py != null
            ? py.Version + " found (" + py.Command + ")"
            : "Python 3 was not found. Install it from python.org (tick 'Add python.exe to PATH'), then open this page again.";
        _run.IsEnabled = _check.IsEnabled = py != null;
    }

    private async Task ExecuteAsync(bool checkOnly)
    {
        var py = await Task.Run(Py.Find);
        if (py == null) return;
        Save();
        var file = Path.Combine(_folder, "script.py");
        File.WriteAllText(file, Normal, new UTF8Encoding(false));

        var args = new List<string>(py.Prefix);
        if (checkOnly) args.AddRange(new[] { "-m", "py_compile", file });
        else args.AddRange(new[] { "-u", file });

        _output.Clear();
        _cts = new CancellationTokenSource();
        _run.IsEnabled = _check.IsEnabled = false;
        _stop.IsEnabled = true;
        _status.Text = checkOnly ? "Checking…" : "Running…";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int code;
        try
        {
            code = await Py.RunAsync(py.File, args, _folder, checkOnly ? "" : _input.Text, (isError, line) => _output.AddFromAnyThread(line, isError), _cts.Token, 60);
        }
        catch (Exception ex)
        {
            _output.Add(ex.Message, true);
            code = -1;
        }
        _stop.IsEnabled = false;
        _run.IsEnabled = _check.IsEnabled = true;
        var seconds = clock.Elapsed.TotalSeconds;
        _status.Text = code switch
        {
            0 => (checkOnly ? "No syntax errors" : "Finished") + $" in {seconds:0.0} s",
            -2 => "Stopped",
            -3 => "Stopped: it ran for more than 60 seconds",
            _ => $"Exited with code {code} after {seconds:0.0} s",
        };
    }
}
