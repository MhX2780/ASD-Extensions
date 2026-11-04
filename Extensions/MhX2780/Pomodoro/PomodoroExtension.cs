using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Pomodoro;

public sealed class PomodoroExtension : IAsdExtension
{
    public string Id => "MhX2780.Pomodoro";

    public void Register(IAsdHost host)
    {
        Clock.Host = host;
        host.AddPage(new ExtensionPage { Tag = "pomodoro", Title = "Pomodoro", Glyph = "", Create = () => new PomodoroView() });
    }
}

/// <summary>The timer lives here, not in the page: it keeps counting when the person opens another tool and still notifies.</summary>
internal static class Clock
{
    public static IAsdHost? Host;
    public static event Action? Changed;

    public static bool IsWork = true;
    public static bool Running;
    public static int Done;
    public static double WorkMinutes = 25, BreakMinutes = 5;
    public static TimeSpan Left = TimeSpan.FromMinutes(25);

    private static DispatcherTimer? _timer;
    private static DateTime _end;

    private static void EnsureTimer()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
    }

    public static void Start()
    {
        EnsureTimer();
        _end = DateTime.UtcNow + Left;
        Running = true;
        _timer!.Start();
        Changed?.Invoke();
    }

    public static void Pause()
    {
        Left = _end - DateTime.UtcNow;
        if (Left < TimeSpan.Zero) Left = TimeSpan.Zero;
        Running = false;
        _timer?.Stop();
        Changed?.Invoke();
    }

    public static void Reset()
    {
        _timer?.Stop();
        Running = false;
        IsWork = true;
        Left = TimeSpan.FromMinutes(WorkMinutes);
        Changed?.Invoke();
    }

    /// <summary>The length of the current kind of session was changed while the timer is stopped.</summary>
    public static void Resize()
    {
        if (Running) return;
        Left = TimeSpan.FromMinutes(IsWork ? WorkMinutes : BreakMinutes);
        Changed?.Invoke();
    }

    private static void Tick()
    {
        var left = _end - DateTime.UtcNow;
        if (left > TimeSpan.Zero) { Left = left; Changed?.Invoke(); return; }

        _timer?.Stop();
        Running = false;
        if (IsWork) { Done++; Host?.Notify("Pomodoro", "Work session finished. Time for a break."); }
        else Host?.Notify("Pomodoro", "The break is over. Ready to work?");
        IsWork = !IsWork;
        Left = TimeSpan.FromMinutes(IsWork ? WorkMinutes : BreakMinutes);
        Changed?.Invoke();
    }
}

internal sealed class PomodoroView : StackPanel
{
    private readonly TextBlock _mode = new() { FontSize = 16, Opacity = 0.7 };
    private readonly TextBlock _time = new() { FontSize = 72, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _count = new() { Opacity = 0.7 };
    private readonly Button _start = new() { MinWidth = 100 };
    private readonly Button _reset = new() { Content = "Reset" };
    private readonly NumberBox _work = new() { Header = "Work (minutes)", Minimum = 1, Maximum = 180, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 190 };
    private readonly NumberBox _break = new() { Header = "Break (minutes)", Minimum = 1, Maximum = 60, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 190 };

    public PomodoroView()
    {
        Spacing = 14;
        _work.Value = Clock.WorkMinutes;
        _break.Value = Clock.BreakMinutes;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _reset } };
        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _work, _break } };
        Children.Add(_mode);
        Children.Add(_time);
        Children.Add(buttons);
        Children.Add(settings);
        Children.Add(_count);

        _start.Click += (_, _) => { if (Clock.Running) Clock.Pause(); else Clock.Start(); };
        _reset.Click += (_, _) => Clock.Reset();
        _work.ValueChanged += (_, _) => { if (!double.IsNaN(_work.Value)) { Clock.WorkMinutes = _work.Value; Clock.Resize(); } };
        _break.ValueChanged += (_, _) => { if (!double.IsNaN(_break.Value)) { Clock.BreakMinutes = _break.Value; Clock.Resize(); } };

        Loaded += (_, _) => { Clock.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => Clock.Changed -= Refresh;
    }

    private void Refresh()
    {
        var seconds = (int)Math.Ceiling(Clock.Left.TotalSeconds);
        _time.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        _mode.Text = Clock.IsWork ? "Work" : "Break";
        _start.Content = Clock.Running ? "Pause" : "Start";
        _count.Text = "Work sessions finished: " + Clock.Done;
    }
}
