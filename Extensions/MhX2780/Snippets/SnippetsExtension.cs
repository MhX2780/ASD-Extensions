using System.Text.Json;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Snippets;

public sealed class SnippetsExtension : IAsdExtension
{
    public string Id => "MhX2780.Snippets";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "snippets", Title = "Snippets", Glyph = "\uE70F", Create = () => new SnippetsView(host.DataFolder) });
    }
}

public sealed class Snippet
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>A list of snippets on the left, the selected one on the right. Everything is saved to snippets.json a moment after you stop typing.</summary>
internal sealed class SnippetsView : Grid
{
    private readonly string _file;
    private readonly List<Snippet> _items = new();
    private Snippet? _current;
    private bool _loading;

    private readonly TextBox _search = new() { PlaceholderText = "Search" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, Height = 470 };
    private readonly Button _new = new() { Content = "New snippet" };
    private readonly TextBox _title = new() { Header = "Title", MinWidth = 300 };
    private readonly TextBox _language = new() { Header = "Language or tag", Width = 200 };
    private readonly TextBox _code = new() { Header = "Code", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas"), Height = 360 };
    private readonly Button _copy = new() { Content = "Copy the code" };
    private readonly Button _delete = new() { Content = "Delete" };
    private readonly TextBlock _status = new() { Opacity = 0.65, FontSize = 12 };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(800) };

    public SnippetsView(string folder)
    {
        _file = System.IO.Path.Combine(folder, "snippets.json");
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        ScrollViewer.SetVerticalScrollBarVisibility(_code, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_code, ScrollBarVisibility.Auto);

        var left = new StackPanel { Spacing = 8, Children = { _search, _list, _new } };
        var confirm = new Button { Content = "Yes, delete it" };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Delete this snippet?" }, confirm } } };
        _delete.Flyout = flyout;
        var right = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _title, _language } },
                _code,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _copy, _delete } },
                _status,
            },
        };
        Grid.SetColumn(right, 1);
        Children.Add(left);
        Children.Add(right);

        Load();
        Render();
        Open(_items.Count > 0 ? _items[0] : null);
        SelectCurrent();

        _search.TextChanged += (_, _) => Render();
        _new.Click += (_, _) => { Flush(); var s = NewSnippet("New snippet", "", ""); _items.Insert(0, s); Save(); _search.Text = ""; Render(); Open(s); SelectCurrent(); };
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Flush();
            Open(_list.SelectedItem is ListViewItem { Tag: Snippet s } ? s : null);
        };
        _title.TextChanged += (_, _) => Edited();
        _language.TextChanged += (_, _) => Edited();
        _code.TextChanged += (_, _) => Edited();
        _save.Tick += (_, _) => { _save.Stop(); Flush(); Render(); SelectCurrent(); };
        _copy.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(CodeText());
                Clipboard.SetContent(package);
                _status.Text = "Copied.";
            }
            catch (Exception ex) { _status.Text = "Could not copy: " + ex.Message; }
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            if (_current == null) return;
            _items.Remove(_current);
            _current = null;
            Save();
            Render();
            Open(_items.Count > 0 ? _items[0] : null);
            SelectCurrent();
        };
        Unloaded += (_, _) => { _save.Stop(); Flush(); };
    }

    private static Snippet NewSnippet(string title, string language, string code) =>
        new() { Id = Guid.NewGuid().ToString("N"), Title = title, Language = language, Code = code };

    private string CodeText() => _code.Text.Replace("\r\n", "\n").Replace('\r', '\n');

    private void Load()
    {
        try
        {
            if (System.IO.File.Exists(_file))
            {
                var items = JsonSerializer.Deserialize<List<Snippet>>(System.IO.File.ReadAllText(_file));
                if (items != null) { _items.AddRange(items); return; }
            }
        }
        catch { }
        _items.AddRange(new[]
        {
            NewSnippet("Read a text file", "Python", "with open('data.txt', encoding='utf-8') as f:\n    text = f.read()\nprint(text)\n"),
            NewSnippet("HTTP GET with requests", "Python", "import requests\n\nr = requests.get('https://example.com', timeout=10)\nr.raise_for_status()\nprint(r.status_code, len(r.text))\n"),
            NewSnippet("argparse skeleton", "Python", "import argparse\n\nparser = argparse.ArgumentParser()\nparser.add_argument('path')\nparser.add_argument('-v', '--verbose', action='store_true')\nargs = parser.parse_args()\nprint(args.path, args.verbose)\n"),
            NewSnippet("Dataclass", "Python", "from dataclasses import dataclass\n\n@dataclass\nclass Item:\n    name: str\n    price: float = 0.0\n"),
            NewSnippet("Undo the last commit, keep the changes", "Git", "git reset --soft HEAD~1"),
            NewSnippet("The 20 biggest files under this folder", "PowerShell", "Get-ChildItem -Recurse -File | Sort-Object Length -Descending | Select-Object -First 20 FullName, Length"),
            NewSnippet("Which program uses a port", "PowerShell", "Get-NetTCPConnection -LocalPort 8080 | Select-Object OwningProcess\nGet-Process -Id 1234"),
        });
        Save();
    }

    private void Save()
    {
        try { System.IO.File.WriteAllText(_file, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _status.Text = "Could not save: " + ex.Message; }
    }

    /// <summary>Takes what is typed into the current snippet and saves.</summary>
    private void Flush()
    {
        if (_current == null) return;
        _current.Title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : "Untitled";
        _current.Language = _language.Text.Trim();
        _current.Code = CodeText();
        Save();
        _status.Text = "Saved";
    }

    private void Edited()
    {
        if (_loading || _current == null) return;
        _status.Text = "Typing...";
        _save.Stop();
        _save.Start();
    }

    private void Open(Snippet? snippet)
    {
        _loading = true;
        _current = snippet;
        _title.IsEnabled = _language.IsEnabled = _code.IsEnabled = _copy.IsEnabled = _delete.IsEnabled = snippet != null;
        _title.Text = snippet?.Title ?? "";
        _language.Text = snippet?.Language ?? "";
        _code.Text = (snippet?.Code ?? "").Replace("\r\n", "\n").Replace('\n', '\r');
        _loading = false;
        _status.Text = "";
    }

    private void Render()
    {
        _loading = true;
        _list.Items.Clear();
        var q = _search.Text.Trim();
        foreach (var s in _items)
        {
            if (q.Length > 0 && !(s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Language.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Code.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(new TextBlock { Text = s.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            if (s.Language.Length > 0) content.Children.Add(new TextBlock { Text = s.Language, Opacity = 0.6, FontSize = 12 });
            _list.Items.Add(new ListViewItem { Content = content, Tag = s });
        }
        _loading = false;
    }

    private void SelectCurrent()
    {
        _loading = true;
        foreach (var item in _list.Items)
            if (item is ListViewItem { Tag: Snippet s } && ReferenceEquals(s, _current)) { _list.SelectedItem = item; break; }
        _loading = false;
    }
}
