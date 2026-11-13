using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PyScript;

/// <summary>
/// The panel with Output, Problems, Logs and AI analysis. It sits beside the code by default (so the code keeps its full height),
/// can be moved under the code, hidden (Ctrl+J), and resized by dragging the thin bar between it and the code. The choice is remembered.
/// </summary>
internal sealed partial class PyScriptWindow
{
    private readonly Grid _main = new();
    private Border? _card;
    private readonly Border _split = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 128, 128, 128)) };
    private readonly Border _splitLine = new() { CornerRadius = new CornerRadius(1), Opacity = 0.35, Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
    private readonly ToggleButton _panelToggle = new() { Content = "Panel", IsChecked = true };
    private readonly Button _dockButton = new();
    private bool _dockRight = true;
    private bool _panelOpen = true;
    private double _panelWidth = 440;
    private double _panelHeight = 260;
    private bool _dragging;
    private double _dragFrom;
    private double _dragSize;

    private void BuildPanel(Border card)
    {
        _card = card;
        LoadPanelState();
        _split.Child = _splitLine;
        ToolTipService.SetToolTip(_split, "Drag to resize");
        ToolTipService.SetToolTip(_panelToggle, "Show or hide Output, Problems, Logs and AI analysis (Ctrl+J)");
        ToolTipService.SetToolTip(_dockButton, "Put the panel beside the code or under it");
        _panelToggle.Click += (_, _) => SetPanelOpen(_panelToggle.IsChecked == true);
        _dockButton.Click += (_, _) => { _dockRight = !_dockRight; ApplyDock(); SavePanelState(); };

        _split.PointerPressed += (_, e) =>
        {
            _dragging = true;
            _dragFrom = DragPosition(e);
            _dragSize = _dockRight ? _panelWidth : _panelHeight;
            _split.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        _split.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            var size = _dragSize - (DragPosition(e) - _dragFrom);   // dragging towards the code makes the panel bigger
            if (_dockRight) _panelWidth = Math.Clamp(size, 240, Math.Max(240, _main.ActualWidth - 360));
            else _panelHeight = Math.Clamp(size, 120, Math.Max(120, _main.ActualHeight - 180));
            ApplySize();
            e.Handled = true;
        };
        _split.PointerReleased += (_, e) => EndDrag(e);
        _split.PointerCanceled += (_, e) => EndDrag(e);
        _split.PointerCaptureLost += (_, _) => { if (_dragging) { _dragging = false; SavePanelState(); } };

        ApplyDock();
    }

    private double DragPosition(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_main).Position;
        return _dockRight ? p.X : p.Y;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _split.ReleasePointerCapture(e.Pointer);
        SavePanelState();
    }

    private void SetPanelOpen(bool open, bool save = true)
    {
        _panelOpen = open;
        ApplyDock();
        if (save) SavePanelState();
        if (!open) _editor.Focus(FocusState.Programmatic);
    }

    private static void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void ApplySize()
    {
        if (_dockRight && _main.ColumnDefinitions.Count == 3) _main.ColumnDefinitions[2].Width = new GridLength(_panelOpen ? _panelWidth : 0);
        else if (!_dockRight && _main.RowDefinitions.Count == 3) _main.RowDefinitions[2].Height = new GridLength(_panelOpen ? _panelHeight : 0);
    }

    /// <summary>Lays out the code, the bar and the panel for the chosen side. Only the grid definitions change, so the editor is never taken out of the window.</summary>
    private void ApplyDock()
    {
        var card = _card;
        if (card == null) return;
        _main.RowDefinitions.Clear();
        _main.ColumnDefinitions.Clear();
        _tabs.Visibility = _split.Visibility = _panelOpen ? Visibility.Visible : Visibility.Collapsed;
        _panelToggle.IsChecked = _panelOpen;
        _dockButton.Content = _dockRight ? "Move panel down" : "Move panel right";

        if (_dockRight)
        {
            _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
            _main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_panelOpen ? _panelWidth : 0), MinWidth = _panelOpen ? 240 : 0 });
            Place(card, 0, 0);
            Place(_split, 0, 1);
            Place(_tabs, 0, 2);
            card.Margin = new Thickness(12, 0, _panelOpen ? 0 : 12, 0);
            _tabs.Margin = new Thickness(0, 0, 12, 0);
            _split.Width = 10;
            _split.Height = double.NaN;
            _splitLine.Width = 2;
            _splitLine.Height = double.NaN;
            _splitLine.HorizontalAlignment = HorizontalAlignment.Center;
            _splitLine.VerticalAlignment = VerticalAlignment.Stretch;
            _splitLine.Margin = new Thickness(0, 24, 0, 24);
        }
        else
        {
            _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
            _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_panelOpen ? _panelHeight : 0), MinHeight = _panelOpen ? 120 : 0 });
            Place(card, 0, 0);
            Place(_split, 1, 0);
            Place(_tabs, 2, 0);
            card.Margin = new Thickness(12, 0, 12, 0);
            _tabs.Margin = new Thickness(12, 0, 12, 0);
            _split.Height = 10;
            _split.Width = double.NaN;
            _splitLine.Height = 2;
            _splitLine.Width = double.NaN;
            _splitLine.HorizontalAlignment = HorizontalAlignment.Stretch;
            _splitLine.VerticalAlignment = VerticalAlignment.Center;
            _splitLine.Margin = new Thickness(24, 0, 24, 0);
        }
    }

    // ───────── remembered between runs (panel.txt in the extension's folder) ─────────

    private string PanelFile => Path.Combine(_folder, "panel.txt");

    private void LoadPanelState()
    {
        try
        {
            if (!File.Exists(PanelFile)) return;
            foreach (var line in File.ReadAllLines(PanelFile))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "dock": _dockRight = value != "bottom"; break;
                    case "open": _panelOpen = value != "0"; break;
                    case "width": if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) _panelWidth = Math.Clamp(w, 240, 1400); break;
                    case "height": if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) _panelHeight = Math.Clamp(h, 120, 1000); break;
                }
            }
        }
        catch { /* the defaults are fine */ }
    }

    private void SavePanelState()
    {
        try
        {
            File.WriteAllText(PanelFile,
                "dock=" + (_dockRight ? "right" : "bottom") + "\n" +
                "open=" + (_panelOpen ? "1" : "0") + "\n" +
                "width=" + _panelWidth.ToString("0", CultureInfo.InvariantCulture) + "\n" +
                "height=" + _panelHeight.ToString("0", CultureInfo.InvariantCulture) + "\n");
        }
        catch { }
    }
}
