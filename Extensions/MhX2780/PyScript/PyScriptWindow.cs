using System.Text;
using System.Text.RegularExpressions;
using ASD.Extensibility;
using AsdPython;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PyScript;

/// <summary>
/// The editor window: toolbar on top, the code with a suggestions list beside it, and tabs below (Output, Problems, Logs, AI analysis).
/// Everything is built in code. Python runs as a separate process (see Py in PythonTools.cs).
/// </summary>
internal sealed class PyScriptWindow : Window
{
    // the syntax check: reads the code from stdin; prints OK or line:column:message
    private const string CheckScript = """
        import sys, ast
        try:
            ast.parse(sys.stdin.read())
            print('OK')
        except SyntaxError as e:
            print('%s:%s:%s' % (e.lineno, e.offset, e.msg))
        """;

    private const string Template = "def main():\r    print('Hello from PyScript')\r\r\rif __name__ == '__main__':\r    main()\r";

    private static PyScriptWindow? _open;

    public static void ShowWindow(IAsdHost host)
    {
        _open ??= new PyScriptWindow(host);
        _open.Activate();
    }

    private readonly string _folder;
    private readonly TitleBar _bar;
    private readonly Grid _root = new();
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas"), FontSize = 14 };
    private readonly TextBox _stdin = new() { Header = "Input for input() (one line for each call)", AcceptsReturn = true, Height = 72, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas") };
    private readonly OutputView _output = new();
    private readonly OutputView _logs = new();
    private readonly ListView _problems = new() { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
    private readonly ListView _suggestions = new() { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
    private readonly TextBox _aiText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsSpellCheckEnabled = false, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly ProgressRing _aiRing = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _suggestHeader = new() { Text = "Suggestions", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _pos = new() { Opacity = 0.7, FontSize = 12 };
    private readonly TextBlock _python = new() { Opacity = 0.7, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Pivot _tabs = new();
    private readonly PivotItem _outputItem = new() { Header = "Output" };
    private readonly PivotItem _problemsItem = new() { Header = "Problems" };
    private readonly PivotItem _logsItem = new() { Header = "Logs" };
    private readonly PivotItem _aiItem = new() { Header = "AI analysis" };
    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Dictionary<string, string[]> _members = new();
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly Button _aiButton;

    private string? _path;          // the file being edited (null = the scratch file)
    private bool _dirty;
    private bool _loading;
    private bool _closing;
    private bool _busy;
    private bool _flakesMissing;
    private bool _flakesHinted;
    private string _lastProblems = "";
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _aiCts;

    private PyScriptWindow(IAsdHost host)
    {
        _folder = host.DataFolder;
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* an older Windows: plain background */ }
        _bar = new TitleBar { Title = "PyScript", Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_bar);
        try { AppWindow.Resize(new SizeInt32(1240, 820)); } catch { }

        // toolbar
        _runButton = Tool("", "Run (F5)", () => _ = RunAsync(), accent: true);
        _stopButton = Tool("", "Stop", () => _runCts?.Cancel());
        _stopButton.IsEnabled = false;
        _aiButton = Tool("", "Analyze with AI", () => _ = AnalyzeAsync());
        var settings = Tool("", "AI settings", () => { });
        settings.Flyout = BuildSettingsFlyout();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 12, 8) };
        foreach (var item in new UIElement[]
        {
            Tool("", "New", () => _ = NewFileAsync()),
            Tool("", "Open", () => _ = OpenFileAsync()),
            Tool("", "Save", () => _ = SaveAsync(false)),
            Tool("", "Save as", () => _ = SaveAsync(true)),
            Separator(),
            _runButton, _stopButton,
            Tool("", "Check", () => _ = CheckAsync(true)),
            Separator(),
            _aiButton, settings,
            Separator(),
            Tool("", "Clear output", () => _output.Clear()),
        }) toolbar.Children.Add(item);

        // code + suggestions
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        _editor.Margin = new Thickness(12, 0, 6, 0);
        var side = new Grid { Margin = new Thickness(6, 0, 12, 0) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_suggestions, 1);
        side.Children.Add(_suggestHeader);
        side.Children.Add(_suggestions);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        Grid.SetColumn(side, 1);
        top.Children.Add(_editor);
        top.Children.Add(side);

        // tabs
        var outputGrid = new Grid { RowSpacing = 8 };
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_output, 1);
        outputGrid.Children.Add(_stdin);
        outputGrid.Children.Add(_output);
        _outputItem.Content = outputGrid;
        _problemsItem.Content = _problems;
        _logsItem.Content = _logs;
        var aiGrid = new Grid { RowSpacing = 8 };
        aiGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        aiGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var aiHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _aiRing, new TextBlock { Text = "Claude reads your code, the problems and the last output.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center } } };
        Grid.SetRow(_aiText, 1);
        aiGrid.Children.Add(aiHead);
        aiGrid.Children.Add(_aiText);
        _aiItem.Content = aiGrid;
        foreach (var item in new[] { _outputItem, _problemsItem, _logsItem, _aiItem }) _tabs.Items.Add(item);
        _tabs.Margin = new Thickness(12, 4, 12, 0);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        Grid.SetRow(_tabs, 1);
        main.Children.Add(top);
        main.Children.Add(_tabs);

        var status = new Grid { Margin = new Thickness(14, 4, 14, 6) };
        status.Children.Add(_pos);
        status.Children.Add(_python);

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(main, 2);
        Grid.SetRow(status, 3);
        _root.Children.Add(_bar);
        _root.Children.Add(toolbar);
        _root.Children.Add(main);
        _root.Children.Add(status);
        Content = _root;

        // keyboard
        Accelerate(Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None, () => _ = RunAsync());
        Accelerate(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, () => _ = SaveAsync(false));
        Accelerate(Windows.System.VirtualKey.O, Windows.System.VirtualKeyModifiers.Control, () => _ = OpenFileAsync());
        Accelerate(Windows.System.VirtualKey.N, Windows.System.VirtualKeyModifiers.Control, () => _ = NewFileAsync());
        Accelerate(Windows.System.VirtualKey.Space, Windows.System.VirtualKeyModifiers.Control, AcceptFirstSuggestion);
        _editor.PreviewKeyDown += OnEditorKey;

        // events
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            SetDirty(true);
            _checkTimer.Stop();
            _checkTimer.Start();
        };
        _editor.SelectionChanged += (_, _) =>
        {
            UpdatePosition();
            _suggestTimer.Stop();
            _suggestTimer.Start();
        };
        _checkTimer.Tick += (_, _) => { _checkTimer.Stop(); _ = CheckAsync(false); };
        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); _ = RefreshSuggestionsAsync(); };
        _suggestions.ItemClick += (_, e) => { if (e.ClickedItem is Suggestion s) Insert(s); };
        _problems.ItemClick += (_, e) => { if (e.ClickedItem is Problem p) GoToLine(p.Line); };

