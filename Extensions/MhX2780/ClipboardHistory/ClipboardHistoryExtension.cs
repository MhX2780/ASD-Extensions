using System.Text.Json;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace ClipboardHistory;

public sealed class ClipboardHistoryExtension : IAsdExtension
{
    public string Id => "MhX2780.ClipboardHistory";

    public void Register(IAsdHost host)
    {
        ClipStore.Start(host.DataFolder);   // listens from the start of ASD, not only while the page is open
        host.AddPage(new ExtensionPage { Tag = "clipboard", Title = "Clipboard History", Glyph = "", Create = () => new ClipView() });
    }
}

internal sealed class ClipItem
{
    public string Text = "";
    public DateTime Time;
    public bool Pinned;
}

/// <summary>The copied texts, newest first. They live in memory; only the pinned ones are written to pinned.json.</summary>
internal static class ClipStore
{
    public static readonly List<ClipItem> Items = new();
    public static event Action? Changed;
    public static bool Paused { get; set; }

    private static string _file = "";
    private static bool _started;
    private static Microsoft.UI.Dispatching.DispatcherQueue? _queue;

    public static void Start(string folder)
    {
        if (_started) return;
        _started = true;
        _file = System.IO.Path.Combine(folder, "pinned.json");
        _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            if (System.IO.File.Exists(_file))
                foreach (var text in JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(_file)) ?? new List<string>())
                    Items.Add(new ClipItem { Text = text, Time = DateTime.Now, Pinned = true });
        }
        catch { }
        try { Clipboard.ContentChanged += (_, _) => _queue?.TryEnqueue(() => _ = CaptureAsync()); }
        catch { }
    }

    private static async Task CaptureAsync()
    {
        if (Paused) return;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;
            if (view.Contains("ExcludeClipboardContentFromMonitorProcessing")) return;   // password managers mark their copies like this
            var text = await view.GetTextAsync();
            Add(text);
        }
        catch { /* the clipboard was busy or not readable: skip this one */ }
    }

    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200_000) return;
        var existing = Items.FirstOrDefault(i => i.Text == text);
        var pinned = existing?.Pinned ?? false;
        if (existing != null) Items.Remove(existing);
        Items.Insert(0, new ClipItem { Text = text, Time = DateTime.Now, Pinned = pinned });
        foreach (var old in Items.Where(i => !i.Pinned).Skip(100).ToList()) Items.Remove(old);
        Changed?.Invoke();
    }

    private static void SavePinned()
    {
        try { System.IO.File.WriteAllText(_file, JsonSerializer.Serialize(Items.Where(i => i.Pinned).Select(i => i.Text).ToList())); }
        catch { }
    }

    public static void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        SavePinned();
        Changed?.Invoke();
    }

    public static void Remove(ClipItem item)
    {
        Items.Remove(item);
        if (item.Pinned) SavePinned();
        Changed?.Invoke();
    }

    public static void ClearUnpinned()
    {
        Items.RemoveAll(i => !i.Pinned);
        Changed?.Invoke();
    }

    public static void CopyBack(ClipItem item)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(item.Text);
            Clipboard.SetContent(package);
        }
        catch { }
    }
}

/// <summary>Search, copy again, pin, delete.</summary>
internal sealed class ClipView : StackPanel
{
    private readonly TextBox _search = new() { PlaceholderText = "Search the copied texts", MinWidth = 300 };
    private readonly ToggleButton _pause = new() { Content = "Pause" };
    private readonly Button _clear = new() { Content = "Clear (keeps pinned)" };
    private readonly TextBlock _note = new() { Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, Height = 440 };

    public ClipView()
    {
        Spacing = 10;
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _search, _pause, _clear } });
        Children.Add(_note);
        Children.Add(_list);
        _pause.IsChecked = ClipStore.Paused;
        _pause.Click += (_, _) => ClipStore.Paused = _pause.IsChecked == true;
        _clear.Click += (_, _) => ClipStore.ClearUnpinned();
        _search.TextChanged += (_, _) => Render();
        Loaded += (_, _) => { ClipStore.Changed += OnChanged; Render(); };
        Unloaded += (_, _) => ClipStore.Changed -= OnChanged;
    }

    private void OnChanged() => Render();

    private void Render()
    {
        _list.Items.Clear();
        var q = _search.Text.Trim();
        var shown = ClipStore.Items
            .Where(i => q.Length == 0 || i.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Pinned)
            .ToList();
        foreach (var item in shown) _list.Items.Add(MakeRow(item));
        _note.Text = ClipStore.Items.Count == 0
            ? "Nothing yet. Copy some text anywhere and it appears here (while ASD is running). Pinned items are kept after ASD closes; the rest is only in memory."
            : shown.Count + " of " + ClipStore.Items.Count + " shown. Pinned items are kept after ASD closes; the rest is only in memory.";
    }

    private static FrameworkElement MakeRow(ClipItem item)
    {
        var preview = item.Text.Replace("\r\n", " / ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 220) preview = preview[..220] + "...";
        var text = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        var lines = item.Text.Count(c => c == '\n') + 1;
        var info = new TextBlock
        {
            Text = (item.Pinned ? "Pinned  -  " : "") + item.Time.ToString("HH:mm") + "  -  " + item.Text.Length + " characters" + (lines > 1 ? ", " + lines + " lines" : ""),
            Opacity = 0.6, FontSize = 12,
        };
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { text, info } };

        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => ClipStore.CopyBack(item);
        var pin = new Button { Content = item.Pinned ? "Unpin" : "Pin" };
        pin.Click += (_, _) => ClipStore.TogglePin(item);
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) => ClipStore.Remove(item);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { copy, pin, delete } };

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 4, 4, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        row.Children.Add(left);
        row.Children.Add(buttons);
        return row;
    }
}
