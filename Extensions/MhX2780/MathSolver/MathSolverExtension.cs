using System.Text;
using System.Text.RegularExpressions;
using ASD.Extensibility;
using AsdAi;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace MathSolver;

public sealed class MathSolverExtension : IAsdExtension
{
    public string Id => "MhX2780.MathSolver";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "mathsolver", Title = "Math Solver (AI)", Glyph = "", Create = () => new MathPage(host.DataFolder) });
    }
}

internal sealed record Solution(string Type, string Given, List<(string Title, string Work)> Steps, string Final, string Check, string Raw);

/// <summary>Type a problem, press Solve: the AI answers with numbered steps and a final answer; a second model can check it.</summary>
internal sealed class MathPage : StackPanel
{
    private const string System1 =
        "You are a careful maths tutor. Solve the problem step by step and double-check every calculation. " +
        "Write maths in plain text (x², √, ×, ÷, a/b, π, ≤, ≥, ≠), never LaTeX and never markdown tables. " +
        "Reply in the language of the problem.";

    private readonly string _folder;
    private readonly List<AiModel> _models;
    private readonly ComboBox _model = new() { Header = "AI model", MinWidth = 280 };
    private readonly ComboBox _detail = new() { Header = "Steps", MinWidth = 150 };
    private readonly CheckBox _verify = new() { Content = "Check the answer with another model", IsChecked = true };
    private readonly TextBox _input = new() { Header = "The problem", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, PlaceholderText = "For example:  2x² − 5x + 3 = 0     or     the derivative of x³ sin(x)" };
    private readonly Button _solve = new() { Content = "Solve", Padding = new Thickness(22, 8, 22, 8) };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _copy = new() { Content = "Copy the solution", IsEnabled = false };
    private readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _result = new() { Spacing = 8 };
    private CancellationTokenSource? _cancel;
    private string _lastText = "";

    public MathPage(string folder)
    {
        _folder = folder;
        _models = AiModels.Load(folder);
        Spacing = 12;
        foreach (var d in new[] { "Normal", "Short", "Very detailed" }) _detail.Items.Add(d);
        _detail.SelectedIndex = 0;
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style accent) _solve.Style = accent;