        AppWindow.Closing += async (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            if (_path == null) SaveScratch();
            if (await ConfirmAsync()) { _closing = true; Close(); }
        };
        Closed += (_, _) =>
        {
            _open = null;
            _checkTimer.Stop();
            _suggestTimer.Stop();
            _runCts?.Cancel();
            _aiCts?.Cancel();
        };

        _ = StartAsync();
    }

    // ───────── small helpers ─────────

    private static Button Tool(string glyph, string text, Action click, bool accent = false)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } },
            },
        };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style st) button.Style = st;
        button.Click += (_, _) => click();
        return button;
    }

    private static UIElement Separator() => new Border
    {
        Width = 1,
        Margin = new Thickness(4, 4, 4, 4),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.5 },
    };

    private void Accelerate(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        _root.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>The code with normal line ends (a TextBox keeps '\r').</summary>
    private string Normal => _editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _logs.Add(line);
        try { File.AppendAllText(Path.Combine(_folder, "pyscript.log"), line + "\n"); } catch { }
    }

    private void Show(PivotItem item) => _tabs.SelectedItem = item;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _runButton.IsEnabled = !busy;
        _stopButton.IsEnabled = busy;
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        var name = _path != null ? Path.GetFileName(_path) : "Untitled";
        _bar.Title = name + (dirty ? " •" : "") + " — PyScript";
        Title = _bar.Title;
    }

    private void UpdatePosition()
    {
        var text = _editor.Text;
        var caret = Math.Min(_editor.SelectionStart, text.Length);
        var before = text[..caret];
        var line = before.Count(c => c == '\r') + 1;
        var column = caret - (before.LastIndexOf('\r') + 1) + 1;
        _pos.Text = $"Line {line}, column {column}";
    }

    private async Task StartAsync()
    {
        try { File.WriteAllText(Path.Combine(_folder, "syntax_check.py"), CheckScript); } catch { }
        var scratch = Path.Combine(_folder, "scratch.py");
        _loading = true;
        _editor.Text = File.Exists(scratch) ? File.ReadAllText(scratch) : Template;
        _loading = false;
        SetDirty(false);
        UpdatePosition();
        Log("PyScript opened");

        var py = await Task.Run(Py.Find);
        _python.Text = py != null ? py.Version : "Python 3 was not found";
        if (py == null) Log("Python 3 was not found. Install it from python.org (tick 'Add python.exe to PATH'), then open PyScript again.");
        else await CheckAsync(false);
    }

    private void SaveScratch()
    {
        try { File.WriteAllText(Path.Combine(_folder, "scratch.py"), Normal, new UTF8Encoding(false)); } catch { }
    }

    // ───────── files ─────────

    private async Task<bool> ConfirmAsync()
    {
        if (!_dirty || _path == null) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = "Save changes?",
            Content = new TextBlock { Text = Path.GetFileName(_path) + " has changes that are not saved.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) return await SaveAsync(false);
        return result == ContentDialogResult.Secondary;
    }

    private void LoadText(string text, string? path)
    {
        _loading = true;
        _editor.Text = text;
        _loading = false;
        _path = path;
        SetDirty(false);
        _problems.ItemsSource = null;
        _problemsItem.Header = "Problems";
        UpdatePosition();
        _ = CheckAsync(false);
    }

    private async Task NewFileAsync()
    {
        if (!await ConfirmAsync()) return;
        if (_path == null) SaveScratch();
        LoadText(Template, null);
        Log("New file");
    }

    private async Task OpenFileAsync()
    {
        var path = await FilePick.OpenAsync(".py");
        if (path == null || !await ConfirmAsync()) return;
        try
        {
            LoadText(File.ReadAllText(path), path);
            Log("Opened " + path);
        }
        catch (Exception ex) { Log("Could not open the file: " + ex.Message); }
    }

    private async Task<bool> SaveAsync(bool asNew)
    {
        var path = _path;
        if (path == null || asNew)
        {
            path = await FilePick.SaveAsync(_path != null ? Path.GetFileNameWithoutExtension(_path) : "script", "Python file", ".py");
            if (path == null) return false;
        }
        try { File.WriteAllText(path, Normal, new UTF8Encoding(false)); }
        catch (Exception ex) { Log("Could not save: " + ex.Message); return false; }
        _path = path;
        SetDirty(false);
        Log("Saved " + path);
        return true;
    }

    // ───────── editing: Tab, Enter (keeps the indent), suggestions ─────────

    private void Type(string text)
    {
        var at = _editor.SelectionStart;
        _editor.SelectedText = text;
        _editor.Select(at + text.Length, 0);
    }

    private string CurrentLine()
    {
        var text = _editor.Text;
        var caret = Math.Min(_editor.SelectionStart, text.Length);
        var before = text[..caret];
        return before[(before.LastIndexOf('\r') + 1)..];
    }

    private static string IndentOf(string line) => new(line.TakeWhile(c => c == ' ' || c == '\t').ToArray());

    private void OnEditorKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            e.Handled = true;
            Type("    ");
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;   // a new line starts with the indent of this one, and 4 more after a colon
            var line = CurrentLine();
            Type("\r" + IndentOf(line) + (line.TrimEnd().EndsWith(':') ? "    " : ""));
        }
    }

    private void Insert(Suggestion s)
    {
        var caret = _editor.SelectionStart;
        var (prefix, _) = Complete.Context(_editor.Text, caret);
        var start = caret - prefix.Length;
        var text = s.Kind == "snippet" ? s.Insert.Replace("\r", "\r" + IndentOf(CurrentLine())) : s.Insert;
        _editor.Select(start, prefix.Length);
        _editor.SelectedText = text;
        _editor.Select(start + text.Length, 0);
        _editor.Focus(FocusState.Programmatic);
    }

    private void AcceptFirstSuggestion()
    {
        if (_suggestions.ItemsSource is List<Suggestion> { Count: > 0 } list) Insert(list[0]);
    }

    /// <summary>Names inside a module that the file imports (asked from Python once, then remembered).</summary>
    private async Task<string[]> MembersAsync(string owner, string text)
    {
        if (_members.TryGetValue(owner, out var known)) return known;
        if (!Regex.IsMatch(text, @"^\s*(?:import|from)\s[^\r\n]*\b" + owner + @"\b", RegexOptions.Multiline)) return Array.Empty<string>();   // only modules the file imports

        var names = await Task.Run(() =>
        {
            var py = Py.Find();
            if (py == null) return Array.Empty<string>();
            var script = "import " + owner + " as m; print(' '.join(n for n in dir(m) if not n.startswith('_')))";
            var (code, output) = Py.RunQuick(py.File, py.Prefix.Concat(new[] { "-c", script }), 8000);
            return code == 0 ? output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        });
        _members[owner] = names;
        return names;
    }

    private async Task RefreshSuggestionsAsync()
    {
        var text = _editor.Text;
        var caret = _editor.SelectionStart;
        var (prefix, owner) = Complete.Context(text, caret);
        List<Suggestion> items;
        if (owner != null)
        {
            var names = await MembersAsync(owner, text);
            if (_editor.SelectionStart != caret) return;   // the person moved on
            items = names
                .Where(n => prefix.Length == 0 || n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(60).Select(n => new Suggestion(n, n, owner)).ToList();
        }
        else items = Complete.Local(text, prefix);
        _suggestions.ItemsSource = items;
        _suggestHeader.Text = items.Count == 0 ? "Suggestions" : $"Suggestions ({items.Count})";
    }

    private void GoToLine(int line)
    {
        var text = _editor.Text;
        var index = 0;
        for (var i = 1; i < line; i++)
        {
            index = text.IndexOf('\r', index);
            if (index < 0) return;
            index++;
        }
        _editor.Focus(FocusState.Programmatic);
        _editor.Select(index, 0);
    }

    // ───────── problems ─────────

    private List<Problem> FindProblems(PyInfo py, string code)
    {
        var list = new List<Problem>();
        var script = Path.Combine(_folder, "syntax_check.py");
        var (_, text) = Py.RunQuick(py.File, py.Prefix.Append(script), 10000, code);
        var line = text.Trim();
        if (line != "OK")
        {
            var parts = line.Split(':', 3);
            if (parts.Length == 3 && int.TryParse(parts[0], out var number))
            {
                _ = int.TryParse(parts[1], out var column);
                list.Add(new Problem(number, column, parts[2].Trim(), "syntax"));
            }
            else if (line.Length > 0) list.Add(new Problem(1, 0, line, "check"));
            return list;
        }

        if (_flakesMissing) return list;
        var (_, flakes) = Py.RunQuick(py.File, py.Prefix.Concat(new[] { "-m", "pyflakes" }), 15000, code);
        if (flakes.Contains("No module named pyflakes")) { _flakesMissing = true; return list; }
        foreach (var raw in flakes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(raw, @"^<stdin>:(\d+):(?:(\d+):?)?\s*(.*)$");
            if (!m.Success) continue;
            list.Add(new Problem(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0, m.Groups[3].Value, "pyflakes"));
        }
        return list;
    }

    private async Task CheckAsync(bool announce)
    {
        var py = await Task.Run(Py.Find);
        if (py == null) return;
        var code = Normal;
        var found = await Task.Run(() => FindProblems(py, code));
        if (Normal != code) return;   // changed meanwhile: a newer check is on its way

        _problems.ItemsSource = found;
        _problemsItem.Header = found.Count == 0 ? "Problems" : $"Problems ({found.Count})";
        _lastProblems = string.Join("\n", found.Select(p => p.ToString()));
        if (_flakesMissing && !_flakesHinted)
        {
            _flakesHinted = true;
            Log("pyflakes is not installed, so only syntax errors are reported. For more checks run: pip install pyflakes");
        }
        if (announce)
        {
            Log(found.Count == 0 ? "Check: no problems found" : $"Check: {found.Count} problem(s)");
            if (found.Count > 0) Show(_problemsItem);
        }
    }

    // ───────── run ─────────

    private async Task RunAsync()
    {
        if (_busy) return;
        var py = await Task.Run(Py.Find);
        if (py == null) { Log("Python 3 was not found. Install it from python.org (tick 'Add python.exe to PATH')."); return; }

        string file;
        try
        {
            if (_path != null) { File.WriteAllText(_path, Normal, new UTF8Encoding(false)); SetDirty(false); file = _path; }
            else { file = Path.Combine(_folder, "scratch.py"); File.WriteAllText(file, Normal, new UTF8Encoding(false)); }
        }
        catch (Exception ex) { Log("Could not save the file before running: " + ex.Message); return; }

        _output.Clear();
        Show(_outputItem);
        _runCts = new CancellationTokenSource();
        SetBusy(true);
        Log("Run: " + file);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int code;
        try
        {
            code = await Py.RunAsync(py.File, py.Prefix.Concat(new[] { "-u", file }), Path.GetDirectoryName(file), _stdin.Text,
                (isError, line) => _output.AddFromAnyThread(line, isError), _runCts.Token, 300);
        }
        catch (Exception ex)
        {
            _output.Add(ex.Message, true);
            code = -1;
        }
        SetBusy(false);
        var summary = code switch
        {
            0 => "Finished",
            -2 => "Stopped",
            -3 => "Stopped: it ran for more than 5 minutes",
            _ => "Exited with code " + code,
        };
        Log($"{summary} ({clock.Elapsed.TotalSeconds:0.0} s)");
        if (code != 0 && code != -2) _ = CheckAsync(false);
    }

    // ───────── AI analysis ─────────

    private Flyout BuildSettingsFlyout()
    {
        var key = new PasswordBox { Header = "API key", PlaceholderText = "paste a new key (empty = keep the saved one)", Width = 360 };
        var model = new TextBox { Header = "Model", Text = Ai.LoadModel(_folder), Width = 360 };
        var info = new TextBlock
        {
            Text = "Get a key at console.anthropic.com. It is stored encrypted for your Windows account and is only sent to api.anthropic.com.",
            TextWrapping = TextWrapping.Wrap, Width = 360, Opacity = 0.7, FontSize = 12,
        };
        var save = new Button { Content = "Save" };
        var remove = new Button { Content = "Remove saved key" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, remove } };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { key, model, info, buttons } } };
        save.Click += async (_, _) =>
        {
            if (key.Password.Length > 0) await Ai.SaveKeyAsync(_folder, key.Password.Trim());
            Ai.SaveModel(_folder, model.Text);
            key.Password = "";
            flyout.Hide();
            Log("AI settings saved");
        };
        remove.Click += async (_, _) =>
        {
            await Ai.SaveKeyAsync(_folder, "");
            flyout.Hide();
            Log("The saved API key was removed");
        };
        return flyout;
    }

    private async Task AnalyzeAsync()
    {
        Show(_aiItem);
        var key = await Ai.LoadKeyAsync(_folder);
        if (string.IsNullOrEmpty(key))
        {
            _aiText.Text = "Add your API key first: press 'AI settings'. The key is stored encrypted for your Windows account.";
            return;
        }
        var code = Normal;
        if (code.Trim().Length == 0) { _aiText.Text = "There is no code to analyze."; return; }

        _aiCts?.Cancel();
        var cts = _aiCts = new CancellationTokenSource();
        _aiText.Text = "";
        _aiButton.IsEnabled = false;
        _aiRing.Visibility = Visibility.Visible;
        _aiRing.IsActive = true;
        var model = Ai.LoadModel(_folder);
        Log("AI analysis started (" + model + ")");
        try
        {
            _aiText.Text = await Ai.AnalyzeAsync(key, model, code, _output.Text, _lastProblems, cts.Token);
            Log("AI analysis finished");
        }
        catch (OperationCanceledException) { Log("AI analysis cancelled"); }
        catch (Exception ex)
        {
            _aiText.Text = "The analysis failed: " + ex.Message;
            Log("AI analysis failed: " + ex.Message);
        }
        finally
        {
            _aiRing.IsActive = false;
            _aiRing.Visibility = Visibility.Collapsed;
            _aiButton.IsEnabled = true;
        }
    }
}
