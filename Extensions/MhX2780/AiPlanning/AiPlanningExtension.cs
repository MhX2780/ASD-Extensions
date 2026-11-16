using ASD.Extensibility;
using AsdAi;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AiPlanning;

public sealed class AiPlanningExtension : IAsdExtension
{
    public string Id => "MhX2780.AiPlanning";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "aiplanning", Title = "AI Planning", Glyph = "", Create = () => new PlanPage(host.DataFolder) });
    }
}

/// <summary>
/// The page: a proposal cell, then one cell per thing that happens (each AI's view, the moderator's draft, flaws, questions to you, the decision).
/// The discussion itself is in <see cref="Debate"/>.
/// </summary>
internal sealed class PlanPage : StackPanel
{
    private static readonly (string Label, int Rounds, int Minutes)[] Presets =
    {
        ("Quick: 3 rounds, 4 minutes", 3, 4),
        ("Normal: 5 rounds, 10 minutes", 5, 10),
        ("Deep: 8 rounds, 25 minutes", 8, 25),
        ("Marathon: 15 rounds, 60 minutes", 15, 60),
    };

    private readonly string _folder;
    private readonly List<AiModel> _models;
    private readonly ComboBox _moderator = new() { Header = "Moderator", MinWidth = 240 };
    private readonly ComboBox _time = new() { Header = "Thinking time", MinWidth = 240 };
    private readonly CheckBox _flaws = new() { Content = "Flaw check before the decision", IsChecked = true };
    private readonly TextBlock _people = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _proposal = new() { Header = "Your proposal or the question to decide", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, PlaceholderText = "For example: Should our team move the backend from Python to Go this year?  Add the facts that matter: size, deadline, budget..." };
    private readonly Button _start = new() { Content = "Start the discussion", Padding = new Thickness(20, 8, 20, 8) };
    private readonly Button _more = new() { Content = "Add time and continue", IsEnabled = false };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _new = new() { Content = "New discussion" };
    private readonly Button _copyDecision = new() { Content = "Copy the decision", IsEnabled = false };
    private readonly Button _copyAll = new() { Content = "Copy everything", IsEnabled = false };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _cells = new() { Spacing = 4 };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private Debate? _debate;
    private CancellationTokenSource? _cancel;
    private string _statusText = "";
    private string _sessionFile = "";
    private bool _running;

    public PlanPage(string folder)
    {
        _folder = folder;
        _models = AiModels.Load(folder);
        Spacing = 12;
        foreach (var p in Presets) _time.Items.Add(p.Label);
        _time.SelectedIndex = 1;
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style accent) _start.Style = accent;

