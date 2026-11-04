using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DateCalculator;

public sealed class DateCalculatorExtension : IAsdExtension
{
    public string Id => "MhX2780.DateCalculator";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "dates", Title = "Date Calculator", Glyph = "", Create = () => new DatesView() });
    }
}

internal sealed class DatesView : StackPanel
{
    private readonly CalendarDatePicker _from = new() { Header = "From", Date = DateTimeOffset.Now };
    private readonly CalendarDatePicker _until = new() { Header = "Until", Date = DateTimeOffset.Now.AddDays(30) };
    private readonly TextBlock _between = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly CalendarDatePicker _start = new() { Header = "Start date", Date = DateTimeOffset.Now };
    private readonly NumberBox _days = new() { Header = "Add days (negative = go back)", Value = 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 260 };
    private readonly TextBlock _result = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public DatesView()
    {
        Spacing = 14;
        Children.Add(new TextBlock { Text = "Between two dates", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _from, _until } });
        Children.Add(_between);
        Children.Add(new TextBlock { Text = "A date plus or minus days", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _start, _days } });
        Children.Add(_result);

        _from.DateChanged += (_, _) => Between();
        _until.DateChanged += (_, _) => Between();
        _start.DateChanged += (_, _) => Add();
        _days.ValueChanged += (_, _) => Add();
        Between();
        Add();
    }

    private void Between()
    {
        if (_from.Date is not DateTimeOffset a || _until.Date is not DateTimeOffset b) { _between.Text = "Pick both dates"; return; }
        var first = a.Date; var last = b.Date;
        var sign = last < first ? "-" : "";
        if (last < first) (first, last) = (last, first);

        // whole years and months, then the days that are left
        var years = 0; var months = 0;
        var cursor = first;
        while (cursor.AddYears(1) <= last) { cursor = cursor.AddYears(1); years++; }
        while (cursor.AddMonths(1) <= last) { cursor = cursor.AddMonths(1); months++; }
        var rest = (last - cursor).Days;
        var total = (last - first).Days;
        _between.Text = $"{sign}{total} days  ({sign}{total / 7} weeks and {total % 7} days)\n{sign}{years} years, {months} months, {rest} days";
    }

    private void Add()
    {
        if (_start.Date is not DateTimeOffset s || double.IsNaN(_days.Value)) { _result.Text = ""; return; }
        var date = s.Date.AddDays(_days.Value);
        _result.Text = date.ToString("dddd, d MMMM yyyy");
    }
}
