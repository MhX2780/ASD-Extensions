using System.Text;
using System.Text.RegularExpressions;
using ASD.Extensibility;
using AsdPython;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace PyScript;

/// <summary>
/// The editor window: toolbar on top, the code in an editor card (line numbers, a suggestion bubble under the caret, the AI's faded
/// continuation) and tabs below (Output, Problems, Logs, AI analysis). Everything is built in code.
/// Python runs as a separate process (see Py in PythonTools.cs).
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

    // the editor card
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas"), FontSize = 14 };
    private readonly TextBlock _gutter = new() { FontFamily = new FontFamily("Consolas"), FontSize = 14, TextAlignment = TextAlignment.Right, Opacity = 0.4, Margin = new Thickness(12, 8, 8, 40), Text = "1" };
    private readonly ScrollViewer _gutterScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsHitTestVisible = false };
    private readonly Canvas _overlay = new();
    private readonly TextBlock _ghost1 = new() { FontFamily = new FontFamily("Consolas"), FontSize = 14, Opacity = 0.45, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _ghost2 = new() { FontFamily = new FontFamily("Consolas"), FontSize = 14, Opacity = 0.45, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _bubble = new() { Visibility = Visibility.Collapsed };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsTabStop = false, MaxHeight = 240, MinWidth = 290, MaxWidth = 520 };
    private ScrollViewer? _editorScroll;

    // the colours: a see-through copy of the code, with only the strings, comments, keywords... coloured, lies over the editor text
    private readonly TextBlock _colorText = new() { FontFamily = new FontFamily("Consolas"), FontSize = 14, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 400, 400) };
    private readonly ScrollViewer _colorScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, IsHitTestVisible = false };
    private readonly Dictionary<Tok, SolidColorBrush> _brushes = new();
    private string _prevText = "";   // the code before the last change (to see that exactly one character was typed)

    // the tabs
    private readonly TextBox _stdin = new() { PlaceholderText = "Input for input(): one line for each call", AcceptsReturn = true, Height = 64, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas") };
    private readonly OutputView _output = new();
    private readonly OutputView _logs = new();
    private readonly OutputView _aiOut = new();
    private readonly ListView _problems = new() { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
    private readonly ProgressRing _aiRing = new() { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pos = new() { Opacity = 0.7, FontSize = 12 };
    private readonly TextBlock _python = new() { Opacity = 0.7, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Pivot _tabs = new();
    private readonly PivotItem _outputItem = new() { Header = "Output" };
    private readonly PivotItem _problemsItem = new() { Header = "Problems" };
    private readonly PivotItem _logsItem = new() { Header = "Logs" };
    private readonly PivotItem _aiItem = new() { Header = "AI analysis" };

    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _aiTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    private readonly Dictionary<string, string[]> _members = new();
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly Button _aiButton;
    private readonly ToggleButton _aiComplete;

    private string? _path;          // the file being edited (null = the scratch file)
    private bool _dirty;
    private bool _loading;
    private bool _closing;
    private bool _busy;
    private bool _flakesMissing;
    private bool _flakesHinted;
    private bool _ghostErrorLogged;
    private int _gutterLines = 1;
    private int _bubbleStart;
    private int _bubbleLength;
    private string _ghostText = "";   // what the AI would add at the caret ('\r' for new lines), empty = nothing shown
    private string _lastProblems = "";
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _aiCts;
    private CancellationTokenSource? _ghostCts;

    private PyScriptWindow(IAsdHost host)
    {
        _folder = host.DataFolder;
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* an older Windows: plain background */ }
        _bar = new TitleBar { Title = "PyScript", Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_bar);
        try { AppWindow.Resize(new SizeInt32(1240, 820)); } catch { }

        var settings = Ai.Load(_folder);

        // toolbar
        _runButton = Tool("", "Run (F5)", () => _ = RunAsync(), accent: true);
        _stopButton = Tool("", "Stop", () => _runCts?.Cancel());
        _stopButton.IsEnabled = false;
        _aiButton = Tool("", "Analyze with AI", () => _ = AnalyzeAsync());
        _aiComplete = new ToggleButton { IsChecked = settings.Inline, Content = "✦ AI complete" };
        ToolTipService.SetToolTip(_aiComplete, "The AI continues your code in faded text while you type. Shift+Tab accepts it.");
        _aiComplete.Click += (_, _) =>
        {
            var s = Ai.Load(_folder);
            s.Inline = _aiComplete.IsChecked == true;
            Ai.Save(_folder, s);
            ClearGhost();
            if (s.Inline) { _aiTimer.Stop(); _aiTimer.Start(); }
        };
        var aiSettings = Tool("", "AI settings", () => { });
        aiSettings.Flyout = BuildSettingsFlyout();
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
            _aiButton, _aiComplete, aiSettings,
            Separator(),
            Tool("", "Clear output", () => _output.Clear()),
        }) toolbar.Children.Add(item);

        // the editor card: line numbers | the code | a layer on top for the suggestion bubble and the AI's faded text
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        var clear = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        foreach (var name in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _editor.Resources[name] = clear;
        _editor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _editor.Background = clear;
        _editor.BorderThickness = new Thickness(0);
        _editor.Padding = new Thickness(8, 8, 8, 8);
        _colorText.Foreground = clear;   // only the coloured parts are drawn; the rest of the copy is see-through
        _colorScroll.Padding = _editor.Padding;
        _colorScroll.Content = _colorText;
        _gutterScroll.Content = _gutter;
        _gutterScroll.Width = 56;

        Brush bubbleBackground;
        try { bubbleBackground = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]; }
        catch { bubbleBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 45, 45)); }
        _bubble.Child = _list;
        _bubble.Background = bubbleBackground;
        _bubble.CornerRadius = new CornerRadius(8);
        _bubble.BorderThickness = new Thickness(1);
        _bubble.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.5 };
        _bubble.Padding = new Thickness(4);
        _overlay.Children.Add(_ghost1);
        _overlay.Children.Add(_ghost2);
        _overlay.Children.Add(_bubble);

        var codeGrid = new Grid();
        codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_editor, 1);
        Grid.SetColumn(_colorScroll, 1);
        Grid.SetColumn(_overlay, 1);
        codeGrid.Children.Add(_gutterScroll);
        codeGrid.Children.Add(_editor);
        codeGrid.Children.Add(_colorScroll);
        codeGrid.Children.Add(_overlay);
        var card = new Border
        {
            Margin = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.35 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.08 },
            Child = codeGrid,
        };

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
        var aiHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _aiRing, new TextBlock { Text = "The AI reads your code, the problems and the last output.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center } } };
        Grid.SetRow(_aiOut, 1);
        aiGrid.Children.Add(aiHead);
        aiGrid.Children.Add(_aiOut);
        _aiItem.Content = aiGrid;
        foreach (var item in new[] { _outputItem, _problemsItem, _logsItem, _aiItem }) _tabs.Items.Add(item);
        _tabs.Margin = new Thickness(12, 4, 12, 0);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        Grid.SetRow(_tabs, 1);
        main.Children.Add(card);
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
        Accelerate(Windows.System.VirtualKey.Space, Windows.System.VirtualKeyModifiers.Control, () => _ = RefreshSuggestionsAsync(true));
        _editor.PreviewKeyDown += OnEditorKey;

        // events
        _root.ActualThemeChanged += (_, _) => ApplyTheme();
        _editor.Loaded += (_, _) =>
        {
            _editor.ApplyTemplate();
            ApplyTheme();
            _editorScroll = FindScroll(_editor);
            if (_editorScroll != null) _editorScroll.ViewChanged += OnEditorScrolled;
        };
        _editor.TextChanged += (_, _) =>
        {
            UpdateGutter();
            if (!_loading) AutoClose();
            _prevText = _editor.Text;
            Recolor();
            if (_loading) return;
            SetDirty(true);
            _checkTimer.Stop();
            _checkTimer.Start();
        };
        _editor.SelectionChanged += (_, _) =>
        {
            UpdatePosition();
            ClearGhost();
            _suggestTimer.Stop();
            _suggestTimer.Start();
            _aiTimer.Stop();
            _aiTimer.Start();
        };
        _checkTimer.Tick += (_, _) => { _checkTimer.Stop(); _ = CheckAsync(false); };
        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); _ = RefreshSuggestionsAsync(false); };
        _aiTimer.Tick += (_, _) => { _aiTimer.Stop(); _ = RequestGhostAsync(); };
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
            _aiTimer.Stop();
            _runCts?.Cancel();
            _aiCts?.Cancel();
            _ghostCts?.Cancel();
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

    private static bool IsDown(Windows.System.VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var deeper = FindScroll(child);
            if (deeper != null) return deeper;
        }
        return null;
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

    private void UpdateGutter()
    {
        var lines = _editor.Text.Count(c => c == '\r') + 1;
        if (lines == _gutterLines) return;
        _gutterLines = lines;
        _gutter.Text = string.Join("\n", Enumerable.Range(1, lines));
    }

    private void OnEditorScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_editorScroll == null) return;
        _gutterScroll.ChangeView(null, _editorScroll.VerticalOffset, null, true);
        _colorScroll.ChangeView(_editorScroll.HorizontalOffset, _editorScroll.VerticalOffset, null, true);
        ClearGhost();
        if (_bubble.Visibility == Visibility.Visible) PositionBubble();
    }

    private async Task StartAsync()
    {
        try { File.WriteAllText(Path.Combine(_folder, "syntax_check.py"), CheckScript); } catch { }
        var scratch = Path.Combine(_folder, "scratch.py");
        _loading = true;
        _editor.Text = File.Exists(scratch) ? File.ReadAllText(scratch) : Template;
        _loading = false;
        SetDirty(false);
        UpdateGutter();
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

    // ───────── typing: Tab, Enter, the keys of the bubble, Shift+Tab for the AI's text ─────────

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
        var bubble = _bubble.Visibility == Visibility.Visible;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down when bubble:
                e.Handled = true;
                MoveSelection(1);
                return;
            case Windows.System.VirtualKey.Up when bubble:
                e.Handled = true;
                MoveSelection(-1);
                return;
            case Windows.System.VirtualKey.Enter when bubble:
                e.Handled = true;
                AcceptSelected();
                return;
            case Windows.System.VirtualKey.Escape:
                if (bubble || _ghostText.Length > 0) { e.Handled = true; HideBubble(); ClearGhost(); }
                return;
            case Windows.System.VirtualKey.Tab:
                e.Handled = true;
                if (IsDown(Windows.System.VirtualKey.Shift)) { if (_ghostText.Length > 0) AcceptGhost(); }   // Shift+Tab = take the AI's faded text
                else if (bubble) AcceptSelected();
                else Type("    ");
                return;
            case Windows.System.VirtualKey.Back:
                if (TryDeletePair()) e.Handled = true;   // Backspace between () [] {} or quotes removes both
                return;
            case Windows.System.VirtualKey.Enter:
            {
                e.Handled = true;   // a new line starts with the indent of this one, and 4 more after a colon
                var line = CurrentLine();
                var indent = IndentOf(line);
                var all = _editor.Text;
                var at = _editor.SelectionStart;
                var open = at > 0 && at <= all.Length ? "([{".IndexOf(all[at - 1]) : -1;
                if (_editor.SelectionLength == 0 && open >= 0 && at < all.Length && all[at] == ")]}"[open])
                {
                    // Enter between brackets: the closing one goes to its own line, the caret waits in the middle
                    Type("\r" + indent + "    " + "\r" + indent);
                    _editor.Select(_editor.SelectionStart - (1 + indent.Length), 0);
                    return;
                }
                Type("\r" + indent + (line.TrimEnd().EndsWith(':') ? "    " : ""));
                return;
            }
        }
    }

    // ───────── the suggestion bubble ─────────

    // ───────── colours and brackets ─────────

    private void ApplyTheme()
    {
        var light = _root.ActualTheme == ElementTheme.Light;
        _brushes.Clear();
        foreach (var kind in Enum.GetValues<Tok>()) _brushes[kind] = new SolidColorBrush(Highlight.ColorOf(kind, light));
        Recolor();
    }

    private void AddRun(string text, Brush? brush)
    {
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) _colorText.Inlines.Add(new LineBreak());
            if (parts[i].Length == 0) continue;
            var run = new Run { Text = parts[i] };
            if (brush != null) run.Foreground = brush;
            _colorText.Inlines.Add(run);
        }
    }

    /// <summary>Builds the see-through copy of the code that carries the colours (the editor's own text stays as it is, under it).</summary>
    private void Recolor()
    {
        try
        {
            var code = Normal;
            _colorText.Inlines.Clear();
            var position = 0;
            if (code.Length <= 80000)
            {
                foreach (var (start, length, kind) in Highlight.Scan(code))
                {
                    if (start > position) AddRun(code[position..start], null);
                    AddRun(code.Substring(start, length), _brushes.TryGetValue(kind, out var brush) ? brush : null);
                    position = start + length;
                }
            }
            if (position < code.Length) AddRun(code[position..], null);
        }
        catch
        {
            _colorText.Inlines.Clear();   // no colours is better than a wrong copy
        }
    }

    private static bool WordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void EditText(int position, int remove, string insert, int caretAt)
    {
        _editor.Select(position, remove);
        _editor.SelectedText = insert;
        _editor.Select(caretAt, 0);
        _prevText = _editor.Text;
    }

    private static bool IsStringPrefix(string text, int quoteIndex)
    {
        var from = quoteIndex;
        while (from > 0 && WordChar(text[from - 1])) from--;
        var word = text[from..quoteIndex];
        return word.Length is >= 1 and <= 2 && word.All(c => "rRbBfFuU".IndexOf(c) >= 0);
    }

    /// <summary>Brackets and quotes close themselves: after "print(" the text is "print()" with the caret between; typing the closing one steps over it.</summary>
    private void AutoClose()
    {
        var text = _editor.Text;
        if (text.Length != _prevText.Length + 1 || _editor.SelectionLength != 0) return;
        var caret = _editor.SelectionStart;
        if (caret < 1 || caret > text.Length || text.Remove(caret - 1, 1) != _prevText) return;   // not one typed character at the caret

        var typed = text[caret - 1];
        var next = caret < text.Length ? text[caret] : '\0';
        var free = next == '\0' || char.IsWhiteSpace(next) || ")]},:;".IndexOf(next) >= 0;   // nothing that belongs to a word follows

        var open = "([{".IndexOf(typed);
        if (open >= 0)
        {
            if (free) EditText(caret, 0, ")]}"[open].ToString(), caret);
            return;
        }
        if (")]}".IndexOf(typed) >= 0)
        {
            if (next == typed) EditText(caret, 1, "", caret);   // the closing one is already there: step over it
            return;
        }
        if (typed == '"' || typed == '\'')
        {
            if (next == typed) { EditText(caret, 1, "", caret); return; }
            var before = caret >= 2 ? text[caret - 2] : '\0';
            if (before == typed) return;                                        // the third quote of a triple-quoted string
            if (WordChar(before) && !IsStringPrefix(text, caret - 1)) return;   // an apostrophe inside a word
            if (free) EditText(caret, 0, typed.ToString(), caret);
        }
    }

    private bool TryDeletePair()
    {
        if (_editor.SelectionLength != 0) return false;
        var text = _editor.Text;
        var caret = _editor.SelectionStart;
        if (caret < 1 || caret >= text.Length) return false;
        var pair = new string(new[] { text[caret - 1], text[caret] });
        if (pair is not ("()" or "[]" or "{}" or "\"\"" or "''")) return false;
        EditText(caret - 1, 2, "", caret - 1);
        return true;
    }

    /// <summary>Where the caret is, in the editor's own coordinates.</summary>
    private Rect CaretRect()
    {
        // worked out from the line and column (Consolas is monospaced and the lines never wrap), the same way the line numbers are
        // placed: the editor's own rectangle API gave positions that were shifted by the padding
        var text = _editor.Text;
        var index = Math.Min(_editor.SelectionStart, text.Length);
        var before = text[..index];
        var line = before.Count(c => c == '\r');
        var column = index - (before.LastIndexOf('\r') + 1);
        var cell = CharSize();
        var pad = _editor.Padding;
        var offsetX = _editorScroll?.HorizontalOffset ?? 0;
        var offsetY = _editorScroll?.VerticalOffset ?? 0;
        return new Rect(pad.Left + column * cell.Width - offsetX, pad.Top + line * cell.Height - offsetY, 0, cell.Height);
    }

    private Size _cell;

    /// <summary>The size of one character cell of the editor's font (width of a letter, height of a line).</summary>
    private Size CharSize()
    {
        if (_cell.Width > 0) return _cell;
        var probe = new TextBlock { FontFamily = _editor.FontFamily, FontSize = _editor.FontSize, Text = new string('M', 20) };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _cell = new Size(probe.DesiredSize.Width / 20, probe.DesiredSize.Height);
        return _cell;
    }

    private static (string Letter, Windows.UI.Color Color) BadgeFor(string kind) => kind switch
    {
        "class" => ("C", Windows.UI.Color.FromArgb(255, 230, 140, 0)),
        "function" => ("f", Windows.UI.Color.FromArgb(255, 155, 89, 182)),
        "variable" => ("v", Windows.UI.Color.FromArgb(255, 52, 152, 219)),
        "keyword" => ("k", Windows.UI.Color.FromArgb(255, 192, 57, 43)),
        "built-in" => ("b", Windows.UI.Color.FromArgb(255, 39, 174, 96)),
        "snippet" => ("{}", Windows.UI.Color.FromArgb(255, 120, 130, 140)),
        "string" => ("s", Windows.UI.Color.FromArgb(255, 190, 110, 70)),
        "number" => ("#", Windows.UI.Color.FromArgb(255, 90, 160, 110)),
        _ => ("m", Windows.UI.Color.FromArgb(255, 22, 160, 133)),
    };

    private ListViewItem MakeItem(Suggestion s)
    {
        var (letter, color) = BadgeFor(s.Kind);
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(color),
            Child = new TextBlock
            {
                Text = letter,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var label = new TextBlock { Text = s.Label, VerticalAlignment = VerticalAlignment.Center };
        var kind = new TextBlock { Text = s.Detail.Length > 0 ? s.Detail : s.Kind, Opacity = 0.55, FontSize = 11, MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(kind, 2);
        row.Children.Add(badge);
        row.Children.Add(label);
        row.Children.Add(kind);
        var item = new ListViewItem { Content = row, Tag = s, MinHeight = 30, Padding = new Thickness(8, 2, 12, 2) };
        item.Tapped += (_, _) => Insert(s);
        return item;
    }

    private void ShowBubble(List<Suggestion> items, int start, int length)
    {
        _bubbleStart = start;
        _bubbleLength = length;
        _list.Items.Clear();
        foreach (var s in items) _list.Items.Add(MakeItem(s));
        _list.SelectedIndex = 0;
        _bubble.Visibility = Visibility.Visible;
        PositionBubble();
    }

    private void PositionBubble()
    {
        var rect = CaretRect();
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _bubble.DesiredSize;
        var x = Math.Max(0, Math.Min(rect.X, _editor.ActualWidth - size.Width - 8));
        var y = rect.Y + rect.Height + 4;
        if (y + size.Height > _editor.ActualHeight) y = Math.Max(0, rect.Y - size.Height - 4);   // no room below: above the line
        Canvas.SetLeft(_bubble, x);
        Canvas.SetTop(_bubble, y);
    }

    private void HideBubble()
    {
        _bubble.Visibility = Visibility.Collapsed;
        _list.Items.Clear();
    }

    private void MoveSelection(int step)
    {
        var count = _list.Items.Count;
        if (count == 0) return;
        var index = (Math.Max(_list.SelectedIndex, 0) + step + count) % count;
        _list.SelectedIndex = index;
        _list.ScrollIntoView(_list.Items[index]);
    }

    private void AcceptSelected()
    {
        if (_list.SelectedIndex >= 0 && _list.Items[_list.SelectedIndex] is ListViewItem { Tag: Suggestion s }) Insert(s);
    }

    private void Insert(Suggestion s)
    {
        var start = _bubbleStart;
        var length = _bubbleLength;
        HideBubble();
        var text = s.Kind == "snippet" ? s.Insert.Replace("\r", "\r" + IndentOf(CurrentLine())) : s.Insert;
        _editor.Select(start, length);
        _editor.SelectedText = text;
        _editor.Select(start + text.Length, 0);
        _editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Names inside a module that the file imports, as "C:name" (class), "F:name" (function), "V:name" (other). Asked from Python once, then remembered.</summary>
    private async Task<string[]> MembersAsync(string owner, string text)
    {
        if (_members.TryGetValue(owner, out var known)) return known;
        if (!Regex.IsMatch(text, @"^\s*(?:import|from)\s[^\r\n]*\b" + owner + @"\b", RegexOptions.Multiline)) return Array.Empty<string>();   // only modules the file imports

        var names = await Task.Run(() =>
        {
            var py = Py.Find();
            if (py == null) return Array.Empty<string>();
            var script = "import " + owner + " as m; print(' '.join(('C:' if isinstance(getattr(m,n,None),type) else 'F:' if callable(getattr(m,n,None)) else 'V:')+n for n in dir(m) if not n.startswith('_')))";
            var (code, output) = Py.RunQuick(py.File, py.Prefix.Concat(new[] { "-c", script }), 8000);
            return code == 0 ? output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        });
        _members[owner] = names;
        return names;
    }

    private static Suggestion ParseMember(string raw)
    {
        if (raw.Length > 2 && raw[1] == ':')
        {
            var kind = raw[0] switch { 'C' => "class", 'F' => "function", _ => "variable" };
            return new Suggestion(raw[2..], raw[2..], kind);
        }
        return new Suggestion(raw, raw, "variable");
    }

    private async Task RefreshSuggestionsAsync(bool force)
    {
        var text = _editor.Text;
        var caret = _editor.SelectionStart;
        var (prefix, owner) = Complete.Context(text, caret);
        List<Suggestion> items;
        if (owner != null)
        {
            var names = await MembersAsync(owner, text);
            if (_editor.SelectionStart != caret) return;   // the person moved on
            items = names.Select(ParseMember)
                .Where(s => s.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (prefix.Length == 0 || !s.Label.Equals(prefix, StringComparison.Ordinal)))
                .Take(60).ToList();
        }
        else items = Complete.Local(text, prefix, force);

        if (items.Count == 0) { HideBubble(); return; }
        ShowBubble(items, caret - prefix.Length, prefix.Length);
    }

    // ───────── the AI continues the code (faded text, Shift+Tab takes it) ─────────

    private void ClearGhost()
    {
        _ghostCts?.Cancel();
        _ghostText = "";
        _ghost1.Visibility = Visibility.Collapsed;
        _ghost2.Visibility = Visibility.Collapsed;
    }

    private void ShowGhost(string answer)
    {
        var lines = answer.Replace("\r\n", "\n").Split('\n');
        _ghostText = string.Join("\r", lines);
        var rect = CaretRect();
        _ghost1.Text = lines[0];
        Canvas.SetLeft(_ghost1, rect.X);
        Canvas.SetTop(_ghost1, rect.Y);
        _ghost1.Visibility = Visibility.Visible;
        if (lines.Length > 1)
        {
            _ghost2.Text = string.Join("\n", lines.Skip(1));
            Canvas.SetLeft(_ghost2, _editor.Padding.Left - (_editorScroll?.HorizontalOffset ?? 0));   // the next lines start at column 0
            Canvas.SetTop(_ghost2, rect.Y + rect.Height);
            _ghost2.Visibility = Visibility.Visible;
        }
    }

    private void AcceptGhost()
    {
        var text = _ghostText;
        ClearGhost();
        Type(text);
    }

    /// <summary>After a pause in typing: asks the AI what comes next. Only when the caret is at the end of the code, so the faded text never covers other code.</summary>
    private async Task RequestGhostAsync()
    {
        if (_aiComplete.IsChecked != true || _bubble.Visibility == Visibility.Visible) return;
        var text = _editor.Text;
        var caret = _editor.SelectionStart;
        if (caret < 8 || text[caret..].Trim().Length > 0) return;

        var settings = Ai.Load(_folder);
        var provider = Ai.Resolve(settings);
        var key = await Ai.LoadKeyAsync(_folder, provider.Id);
        if (string.IsNullOrEmpty(key)) return;

        _ghostCts?.Cancel();
        var cts = _ghostCts = new CancellationTokenSource();
        try
        {
            var before = text[..caret].Replace("\r\n", "\n").Replace('\r', '\n');
            var answer = await Ai.CompleteAsync(provider, Ai.ModelFor(settings, provider), key, before, cts.Token);
            if (cts.IsCancellationRequested || _editor.Text != text || _editor.SelectionStart != caret || answer.Trim().Length == 0) return;
            ShowGhost(answer);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_ghostErrorLogged) return;
            _ghostErrorLogged = true;
            Log("AI completion failed (switch it off with 'AI complete'): " + ex.Message);
        }
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
        var settings = Ai.Load(_folder);
        var provider = new ComboBox { Header = "Provider", Width = 380 };
        foreach (var p in Ai.Providers) provider.Items.Add(p.Name);
        provider.SelectedIndex = Math.Max(0, Array.FindIndex(Ai.Providers, p => p.Id == settings.Provider));

        // only for "Another provider"
        var customName = new TextBox { Header = "Name (optional)", Text = settings.CustomName, Width = 380 };
        var customUrl = new TextBox { Header = "Address", PlaceholderText = "https://example.com/v1", Text = settings.CustomUrl, Width = 380 };
        var customStyle = new ComboBox { Header = "Style (how the service is spoken to)", Width = 380 };
        customStyle.Items.Add("OpenAI compatible");
        customStyle.Items.Add("Anthropic");
        customStyle.Items.Add("Gemini");
        customStyle.SelectedIndex = settings.CustomStyle switch { "anthropic" => 1, "gemini" => 2, _ => 0 };
        var custom = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed, Children = { customName, customUrl, customStyle } };
        var cfAccount = new TextBox { Header = "Cloudflare Account ID", PlaceholderText = "32 letters and digits, from dash.cloudflare.com", Text = settings.CloudflareAccount, Width = 380, Visibility = Visibility.Collapsed };

        var model = new ComboBox { Header = "Model (pick one or type its name)", Width = 380, IsEditable = true };
        var key = new PasswordBox { Header = "API key", PlaceholderText = "paste a new key (empty = keep the saved one)", Width = 380 };
        var keyState = new TextBlock { Opacity = 0.7, FontSize = 12 };
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 380, Opacity = 0.7, FontSize = 12 };
        var save = new Button { Content = "Save" };
        var remove = new Button { Content = "Remove saved key" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, remove } };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { provider, custom, cfAccount, model, key, keyState, info, buttons } } };

        ProviderInfo Chosen() => Ai.Providers[Math.Max(0, provider.SelectedIndex)];
        void Sync()
        {
            var p = Chosen();
            custom.Visibility = p.Id == "custom" ? Visibility.Visible : Visibility.Collapsed;
            cfAccount.Visibility = p.Id == "cloudflare" ? Visibility.Visible : Visibility.Collapsed;
            model.Items.Clear();
            foreach (var name in Ai.KnownModels(p.Id)) model.Items.Add(name);
            model.Text = settings.Models.TryGetValue(p.Id, out var m) && m.Length > 0 ? m : p.DefaultModel;
            keyState.Text = Ai.HasKey(_folder, p.Id) ? "A key is saved for this provider." : "No key is saved for this provider yet.";
            info.Text = p.Note + " The key is stored encrypted for your Windows account and is only sent to "
                + (p.Id == "custom" ? "the address you enter above." : new Uri(p.Url).Host + ".");
        }
        provider.SelectionChanged += (_, _) => Sync();
        Sync();

        save.Click += async (_, _) =>
        {
            var p = Chosen();
            settings.Provider = p.Id;
            settings.Models[p.Id] = model.Text.Trim();
            settings.CustomName = customName.Text.Trim();
            settings.CloudflareAccount = cfAccount.Text.Trim();
            settings.CustomUrl = customUrl.Text.Trim();
            settings.CustomStyle = customStyle.SelectedIndex switch { 1 => "anthropic", 2 => "gemini", _ => "openai" };
            settings.Inline = _aiComplete.IsChecked == true;
            Ai.Save(_folder, settings);
            if (key.Password.Length > 0) await Ai.SaveKeyAsync(_folder, p.Id, key.Password.Trim());
            key.Password = "";
            Sync();
            flyout.Hide();
            _ghostErrorLogged = false;
            Log("AI settings saved: " + Ai.Resolve(settings).Name);
        };
        remove.Click += async (_, _) =>
        {
            var p = Chosen();
            await Ai.SaveKeyAsync(_folder, p.Id, "");
            Sync();
            Log("The saved API key of " + p.Name + " was removed");
        };
        return flyout;
    }

    private void SetAi(string text)
    {
        _aiOut.Clear();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) _aiOut.Add(line, false, scroll: false);
        _aiOut.ScrollToTop();
    }

    private async Task AnalyzeAsync()
    {
        Show(_aiItem);
        var settings = Ai.Load(_folder);
        var provider = Ai.Resolve(settings);
        var key = await Ai.LoadKeyAsync(_folder, provider.Id);
        if (string.IsNullOrEmpty(key))
        {
            SetAi($"Add your API key for {provider.Name} first: press 'AI settings'. The key is stored encrypted for your Windows account.");
            return;
        }
        var code = Normal;
        if (code.Trim().Length == 0) { SetAi("There is no code to analyze."); return; }

        _aiCts?.Cancel();
        var cts = _aiCts = new CancellationTokenSource();
        _aiOut.Clear();
        _aiButton.IsEnabled = false;
        _aiRing.Visibility = Visibility.Visible;
        _aiRing.IsActive = true;
        var model = Ai.ModelFor(settings, provider);
        Log($"AI analysis started ({provider.Name}, {model})");
        try
        {
            SetAi(await Ai.AnalyzeAsync(provider, model, key, code, _output.Text, _lastProblems, cts.Token));
            Log("AI analysis finished");
        }
        catch (OperationCanceledException) { Log("AI analysis cancelled"); }
        catch (Exception ex)
        {
            SetAi("The analysis failed: " + ex.Message);
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