        var manage = new Button { Content = "AI models..." };
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
        row1.Children.Add(manage);
        row1.Children.Add(_moderator);
        row1.Children.Add(_time);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in new UIElement[] { _start, _more, _stop, _new, _copyDecision, _copyAll }) row2.Children.Add(b);
        var history = new Button { Content = "History folder" };
        row2.Children.Add(history);

        Children.Add(new TextBlock { Text = "Add the AI models you want (each with its own key and, if you like, a role such as critic). They discuss your proposal in rounds, look for flaws, may ask you questions, and agree on one decision.", TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        Children.Add(row1);
        Children.Add(_people);
        Children.Add(_flaws);
        Children.Add(row2);
        Children.Add(_busy);
        Children.Add(_status);
        _cells.Children.Add(Frame("Proposal", "What should the AIs decide?", "", "info", _proposal));
        Children.Add(_cells);

        RefreshModels();
        manage.Click += async (_, _) =>
        {
            await AiModelsDialog.ShowAsync(XamlRoot, _folder, _models, withRole: true);
            RefreshModels();
        };
        history.Click += async (_, _) =>
        {
            try { Directory.CreateDirectory(Path.Combine(_folder, "sessions")); await Windows.System.Launcher.LaunchFolderPathAsync(Path.Combine(_folder, "sessions")); }
            catch (Exception ex) { _status.Text = "Could not open the folder: " + ex.Message; }
        };
        _start.Click += async (_, _) => await StartAsync();
        _more.Click += async (_, _) => await MoreAsync();
        _stop.Click += (_, _) => _cancel?.Cancel();
        _new.Click += (_, _) => NewDiscussion();
        _copyDecision.Click += (_, _) => CopyText(_debate == null ? "" : _debate.Final.Length > 0 ? _debate.Final : _debate.Draft);
        _copyAll.Click += (_, _) => CopyText(_debate?.ToMarkdown() ?? "");
        _clock.Tick += (_, _) => ShowStatus();
        Unloaded += (_, _) => { _clock.Stop(); _cancel?.Cancel(); };
    }

    private void RefreshModels()
    {
        var keep = (_moderator.SelectedItem as ComboBoxItem)?.Tag as AiModel;
        var on = _models.Where(m => m.Enabled).ToList();
        _moderator.Items.Clear();
        foreach (var m in on) _moderator.Items.Add(new ComboBoxItem { Content = AiModels.Display(m), Tag = m });
        _moderator.SelectedIndex = on.Count == 0 ? -1 : Math.Max(0, keep == null ? 0 : on.IndexOf(keep));
        _people.Text = on.Count >= 2
            ? $"The AIs ({on.Count}): " + string.Join("  ·  ", on.Select(m => AiModels.Display(m) + (m.Role.Length > 0 ? " (" + m.Role.Split(':')[0] + ")" : "")))
            : "Add at least two AI models with 'AI models...' (each needs its API key).";
    }

    // ───────── starting, continuing, stopping ─────────

    private async Task StartAsync()
    {
        var on = _models.Where(m => m.Enabled).ToList();
        if (_proposal.Text.Trim().Length == 0) { _status.Text = "Write the proposal first."; return; }
        if (on.Count < 2) { _status.Text = "Add at least two AI models first (AI models...)."; return; }
        var preset = Presets[Math.Max(0, _time.SelectedIndex)];
        var started = DateTime.Now;
        _sessionFile = Path.Combine(_folder, "sessions", started.ToString("yyyyMMdd-HHmmss") + ".md");
        _debate = new Debate
        {
            Folder = _folder,
            Proposal = _proposal.Text.Trim(),
            Participants = on,
            Moderator = (_moderator.SelectedItem as ComboBoxItem)?.Tag as AiModel ?? on[0],
            FlawCheck = _flaws.IsChecked == true,
            MaxRounds = preset.Rounds,
            Deadline = DateTime.UtcNow.AddMinutes(preset.Minutes),
            OnCell = AddCell,
            OnStatus = t => { _statusText = t; ShowStatus(); },
            AskUser = AskUserAsync,
        };
        _proposal.IsReadOnly = true;
        await RunAsync();
    }

    private async Task MoreAsync()
    {
        if (_debate == null) return;
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
        catch (OperationCanceledException) { _statusText = "Stopped. You can add time and continue."; }
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
        _start.IsEnabled = !running && _debate == null;
        _more.IsEnabled = !running && _debate != null;
        _stop.IsEnabled = running;
        _new.IsEnabled = !running;
        _copyDecision.IsEnabled = _copyAll.IsEnabled = _debate != null && !running;
        _busy.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) _clock.Start(); else _clock.Stop();
        ShowStatus();
    }

    private void NewDiscussion()
    {
        _debate = null;
        _statusText = "";
        _proposal.IsReadOnly = false;
        for (var i = _cells.Children.Count - 1; i >= 1; i--) _cells.Children.RemoveAt(i);
        SetRunning(false);
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

    private static void CopyText(string text)
    {
        try { var p = new DataPackage(); p.SetText(text); Clipboard.SetContent(p); } catch { }
    }

    // ───────── the cells ─────────

    private static (byte R, byte G, byte B) ToneColor(string tone) => tone switch
    {
        "agree" or "final" => (34, 160, 75),
        "object" or "error" => (229, 72, 77),
        "draft" => (59, 130, 246),
        "flaws" or "warn" => (245, 158, 11),
        "question" => (139, 92, 246),
        _ => (128, 128, 128),
    };

    /// <summary>One cell: a colored label, a title, and the content.</summary>
    private static Border Frame(string kind, string title, string subtitle, string tone, UIElement body)
    {
        var (r, g, b) = ToneColor(tone);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, r, g, b)),
            Child = new TextBlock { Text = kind, FontSize = 11, FontWeight = FontWeights.SemiBold },
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(chip);
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (subtitle.Length > 0) head.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var inside = new StackPanel { Spacing = 8 };
        inside.Children.Add(head);
        inside.Children.Add(body);
        return new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(tone == "final" ? 45 : 24), r, g, b)),
            Child = inside,
        };
    }

    private void AddCard(Border card)
    {
        _cells.Children.Add(new TextBlock { Text = "↓", Opacity = 0.45, Margin = new Thickness(22, 0, 0, 0) });
        _cells.Children.Add(card);
        card.StartBringIntoView();
    }

    private void AddCell(Cell c)
    {
        var body = new StackPanel { Spacing = 6 };
        foreach (var (label, text) in c.Parts)
        {
            body.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.6, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        AddCard(Frame(c.Kind, c.Title, c.Subtitle, c.Tone, body));
    }

    /// <summary>A cell with the AIs' questions and a box for each answer. The discussion waits until the person answers or lets the AIs decide.</summary>
    private Task<List<string>?> AskUserAsync(List<string> questions, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<List<string>?>();
        var boxes = new List<TextBox>();
        var body = new StackPanel { Spacing = 8 };
        foreach (var q in questions)
        {
            body.Children.Add(new TextBlock { Text = q, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true });
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, PlaceholderText = "Your answer" };
            boxes.Add(box);
            body.Children.Add(box);
        }
        var send = new Button { Content = "Send my answers" };
        var skip = new Button { Content = "Let the AIs decide" };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style accent) send.Style = accent;
        void Done(List<string>? answers)
        {
            send.IsEnabled = skip.IsEnabled = false;
            foreach (var b in boxes) b.IsReadOnly = true;
            if (_debate != null)
            {
                _debate.Log.AppendLine("## Question for the user");
                for (var i = 0; i < questions.Count; i++) _debate.Log.AppendLine($"- {questions[i]}  \n  Answer: {(answers != null && i < answers.Count && answers[i].Trim().Length > 0 ? answers[i].Trim() : "(none)")}");
                _debate.Log.AppendLine();
            }
            tcs.TrySetResult(answers);
        }
        send.Click += (_, _) => Done(boxes.Select(b => b.Text).ToList());
        skip.Click += (_, _) => Done(null);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { send, skip } });
        AddCard(Frame("Question", "The AIs need your answer to go on", "", "question", body));
        ct.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }
}
