using ASD.Extensibility;
using AsdShared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TextDiff;

public sealed class TextDiffExtension : IAsdExtension
{
    public string Id => "MhX2780.TextDiff";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "diff", Title = "Text Diff", Glyph = "", Create = () => new DiffView() });
    }
}

/// <summary>Two boxes (type, paste or open a file in each), a Compare button and the colored result.</summary>
internal sealed class DiffView : StackPanel
{
    private enum Op { Same, Add, Del }

    private static readonly SolidColorBrush AddBrush = new(Windows.UI.Color.FromArgb(70, 40, 180, 80));
    private static readonly SolidColorBrush DelBrush = new(Windows.UI.Color.FromArgb(70, 220, 60, 60));

    private readonly TextBox _left = MakeBox();
    private readonly TextBox _right = MakeBox();
    private readonly CheckBox _ignoreCase = new() { Content = "Ignore upper/lower case" };
    private readonly CheckBox _ignoreSpace = new() { Content = "Ignore spaces at the start and end of lines" };
    private readonly Button _compare = new() { Content = "Compare" };
    private readonly Button _swap = new() { Content = "Swap sides" };
    private readonly TextBlock _summary = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly ListView _result = new() { SelectionMode = ListViewSelectionMode.None, Height = 360 };

    private static TextBox MakeBox() => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false,
        FontFamily = new FontFamily("Consolas"), FontSize = 13, Height = 220,
    };

    public DiffView()
    {
        Spacing = 10;
        foreach (var box in new[] { _left, _right })
        {
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        }

        var sides = new Grid { ColumnSpacing = 12 };
        sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftSide = Side("Original", _left);
        var rightSide = Side("Changed", _right);
        Grid.SetColumn(rightSide, 1);
        sides.Children.Add(leftSide);
        sides.Children.Add(rightSide);

        Children.Add(sides);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _compare, _swap, _ignoreCase, _ignoreSpace } });
        Children.Add(_summary);
        Children.Add(_result);

        _compare.Click += async (_, _) => await CompareAsync();
        _swap.Click += (_, _) => { (_left.Text, _right.Text) = (_right.Text, _left.Text); };
    }

    private static StackPanel Side(string title, TextBox box)
    {
        var open = new Button { Content = "Open a file..." };
        open.Click += async (_, _) =>
        {
            var path = await Pick.FileAsync();
            if (path == null) return;
            try { box.Text = (await System.IO.File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Replace('\n', '\r'); }
            catch (Exception ex) { box.Text = "Could not read the file: " + ex.Message; }
        };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(open, 1);
        head.Children.Add(label);
        head.Children.Add(open);
        return new StackPanel { Spacing = 6, Children = { head, box } };
    }

    private static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private async Task CompareAsync()
    {
        var a = Lines(_left.Text);
        var b = Lines(_right.Text);
        var ignoreCase = _ignoreCase.IsChecked == true;
        var ignoreSpace = _ignoreSpace.IsChecked == true;
        Func<string, string> norm = s =>
        {
            if (ignoreSpace) s = s.Trim();
            if (ignoreCase) s = s.ToLowerInvariant();
            return s;
        };
        _compare.IsEnabled = false;
        _summary.Text = "Comparing...";
        var diff = await Task.Run(() => Compute(a, b, norm));
        _compare.IsEnabled = true;
        if (diff == null) { _summary.Text = "These texts are too long and too different to compare here."; _result.Items.Clear(); return; }
        Render(diff);
    }

    /// <summary>The longest common subsequence of the lines (the same lines at the start and the end are skipped first).</summary>
    private static List<(Op Op, string Text)>? Compute(string[] a, string[] b, Func<string, string> norm)
    {
        var ka = a.Select(norm).ToArray();
        var kb = b.Select(norm).ToArray();
        var start = 0;
        while (start < a.Length && start < b.Length && ka[start] == kb[start]) start++;
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && ka[endA - 1] == kb[endB - 1]) { endA--; endB--; }
        var n = endA - start;
        var m = endB - start;
        if ((long)n * m > 6_000_000) return null;

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = ka[start + i] == kb[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<(Op Op, string Text)>();
        for (var i = 0; i < start; i++) result.Add((Op.Same, a[i]));
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (ka[start + x] == kb[start + y]) { result.Add((Op.Same, a[start + x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { result.Add((Op.Del, a[start + x])); x++; }
            else { result.Add((Op.Add, b[start + y])); y++; }
        }
        while (x < n) { result.Add((Op.Del, a[start + x])); x++; }
        while (y < m) { result.Add((Op.Add, b[start + y])); y++; }
        for (var i = endA; i < a.Length; i++) result.Add((Op.Same, a[i]));
        return result;
    }

    private void Render(List<(Op Op, string Text)> diff)
    {
        _result.Items.Clear();
        var added = diff.Count(d => d.Op == Op.Add);
        var removed = diff.Count(d => d.Op == Op.Del);
        if (added + removed == 0) { _summary.Text = "The texts are the same."; return; }
        _summary.Text = added + " line(s) added, " + removed + " line(s) removed.";

        // the changed lines and 3 lines around each; the rest is folded
        var show = new bool[diff.Count];
        for (var i = 0; i < diff.Count; i++)
            if (diff[i].Op != Op.Same)
                for (var k = Math.Max(0, i - 3); k <= Math.Min(diff.Count - 1, i + 3); k++) show[k] = true;

        var hidden = 0;
        var rows = 0;
        for (var i = 0; i < diff.Count; i++)
        {
            if (!show[i]) { hidden++; continue; }
            if (hidden > 0) { _result.Items.Add(Row(Op.Same, "... " + hidden + " unchanged line(s) ...", true)); hidden = 0; }
            if (++rows > 2500) { _result.Items.Add(Row(Op.Same, "... the list is cut here ...", true)); return; }
            _result.Items.Add(Row(diff[i].Op, diff[i].Text, false));
        }
        if (hidden > 0) _result.Items.Add(Row(Op.Same, "... " + hidden + " unchanged line(s) ...", true));
    }

    private static FrameworkElement Row(Op op, string text, bool note)
    {
        var prefix = op switch { Op.Add => "+ ", Op.Del => "- ", _ => "  " };
        var block = new TextBlock
        {
            Text = note ? text : prefix + text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Opacity = note ? 0.55 : 1,
            TextWrapping = TextWrapping.NoWrap,
        };
        return new Border
        {
            Child = block,
            Padding = new Thickness(6, 1, 6, 1),
            Background = op == Op.Add ? AddBrush : op == Op.Del ? DelBrush : null,
        };
    }
}
