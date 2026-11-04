using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Checklist;

public sealed class ChecklistExtension : IAsdExtension
{
    public string Id => "MhX2780.Checklist";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "checklist", Title = "Checklist", Glyph = "", Create = () => new ChecklistView(host.DataFolder) });
    }
}

/// <summary>A to-do list saved as todo.txt in the extension's data folder ("[x] text" or "[ ] text" per line).</summary>
internal sealed class ChecklistView : StackPanel
{
    private sealed class Item { public bool Done; public string Text = ""; }

    private readonly string _file;
    private readonly List<Item> _items = new();
    private readonly TextBox _new = new() { PlaceholderText = "Add something to do", MinWidth = 320 };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly TextBlock _left = new() { Opacity = 0.7 };

    public ChecklistView(string folder)
    {
        _file = System.IO.Path.Combine(folder, "todo.txt");
        Spacing = 12;

        var add = new Button { Content = "Add" };
        var clear = new Button { Content = "Remove finished" };
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _new, add, clear } });
        Children.Add(_rows);
        Children.Add(_left);

        add.Click += (_, _) => Add();
        clear.Click += (_, _) => { _items.RemoveAll(i => i.Done); Save(); Render(); };
        _new.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Add(); };

        Load();
        Render();
    }

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(_file)) return;
            foreach (var line in System.IO.File.ReadAllLines(_file))
            {
                if (line.Length < 4) continue;
                _items.Add(new Item { Done = line.StartsWith("[x]"), Text = line[3..].TrimStart() });
            }
        }
        catch { /* start with an empty list */ }
    }

    private void Save()
    {
        try { System.IO.File.WriteAllLines(_file, _items.Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text)); }
        catch { /* the list still works until ASD closes */ }
    }

    private void Add()
    {
        var text = _new.Text.Trim();
        if (text.Length == 0) return;
        _items.Add(new Item { Text = text });
        _new.Text = "";
        Save();
        Render();
    }

    private void Render()
    {
        _rows.Children.Clear();
        foreach (var item in _items)
        {
            var it = item;
            var box = new CheckBox { Content = it.Text, IsChecked = it.Done, MinWidth = 300 };
            box.Checked += (_, _) => { it.Done = true; Save(); Count(); };
            box.Unchecked += (_, _) => { it.Done = false; Save(); Count(); };
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8, 4, 8, 4) };
            ToolTipService.SetToolTip(remove, "Remove");
            remove.Click += (_, _) => { _items.Remove(it); Save(); Render(); };
            _rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, remove } });
        }
        Count();
    }

    private void Count()
    {
        var open = _items.Count(i => !i.Done);
        _left.Text = _items.Count == 0 ? "Nothing to do yet." : open == 0 ? "All done." : open + (open == 1 ? " item left" : " items left");
    }
}
