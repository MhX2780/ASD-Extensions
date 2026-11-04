using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuickNotes;

public sealed class QuickNotesExtension : IAsdExtension
{
    public string Id => "MhX2780.QuickNotes";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "notes", Title = "Quick Notes", Glyph = "", Create = () => new NotesView(host.DataFolder) });
    }
}

/// <summary>Every note is a .txt file in the extension's data folder; the text saves itself a moment after you stop typing.</summary>
internal sealed class NotesView : StackPanel
{
    private readonly string _folder;
    private readonly ComboBox _list = new() { Header = "Note", MinWidth = 260 };
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 380, IsSpellCheckEnabled = true };
    private readonly TextBlock _status = new() { Opacity = 0.65, FontSize = 12 };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private string? _current;
    private bool _loading;

    public NotesView(string folder)
    {
        _folder = folder;
        Spacing = 12;

        var add = new Button { Content = "New note" };
        var delete = new Button { Content = "Delete" };
        var confirm = new Button { Content = "Yes, delete it" };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Delete this note?" }, confirm } } };
        delete.Flyout = flyout;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Children = { add, delete } };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _list, bar } };
        Children.Add(top);
        Children.Add(_editor);
        Children.Add(_status);

        add.Click += (_, _) => { Flush(); Create(); };
        confirm.Click += (_, _) => { flyout.Hide(); Remove(); };
        _list.SelectionChanged += (_, _) => { if (!_loading) { Flush(); Open(_list.SelectedItem as string); } };
        _editor.TextChanged += (_, _) => { if (_loading) return; _status.Text = "Typing…"; _save.Stop(); _save.Start(); };
        _save.Tick += (_, _) => { _save.Stop(); Flush(); };
        Unloaded += (_, _) => { _save.Stop(); Flush(); };

        Reload(null);
        if (_list.Items.Count == 0) Create();
    }

    private string PathOf(string name) => System.IO.Path.Combine(_folder, name + ".txt");

    private void Reload(string? select)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var file in System.IO.Directory.GetFiles(_folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(System.IO.Path.GetFileNameWithoutExtension(file));
        if (_list.Items.Count > 0) _list.SelectedItem = select != null && _list.Items.Contains(select) ? select : _list.Items[0];
        _loading = false;
        Open(_list.SelectedItem as string);
    }

    private void Open(string? name)
    {
        _loading = true;
        _current = name;
        _editor.IsEnabled = name != null;
        _editor.Text = name != null && System.IO.File.Exists(PathOf(name)) ? System.IO.File.ReadAllText(PathOf(name)) : "";
        _loading = false;
        _status.Text = name != null ? "Saved automatically in " + _folder : "";
    }

    private void Flush()
    {
        if (_current == null) return;
        try { System.IO.File.WriteAllText(PathOf(_current), _editor.Text); _status.Text = "Saved"; }
        catch (Exception ex) { _status.Text = "Could not save: " + ex.Message; }
    }

    private void Create()
    {
        var n = 1;
        while (System.IO.File.Exists(PathOf("Note " + n))) n++;
        var name = "Note " + n;
        System.IO.File.WriteAllText(PathOf(name), "");
        Reload(name);
    }

    private void Remove()
    {
        if (_current == null) return;
        try { System.IO.File.Delete(PathOf(_current)); } catch { }
        _current = null;
        Reload(null);
        if (_list.Items.Count == 0) Create();
    }
}
