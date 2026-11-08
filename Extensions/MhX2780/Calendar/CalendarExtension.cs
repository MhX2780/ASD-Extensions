using System.Globalization;
using System.Text.Json;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CalendarTool;

public sealed class CalendarExtension : IAsdExtension
{
    public string Id => "MhX2780.Calendar";

    public void Register(IAsdHost host)
    {
        Store.Host = host;
        Store.Load();
        Store.StartReminders();   // keeps checking while the person uses other tools
        host.AddPage(new ExtensionPage { Tag = "calendar", Title = "Calendar", Glyph = "", Create = () => new MonthView() });
    }
}

internal sealed class CalEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Date { get; set; } = "";    // yyyy-MM-dd
    public string Time { get; set; } = "";    // HH:mm, or empty for the whole day
    public string Title { get; set; } = "";
    public bool Notified { get; set; }
}

/// <summary>The events, saved as events.json in the extension's data folder, and the reminder timer.</summary>
internal static class Store
{
    public static IAsdHost? Host;
    public static List<CalEvent> Events = new();
    public static event Action? Changed;
    private static DispatcherTimer? _timer;

    private static string FilePath => Path.Combine(Host!.DataFolder, "events.json");

    public static void Load()
    {
        try { if (File.Exists(FilePath)) Events = JsonSerializer.Deserialize<List<CalEvent>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { Events = new(); }
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Events, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        Changed?.Invoke();
    }

    public static void StartReminders()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    private static void Check()
    {
        var now = DateTime.Now;
        var changed = false;
        foreach (var x in Events)
        {
            if (x.Notified || x.Time.Length == 0) continue;
            if (!DateTime.TryParseExact(x.Date + " " + x.Time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)) continue;
            if (now < due || now - due > TimeSpan.FromHours(12)) continue;
            Host?.Notify("Calendar", x.Title + " (" + x.Time + ")");
            x.Notified = true;
            changed = true;
        }
        if (changed) Save();
    }
}

/// <summary>The month on the left; the events of the chosen day (and what comes next) on the right.</summary>
internal sealed class MonthView : Grid
{
    private readonly CalendarView _cal = new() { SelectionMode = CalendarViewSelectionMode.Single, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _day = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _upcoming = new() { Spacing = 4 };
    private readonly TextBox _title = new() { PlaceholderText = "New event for this day", MinWidth = 240 };
    private readonly CheckBox _timed = new() { Content = "At", MinWidth = 0 };
    private readonly TimePicker _time = new() { ClockIdentifier = "24HourClock", IsEnabled = false, Time = new TimeSpan(9, 0, 0) };
    private readonly Button _add = new() { Content = "Add" };
    private bool _loading;

    public MonthView()
    {
        ColumnSpacing = 28;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var today = new Button { Content = "Today" };
        var left = new StackPanel { Spacing = 10, Children = { _cal, today } };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _title, _timed, _time, _add } };
        var soon = new TextBlock { Text = "Coming up", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Spacing = 14, Children = { _day, _list, addRow, soon, _upcoming } };
        SetColumn(right, 1);
        Children.Add(left);
        Children.Add(right);

        _timed.Checked += (_, _) => _time.IsEnabled = true;
        _timed.Unchecked += (_, _) => _time.IsEnabled = false;
        today.Click += (_, _) => { Select(DateTime.Today); _cal.SetDisplayDate(DateTime.Today); };
        _add.Click += (_, _) => AddEvent();
        _title.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) AddEvent(); };
        _cal.SelectedDatesChanged += (_, _) => { if (!_loading) Refresh(); };
        Loaded += (_, _) => { Store.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => Store.Changed -= Refresh;
        Select(DateTime.Today);
    }

    private DateTime Selected() => _cal.SelectedDates.Count > 0 ? _cal.SelectedDates[0].Date : DateTime.Today;

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Select(DateTime date)
    {
        _loading = true;
        _cal.SelectedDates.Clear();
        _cal.SelectedDates.Add(date);
        _loading = false;
        Refresh();
    }

    private void AddEvent()
    {
        var title = _title.Text.Trim();
        if (title.Length == 0) return;
        Store.Events.Add(new CalEvent
        {
            Date = Key(Selected()),
            Time = _timed.IsChecked == true ? _time.Time.ToString(@"hh\:mm") : "",
            Title = title,
        });
        _title.Text = "";
        Store.Save();
    }

    private static string When(CalEvent x)
    {
        var day = DateTime.TryParseExact(x.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("ddd d MMM") : x.Date;
        return x.Time.Length > 0 ? day + " " + x.Time : day;
    }

    private void Refresh()
    {
        var date = Selected();
        var key = Key(date);
        _day.Text = date.ToString("dddd, d MMMM yyyy");

        _list.Children.Clear();
        var today = Store.Events.Where(x => x.Date == key).OrderBy(x => x.Time).ToList();
        if (today.Count == 0) _list.Children.Add(new TextBlock { Text = "Nothing planned.", Opacity = 0.65 });
        foreach (var ev in today) _list.Children.Add(Row(ev));

        _upcoming.Children.Clear();
        var from = Key(DateTime.Today);
        foreach (var ev in Store.Events.Where(x => string.CompareOrdinal(x.Date, from) >= 0).OrderBy(x => x.Date).ThenBy(x => x.Time).Take(8))
            _upcoming.Children.Add(new TextBlock { Text = When(ev) + "   " + ev.Title, Opacity = 0.8 });
        if (_upcoming.Children.Count == 0) _upcoming.Children.Add(new TextBlock { Text = "No upcoming events.", Opacity = 0.65 });
    }

    private UIElement Row(CalEvent ev)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var time = new TextBlock { Text = ev.Time.Length > 0 ? ev.Time : "all day", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = ev.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 } };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(delete, "Delete");
        delete.Click += (_, _) => { Store.Events.Remove(ev); Store.Save(); };
        SetColumn(title, 1);
        SetColumn(delete, 2);
        grid.Children.Add(time);
        grid.Children.Add(title);
        grid.Children.Add(delete);
        return grid;
    }
}