        var manage = new Button { Content = "AI models..." };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom };
        top.Children.Add(_model);
        top.Children.Add(_detail);
        top.Children.Add(manage);

        var symbols = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var sym in new[] { "²", "³", "√", "π", "∫", "∑", "≤", "≥", "≠", "±", "×", "÷", "∞" })
        {
            var b = new Button { Content = sym, MinWidth = 36, Padding = new Thickness(6, 4, 6, 4) };
            b.Click += (_, _) =>
            {
                var at = _input.SelectionStart;
                _input.Text = _input.Text.Insert(at, sym);
                _input.SelectionStart = at + sym.Length;
                _input.Focus(FocusState.Programmatic);
            };
            symbols.Children.Add(b);
        }

        Children.Add(new TextBlock { Text = "Type a maths problem and get the solution step by step. You choose the AI model; add one with your own API key under 'AI models'.", TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        Children.Add(top);
        Children.Add(symbols);
        Children.Add(_input);
        Children.Add(_verify);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _solve, _stop, _copy } });
        Children.Add(_status);
        Children.Add(_result);

        RefreshModels();
        manage.Click += async (_, _) =>
        {
            await AiModelsDialog.ShowAsync(XamlRoot, _folder, _models, withRole: false);
            RefreshModels();
        };
        _solve.Click += async (_, _) => await SolveAsync();
        _stop.Click += (_, _) => _cancel?.Cancel();
        _copy.Click += (_, _) =>
        {
            try { var p = new DataPackage(); p.SetText(_lastText); Clipboard.SetContent(p); _status.Text = "Copied."; }
            catch (Exception ex) { _status.Text = "Could not copy: " + ex.Message; }
        };
        Unloaded += (_, _) => _cancel?.Cancel();
    }

    private void RefreshModels()
    {
        var keep = (_model.SelectedItem as ComboBoxItem)?.Tag as AiModel;
        _model.Items.Clear();
        foreach (var m in _models.Where(m => m.Enabled)) _model.Items.Add(new ComboBoxItem { Content = AiModels.Display(m), Tag = m });
        _model.SelectedIndex = _model.Items.Count == 0 ? -1 : Math.Max(0, keep == null ? 0 : _models.Where(m => m.Enabled).ToList().IndexOf(keep));
        _status.Text = _model.Items.Count == 0 ? "No AI model yet. Press 'AI models...' and add one with its API key." : "";
    }

    private AiModel? Chosen() => (_model.SelectedItem as ComboBoxItem)?.Tag as AiModel;

    private async Task SolveAsync()
    {
        var problem = _input.Text.Trim();
        var model = Chosen();
        if (model == null) { _status.Text = "Add an AI model first (AI models...)."; return; }
        if (problem.Length == 0) { _status.Text = "Type the problem first."; return; }
        var key = await AiModels.LoadKeyAsync(_folder, model);
        if (key == null) { _status.Text = "There is no API key for " + AiModels.Display(model) + ". Add it under 'AI models...'."; return; }

        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _solve.IsEnabled = false; _stop.IsEnabled = true; _copy.IsEnabled = false;
        _result.Children.Clear();
        _lastText = "";
        try
        {
            _status.Text = AiModels.Display(model) + " is solving...";
            var raw = await AiModels.AskAsync(model, key, System1, SolvePrompt(problem), 3000, cancel.Token);
            var solution = Parse(raw);
            Show(solution);
            var text = new StringBuilder("Problem: " + problem + "\n\n");
            foreach (var (title, work) in solution.Steps.Select(x => x)) text.AppendLine(title + "\n" + work + "\n");
            text.AppendLine("Final answer: " + solution.Final);
            _lastText = solution.Steps.Count == 0 && solution.Final.Length == 0 ? raw : text.ToString();
            _copy.IsEnabled = true;

            if (_verify.IsChecked == true) await VerifyAsync(problem, solution, model, cancel.Token);
            _status.Text = "Done.";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _solve.IsEnabled = true; _stop.IsEnabled = false; }
    }

    private string SolvePrompt(string problem)
    {
        var level = _detail.SelectedIndex switch
        {
            1 => "Keep every step short: one or two lines.",
            2 => "Explain every step in detail, name the rule or formula used and why it applies.",
            _ => "Explain each step clearly but briefly.",
        };
        return "=== PROBLEM ===\n" + problem + "\n\n" + level + "\nAnswer in exactly this format (the labels in English):\n" +
               "PROBLEM TYPE: a few words\nGIVEN: what is given and what is asked\nSTEP 1: short title of the step\nthe work and the result of this step\nSTEP 2: ...\n...\n" +
               "FINAL ANSWER: the answer\nCHECK: a quick check, for example putting the answer back in the problem";
    }

    private async Task VerifyAsync(string problem, Solution solution, AiModel solver, CancellationToken ct)
    {
        var other = _models.FirstOrDefault(m => m.Enabled && m.Id != solver.Id) ?? solver;
        var key = await AiModels.LoadKeyAsync(_folder, other);
        if (key == null) { _result.Children.Add(Note("Not checked: there is no key for " + AiModels.Display(other) + ".", false)); return; }
        _status.Text = AiModels.Display(other) + " is checking the answer...";
        var prompt = "=== PROBLEM ===\n" + problem + "\n\n=== PROPOSED FINAL ANSWER ===\n" + solution.Final +
                     "\n\nSolve the problem yourself first, without trusting the proposed answer. Then reply exactly:\nMY ANSWER: your own final answer\nVERDICT: CORRECT or WRONG\nWHY: one or two sentences";
        try
        {
            var raw = await AiModels.AskAsync(other, key, System1, prompt, 1500, ct);
            var parts = AiText.Sections(raw, "MY ANSWER", "VERDICT", "WHY");
            var verdict = parts.GetValueOrDefault("VERDICT") ?? "";
            var ok = verdict.StartsWith("CORRECT", StringComparison.OrdinalIgnoreCase);
            var why = parts.GetValueOrDefault("WHY") ?? raw;
            _result.Children.Add(Note(ok
                ? $"✓ Checked by {AiModels.Display(other)}: the answer is correct. {why}"
                : $"✗ {AiModels.Display(other)} does not agree. Its answer: {parts.GetValueOrDefault("MY ANSWER") ?? "?"}. {why}", ok));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _result.Children.Add(Note("Not checked: " + ex.Message, false)); }
    }

    private static Border Card(UIElement child, byte r = 128, byte g = 128, byte b = 128, byte alpha = 28)
        => new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, r, g, b)), Child = child };

    private static Border Note(string text, bool good)
        => good ? Card(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, 34, 160, 75, 40)
                : Card(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, 245, 158, 11, 45);

    private void Show(Solution s)
    {
        if (s.Steps.Count == 0 && s.Final.Length == 0)
        {
            _result.Children.Add(Card(new TextBlock { Text = s.Raw, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }));
            return;
        }
        var head = new StringBuilder();
        if (s.Type.Length > 0) head.Append(s.Type);
        if (s.Given.Length > 0) head.Append((head.Length > 0 ? "\n" : "") + s.Given);
        if (head.Length > 0) _result.Children.Add(new TextBlock { Text = head.ToString(), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, IsTextSelectionEnabled = true });

        var n = 0;
        foreach (var (title, work) in s.Steps)
        {
            n++;
            var number = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)),
                Child = new TextBlock { Text = n.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold },
            };
            var body = new StackPanel { Spacing = 3 };
            if (title.Length > 0) body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (work.Length > 0) body.Children.Add(new TextBlock { Text = work, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(body, 1);
            row.Children.Add(number);
            row.Children.Add(body);
            _result.Children.Add(Card(row));
        }

        if (s.Final.Length > 0)
        {
            var box = new StackPanel { Spacing = 3 };
            box.Children.Add(new TextBlock { Text = "Final answer", Opacity = 0.7, FontSize = 12 });
            box.Children.Add(new TextBlock { Text = s.Final, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            _result.Children.Add(Card(box, 34, 160, 75, 45));
        }
        if (s.Check.Length > 0) _result.Children.Add(new TextBlock { Text = "Check: " + s.Check, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, IsTextSelectionEnabled = true });
    }

    private static readonly Regex Header = new(@"^\s*[*#]*\s*(PROBLEM TYPE|GIVEN|FINAL ANSWER|CHECK|STEP\s*\d+)\s*\**\s*:\s*\**\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Solution Parse(string raw)
    {
        string type = "", given = "", final = "", check = "";
        var steps = new List<(string Title, string Work)>();
        string kind = "";
        var buffer = new StringBuilder();
        var title = "";

        void Flush()
        {
            var text = buffer.ToString().Replace("**", "").Trim();
            switch (kind)
            {
                case "TYPE": type = text; break;
                case "GIVEN": given = text; break;
                case "FINAL": final = text; break;
                case "CHECK": check = text; break;
                case "STEP": steps.Add((title, text)); break;
            }
            buffer.Clear();
        }

        foreach (var line in raw.Replace("\r", "").Split('\n'))
        {
            var m = Header.Match(line);
            if (m.Success)
            {
                Flush();
                var label = m.Groups[1].Value.ToUpperInvariant();
                kind = label.StartsWith("STEP") ? "STEP" : label.StartsWith("PROBLEM") ? "TYPE" : label.StartsWith("GIVEN") ? "GIVEN" : label.StartsWith("FINAL") ? "FINAL" : "CHECK";
                var rest = m.Groups[2].Value.Replace("**", "").Trim();
                if (kind == "STEP") title = rest; else buffer.AppendLine(rest);
            }
            else if (kind.Length > 0) buffer.AppendLine(line);
        }
        Flush();
        return new Solution(type, given, steps, final, check, raw.Trim());
    }
}
