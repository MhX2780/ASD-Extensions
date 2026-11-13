using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppStore;

/// <summary>
/// The window shown while winget installs, updates or removes an app: a big percentage, a bar, a line of detail and a Cancel button.
/// While the amount is not known (the installer's own phase) the bar runs back and forth and the percentage shows three dots.
/// </summary>
internal sealed class ProgressDialog
{
    private readonly ContentDialog _dialog;
    private readonly TextBlock _percent = new()
    {
        Text = "...", FontSize = 64, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly TextBlock _detail = new()
    {
        Text = "Starting...", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8,
    };
    private bool _finished;

    public ProgressDialog(XamlRoot root, string title, Action cancel)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 380, Children = { _percent, _bar, _detail } };
        _dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
        };
        try { if (root.Content is FrameworkElement element) _dialog.RequestedTheme = element.ActualTheme; } catch { }
        // closed by the person (Cancel or Esc) and not by us: stop winget
        _dialog.Closed += (_, _) => { if (!_finished) cancel(); };
    }

    public void Show()
    {
        try { _ = _dialog.ShowAsync(); }
        catch { /* another dialog is open: the progress is still shown at the bottom of the page */ }
    }

    /// <summary>The amount is known: 0..1.</summary>
    public void SetPercent(double fraction, string detail)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        _percent.Text = (int)Math.Floor(Math.Clamp(fraction, 0, 1) * 100) + "%";
        _detail.Text = detail.EndsWith('%') ? "Downloading" : "Downloaded " + detail;
    }

    /// <summary>No amount is known (starting, verifying, the installer's own work).</summary>
    public void SetWorking(string detail)
    {
        _bar.IsIndeterminate = true;
        _percent.Text = "...";
        _detail.Text = detail;
    }

    public void Close()
    {
        _finished = true;
        try { _dialog.Hide(); } catch { }
    }
}
