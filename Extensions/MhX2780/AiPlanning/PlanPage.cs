using AsdAi;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AiPlanning;

/// <summary>
/// The page, built like Automation's: a header with a toolbar of split buttons, a board of cards joined by arrows, and a hint line.
/// The proposal is the first card; each AI's view, the moderator's draft, flaws, questions to you and the decision appear as cards on their own as the discussion goes.
/// Click a card to read it. The discussion itself is in <see cref="Debate"/>.
/// </summary>
internal sealed class PlanPage : Grid
{
    private static readonly (string Label, int Rounds, int Minutes)[] Presets =
    {
        ("Quick: 3 rounds, 4 minutes", 3, 4),
        ("Normal: 5 rounds, 10 minutes", 5, 10),
        ("Deep: 8 rounds, 25 minutes", 8, 25),
        ("Marathon: 15 rounds, 60 minutes", 15, 60),
    };

    private sealed class QuestionState
    {
        public List<string> Questions = new();
        public string[] Answers = Array.Empty<string>();
        public TaskCompletionSource<List<string>?> Tcs = new();
        public bool Done;
        public BoardNode Node = null!;
    }

    private const double GapX = 330, GapY = 150;

    private readonly string _folder;
    private readonly List<AiModel> _models;
    private readonly Board _board = new();
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true };
    private readonly TextBlock _status = new() { Opacity = 0.7, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 };
    private readonly ComboBox _moderator = new() { Header = "Moderator (writes the drafts and the decision)", MinWidth = 300 };
    private readonly ComboBox _time = new() { Header = "Thinking time", MinWidth = 300 };
    private readonly CheckBox _flaws = new() { Content = "Flaw check before the decision", IsChecked = true };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private SplitButton _btnStart = null!;
    private Button _btnStop = null!;
    private DropDownButton _btnZoom = null!;
    private MenuFlyoutItem _btnMore = null!, _btnNew = null!, _btnCopyDecision = null!, _btnCopyAll = null!;

    private Debate? _debate;
    private CancellationTokenSource? _cancel;
    private string _proposalText = "";
    private string _statusText = "";
    private string _sessionFile = "";
    private bool _running, _dialogOpen;

    // where the next card goes: one column per step of the discussion
    private int _seq, _col, _row, _size = 3;
    private string _groupKind = "", _lastSingle = "";
    private List<string> _group = new();

    public PlanPage(string folder)
    {
        _folder = folder;
        _models = AiModels.Load(folder);
        foreach (var p in Presets) _time.Items.Add(p.Label);
        _time.SelectedIndex = 1;
        BuildLayout();
        RefreshModels();
        ResetBoard();
        _board.NodeClicked += n => _ = OnNodeAsync(n);
        _board.ZoomChanged += t => _btnZoom.Content = t;
        _clock.Tick += (_, _) => ShowStatus();
        Unloaded += (_, _) => { _clock.Stop(); _cancel?.Cancel(); };
    }

    // ───────── layout ─────────

    private static StackPanel Label(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        row.Children.Add(new TextBlock { Text = text });
        return row;
    }

    private static MenuFlyoutItem Item(string glyph, string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => click();
        return item;
    }

    private static Border Separator() => new() { Width = 1, Height = 22, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.5 } };

    private void BuildLayout()
    {
        _btnMore = Item("", "Add time and continue (+4 rounds, +15 minutes)", () => _ = MoreAsync());
        _btnNew = Item("", "New discussion", NewDiscussion);
        var menu = new MenuFlyout();
        menu.Items.Add(_btnMore);
        menu.Items.Add(_btnNew);
        _btnStart = new SplitButton { Content = Label("", "Start"), Flyout = menu };
        if (Application.Current.Resources.TryGetValue("AccentSplitButtonStyle", out var st) && st is Style accent) _btnStart.Style = accent;
        _btnStart.Click += async (_, _) => { if (_debate == null) await StartAsync(); else await MoreAsync(); };
        ToolTipService.SetToolTip(_btnStart, "Start the discussion. The arrow: add time and continue, new discussion.");

        _btnStop = new Button { Content = Label("", "Stop"), IsEnabled = false };
        _btnStop.Click += (_, _) => _cancel?.Cancel();

        var models = new Button { Content = Label("", "AI models") };
        models.Click += async (_, _) => { await ShowModelsAsync(); };

        var settingsPanel = new StackPanel { Spacing = 10, Width = 320 };
        settingsPanel.Children.Add(_moderator);
        settingsPanel.Children.Add(_time);
        settingsPanel.Children.Add(_flaws);
        var settings = new DropDownButton { Content = Label("", "Settings"), Flyout = new Flyout { Content = settingsPanel } };

        _btnCopyDecision = Item("", "Copy the decision", () => CopyText(_debate == null ? "" : _debate.Final.Length > 0 ? _debate.Final : _debate.Draft));
        _btnCopyAll = Item("", "Copy the whole discussion", () => CopyText(_debate?.ToMarkdown() ?? ""));
        var copyMenu = new MenuFlyout();
        copyMenu.Items.Add(_btnCopyDecision);
        copyMenu.Items.Add(_btnCopyAll);
        copyMenu.Items.Add(Item("", "Open the history folder", OpenHistory));
        var copy = new DropDownButton { Content = Label("", "Copy"), Flyout = copyMenu };

        var zoomMenu = new MenuFlyout();
        zoomMenu.Items.Add(Item("", "Zoom in", () => _board.Zoom(1.25f)));
        zoomMenu.Items.Add(Item("", "Zoom out", () => _board.Zoom(1 / 1.25f)));
        zoomMenu.Items.Add(Item("", "100%", () => _board.ResetZoom()));
        zoomMenu.Items.Add(Item("", "Fit everything", () => _board.ZoomToFit()));
        _btnZoom = new DropDownButton { Content = "100%", Flyout = zoomMenu };
        ToolTipService.SetToolTip(_btnZoom, "Zoom. Ctrl + mouse wheel, or two fingers on the touchpad, also zoom.");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new UIElement[] { _btnStart, _btnStop, Separator(), models, settings, Separator(), copy, Separator(), _btnZoom }) bar.Children.Add(b);
        var barScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, Content = bar, HorizontalAlignment = HorizontalAlignment.Right,
        };
        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "AI Planning", FontSize = 26, FontWeight = FontWeights.Bold });
        title.Children.Add(_status);
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(barScroll, 1);
        header.Children.Add(title);
        header.Children.Add(barScroll);

        var area = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MinHeight = 420,
            BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.4 }, Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.06 }, Child = _board,
        };
        var hint = new TextBlock
        {
            Text = "Click the first card to write your proposal, add the AI models you want (AI models), then Start. Each AI gets a card; the moderator merges them into a draft; they judge the draft until all agree. " +
                   "Click any card to read it · drag a card to move it · right mouse or the empty board: move the view · Ctrl + wheel / pinch: zoom · Settings: moderator, thinking time, flaw check",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };

        RowSpacing = 8;
        Padding = new Thickness(24, 16, 24, 16);
        for (var i = 0; i < 4; i++) RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Microsoft.UI.Xaml.Controls.Grid.SetRow(_info, 1);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(area, 2);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(hint, 3);
        Children.Add(header);
        Children.Add(_info);
        Children.Add(area);
        Children.Add(hint);
    }

    private void Say(string text, InfoBarSeverity severity)
    {
        _info.Severity = severity;
        _info.Message = text;
        _info.IsOpen = true;
    }

    private async Task ShowModelsAsync()
    {
        while (_dialogOpen) await Task.Delay(200);
        _dialogOpen = true;
        try { await AiModelsDialog.ShowAsync(XamlRoot, _folder, _models, withRole: true); }
        finally { _dialogOpen = false; }
        RefreshModels();
    }

    private void RefreshModels()
    {
        var keep = (_moderator.SelectedItem as ComboBoxItem)?.Tag as AiModel;
        var on = _models.Where(m => m.Enabled).ToList();
        _moderator.Items.Clear();
        foreach (var m in on) _moderator.Items.Add(new ComboBoxItem { Content = AiModels.Display(m), Tag = m });
        _moderator.SelectedIndex = on.Count == 0 ? -1 : Math.Max(0, keep == null ? 0 : on.IndexOf(keep));
        if (_debate == null)
        {
            _statusText = on.Count >= 2 ? $"{on.Count} AIs: " + string.Join(", ", on.Select(AiModels.Display)) : "Add at least two AI models (AI models).";
            ShowStatus();
        }
    }

    private static void CopyText(string text)
    {
        try { var p = new DataPackage(); p.SetText(text); Clipboard.SetContent(p); } catch { }
    }

    private async void OpenHistory()
    {
        try { Directory.CreateDirectory(Path.Combine(_folder, "sessions")); await Windows.System.Launcher.LaunchFolderPathAsync(Path.Combine(_folder, "sessions")); }
        catch (Exception ex) { Say("Could not open the folder: " + ex.Message, InfoBarSeverity.Warning); }
    }

    // ───────── the board: where each card goes ─────────

    private static double ColX(int col) => 40 + col * GapX;

    private double SingleY() => 40 + (Math.Max(1, _size) - 1) / 2.0 * GapY;

    private static string Flat(string s, int max)
    {
        var t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > max ? t[..max] + "..." : t;
    }

    private void ResetBoard()
    {
        _board.Clear();
        _seq = 0; _col = 0; _row = 0; _groupKind = ""; _group = new();
        var n = new BoardNode { Id = "proposal", Kind = "Proposal", Title = "Your proposal", Tone = "info" };
        UpdateProposalNode(n);
        _board.AddNode(n, ColX(0), SingleY());
        _lastSingle = n.Id;
    }

    private void UpdateProposalNode(BoardNode n)
    {
        n.Summary = _proposalText.Length > 0 ? Flat(_proposalText, 150) : "Click here to write the proposal or the question the AIs should decide.";
        n.Status = _debate == null ? "click to edit" : "";
        n.Parts = new() { ("Proposal", _proposalText) };
    }

    private static string StatusOf(Cell c) => c.Tone switch
    {
        "agree" => "agrees", "object" => "objects", "draft" => "draft", "flaws" => "flaws found",
        "final" => "final decision", "warn" => "no full agreement", "error" => "left the discussion", _ => "",
    };

    private void AddCell(Cell c)
    {
        var first = c.Parts.Count > 0 ? c.Parts[0].Text : "";
        var n = new BoardNode
        {
            Id = "n" + (++_seq), Kind = c.Kind, Title = c.Title, Subtitle = c.Subtitle.Length > 0 && c.Kind.StartsWith("Round") ? "" : c.Subtitle, Tone = c.Tone,
            Summary = Flat(first, 150), Status = StatusOf(c), Parts = c.Parts,
        };
        if (c.Kind.StartsWith("Round ") && c.Subtitle.Length > 0 && c.Subtitle is "agrees" or "objects") n.Subtitle = c.Subtitle;

        if (c.Kind.StartsWith("Round ") || c.Kind == "Flaws" || c.Kind == "Skipped")
        {
            if (c.Kind != _groupKind) { _groupKind = c.Kind; _col++; _row = 0; _group = new(); }
            _board.AddNode(n, ColX(_col), 40 + _row++ * GapY);
            if (c.Kind != "Skipped" && _lastSingle.Length > 0)
                _board.AddLink(new BoardLink(_lastSingle, n.Id, c.Kind == "Flaws" ? "flaw check" : (_debate?.Round ?? 1) <= 1 ? "opening" : "judges the draft", c.Kind == "Flaws" ? "flaws" : "draft"));
            _group.Add(n.Id);
        }
        else
        {
            var sources = _groupKind.Length > 0 ? _group.ToList() : _lastSingle.Length > 0 ? new List<string> { _lastSingle } : new List<string>();
            _col++;
            _groupKind = "";
            _board.AddNode(n, ColX(_col), SingleY());
            foreach (var from in sources)
            {
                var f = _board.Find(from);
                _board.AddLink(new BoardLink(from, n.Id, f == null ? "" : f.Subtitle.Length > 0 ? f.Subtitle : f.Kind == "Flaws" ? "flaws" : f.Kind.StartsWith("Round") ? "view" : "", f?.Tone ?? "info"));
            }
            _lastSingle = n.Id;
        }
        _board.Reveal(n);
    }

    // ───────── start, continue, stop ─────────

    private async Task StartAsync()
    {
        var on = _models.Where(m => m.Enabled).ToList();
        if (_proposalText.Trim().Length == 0) { Say("Click the first card and write the proposal first.", InfoBarSeverity.Warning); return; }
        if (on.Count < 2) { Say("Add at least two AI models first (AI models), each with its API key.", InfoBarSeverity.Warning); return; }
        _info.IsOpen = false;
        var preset = Presets[Math.Max(0, _time.SelectedIndex)];
        _sessionFile = Path.Combine(_folder, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        _debate = new Debate
        {
            Folder = _folder, Proposal = _proposalText.Trim(), Participants = on,
            Moderator = (_moderator.SelectedItem as ComboBoxItem)?.Tag as AiModel ?? on[0],
            FlawCheck = _flaws.IsChecked == true, MaxRounds = preset.Rounds, Deadline = DateTime.UtcNow.AddMinutes(preset.Minutes),
            OnCell = AddCell, OnStatus = t => { _statusText = t; ShowStatus(); }, AskUser = AskUserAsync,
        };
        _size = on.Count;
        if (_board.Find("proposal") is { } p) { UpdateProposalNode(p); _board.MoveNode(p, ColX(0), SingleY()); _board.Refresh(p); }
        await RunAsync();
    }

    private async Task MoreAsync()
    {
        if (_debate == null || _running) return;
        _debate.MaxRounds += 4;
        _debate.Deadline = (DateTime.UtcNow > _debate.Deadline ? DateTime.UtcNow : _debate.Deadline).AddMinutes(15);
        await RunAsync();
    }

    private async Task RunAsync()
    {
        var debate = _debate!;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        SetRunning(true);
        try { await debate.RunAsync(cancel.Token); }
        catch (OperationCanceledException) { _statusText = "Stopped. Use the arrow next to Start to add time and continue."; }
        catch (Exception ex) { _statusText = "Error: " + ex.Message; }
        finally
        {
            SetRunning(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionFile)!);
                File.WriteAllText(_sessionFile, debate.ToMarkdown());
            }
            catch { /* the history is a convenience */ }
        }
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _btnStart.IsEnabled = !running;
        _btnStop.IsEnabled = running;
        _btnMore.IsEnabled = !running && _debate != null;
        _btnNew.IsEnabled = !running;
        _btnCopyDecision.IsEnabled = _btnCopyAll.IsEnabled = _debate != null;
        ((TextBlock)((StackPanel)_btnStart.Content).Children[1]).Text = _debate == null ? "Start" : "Continue";
        if (running) _clock.Start(); else _clock.Stop();
        ShowStatus();
    }

    private void NewDiscussion()
    {
        if (_running) return;
        _debate = null;
        _statusText = "";
        ResetBoard();
        SetRunning(false);
        RefreshModels();
    }

    private void ShowStatus()
    {
        var text = _statusText;
        if (_running && _debate != null)
        {
            var left = _debate.Deadline - DateTime.UtcNow;
            text += left > TimeSpan.Zero ? $"   ·   {(int)left.TotalMinutes:00}:{left.Seconds:00} left" : "   ·   time is up after this round";
        }
        _status.Text = text;
    }

    // ───────── dialogs ─────────

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        while (_dialogOpen) await Task.Delay(200);
        _dialogOpen = true;
        try { dialog.XamlRoot = XamlRoot; return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private static ScrollViewer Scrollable(UIElement content) => new() { Content = content, MaxHeight = 520, Padding = new Thickness(0, 0, 14, 0) };

    private async Task OnNodeAsync(BoardNode n)
    {
        if (n.Id == "proposal") { await OpenProposalAsync(n); return; }
        if (n.Tag is QuestionState q && !q.Done) { await OpenQuestionAsync(q); return; }

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (var (label, text) in n.Parts)
        {
            body.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.6, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        var dialog = new ContentDialog { Title = n.Title, Content = Scrollable(body), CloseButtonText = "Close", SecondaryButtonText = "Copy" };
        if (await ShowAsync(dialog) == ContentDialogResult.Secondary)
            CopyText(n.Kind + ": " + n.Title + "\n\n" + string.Join("\n\n", n.Parts.Select(p => p.Label + ": " + p.Text)));
    }

    private async Task OpenProposalAsync(BoardNode n)
    {
        var box = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220, MinWidth = 460, Text = _proposalText, IsReadOnly = _debate != null,
            PlaceholderText = "For example: Should our team move the backend from Python to Go this year? Add the facts that matter: size, deadline, budget...",
        };
        var dialog = new ContentDialog
        {
            Title = "Your proposal", Content = box, CloseButtonText = _debate == null ? "Cancel" : "Close",
            PrimaryButtonText = _debate == null ? "Save" : "", DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary || _debate != null) return;
        _proposalText = box.Text.Trim();
        UpdateProposalNode(n);
        _board.Refresh(n);
    }

    private async Task OpenQuestionAsync(QuestionState q)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        var boxes = new List<TextBox>();
        for (var i = 0; i < q.Questions.Count; i++)
        {
            body.Children.Add(new TextBlock { Text = q.Questions[i], TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true });
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, PlaceholderText = "Your answer", Text = i < q.Answers.Length ? q.Answers[i] : "" };
            boxes.Add(box);
            body.Children.Add(box);
        }
        var dialog = new ContentDialog
        {
            Title = "The AIs need your answer", Content = Scrollable(body), PrimaryButtonText = "Send my answers",
            SecondaryButtonText = "Let the AIs decide", CloseButtonText = "Later", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowAsync(dialog);
        q.Answers = boxes.Select(b => b.Text).ToArray();
        if (result == ContentDialogResult.None || q.Done) return;
        q.Done = true;
        var answers = result == ContentDialogResult.Primary ? q.Answers.ToList() : null;
        q.Node.Status = answers == null ? "the AIs decide" : "answered";
        q.Node.Tone = "info";
        _board.Refresh(q.Node);
        if (_debate != null)
        {
            _debate.Log.AppendLine("## Question for the user");
            for (var i = 0; i < q.Questions.Count; i++)
                _debate.Log.AppendLine($"- {q.Questions[i]}  \n  Answer: {(answers != null && i < answers.Count && answers[i].Trim().Length > 0 ? answers[i].Trim() : "(none)")}");
            _debate.Log.AppendLine();
        }
        q.Tcs.TrySetResult(answers);
    }

    /// <summary>A purple card with the AIs' questions. The discussion waits until the person answers or lets the AIs decide.</summary>
    private Task<List<string>?> AskUserAsync(List<string> questions, CancellationToken ct)
    {
        var q = new QuestionState { Questions = questions };
        var n = new BoardNode
        {
            Id = "n" + (++_seq), Kind = "Question", Title = "A question for you", Tone = "question", Status = "waiting for your answer · click",
            Summary = Flat(questions[0], 150), Parts = questions.Select((t, i) => ($"Question {i + 1}", t)).ToList(), Tag = q,
        };
        q.Node = n;
        _col++;
        var sources = _groupKind.Length > 0 ? _group.ToList() : new List<string> { _lastSingle };
        _groupKind = "";
        _board.AddNode(n, ColX(_col), SingleY());
        foreach (var from in sources) _board.AddLink(new BoardLink(from, n.Id, "asks you", "question"));
        _lastSingle = n.Id;
        _board.Reveal(n);
        Say("The AIs have a question for you: answer it in the purple card (it opens now), or let them decide.", InfoBarSeverity.Informational);
        ct.Register(() => q.Tcs.TrySetCanceled());
        _ = OpenQuestionAsync(q);
        return q.Tcs.Task;
    }
}
