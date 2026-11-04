using System.Globalization;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnitConverter;

public sealed class UnitConverterExtension : IAsdExtension
{
    public string Id => "MhX2780.UnitConverter";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "units", Title = "Unit Converter", Glyph = "", Create = () => new ConverterView() });
    }
}

internal static class Units
{
    public const string Temperature = "Temperature";

    // each unit: a name and how many base units it holds (the base is the first unit written as 1)
    public static readonly Dictionary<string, (string Name, double Factor)[]> Table = new()
    {
        ["Length"] = new[] { ("mm", 0.001), ("cm", 0.01), ("m", 1.0), ("km", 1000.0), ("in", 0.0254), ("ft", 0.3048), ("yd", 0.9144), ("mi", 1609.344) },
        ["Mass"] = new[] { ("mg", 0.000001), ("g", 0.001), ("kg", 1.0), ("t", 1000.0), ("oz", 0.028349523125), ("lb", 0.45359237) },
        ["Data size"] = new[] { ("B", 1.0), ("KB", 1024.0), ("MB", 1048576.0), ("GB", 1073741824.0), ("TB", 1099511627776.0) },
        ["Time"] = new[] { ("ms", 0.001), ("s", 1.0), ("min", 60.0), ("h", 3600.0), ("day", 86400.0), ("week", 604800.0) },
        ["Speed"] = new[] { ("m/s", 1.0), ("km/h", 1.0 / 3.6), ("mph", 0.44704), ("knot", 0.514444), ("ft/s", 0.3048) },
        [Temperature] = new[] { ("°C", 1.0), ("°F", 1.0), ("K", 1.0) },
    };

    public static double Convert(string category, string fromUnit, string toUnit, double value)
    {
        if (category == Temperature)
        {
            var celsius = fromUnit switch { "°F" => (value - 32) * 5 / 9, "K" => value - 273.15, _ => value };
            return toUnit switch { "°F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => celsius };
        }
        var units = Table[category];
        var f = units.First(u => u.Name == fromUnit).Factor;
        var t = units.First(u => u.Name == toUnit).Factor;
        return value * f / t;
    }
}

internal sealed class ConverterView : StackPanel
{
    private readonly ComboBox _category = new() { Header = "Kind", MinWidth = 200 };
    private readonly TextBox _value = new() { Header = "Value", Text = "1", MinWidth = 200 };
    private readonly ComboBox _from = new() { Header = "From", MinWidth = 130 };
    private readonly ComboBox _to = new() { Header = "To", MinWidth = 130 };
    private readonly TextBlock _result = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private bool _filling;

    public ConverterView()
    {
        Spacing = 14;
        foreach (var key in Units.Table.Keys) _category.Items.Add(key);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _value, _from, _to } };
        Children.Add(_category);
        Children.Add(row);
        Children.Add(_result);

        _category.SelectionChanged += (_, _) => FillUnits();
        _from.SelectionChanged += (_, _) => Recalculate();
        _to.SelectionChanged += (_, _) => Recalculate();
        _value.TextChanged += (_, _) => Recalculate();
        _category.SelectedIndex = 0;
    }

    private string Category => _category.SelectedItem as string ?? "Length";

    private void FillUnits()
    {
        _filling = true;
        _from.Items.Clear();
        _to.Items.Clear();
        foreach (var u in Units.Table[Category]) { _from.Items.Add(u.Name); _to.Items.Add(u.Name); }
        _from.SelectedIndex = 0;
        _to.SelectedIndex = Math.Min(1, _to.Items.Count - 1);
        _filling = false;
        Recalculate();
    }

    private void Recalculate()
    {
        if (_filling || _from.SelectedItem is not string fromUnit || _to.SelectedItem is not string toUnit) return;
        var text = _value.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { _result.Text = "Type a number"; return; }
        var r = Units.Convert(Category, fromUnit, toUnit, v);
        _result.Text = $"{v.ToString("0.##########", CultureInfo.InvariantCulture)} {fromUnit} = {r.ToString("0.##########", CultureInfo.InvariantCulture)} {toUnit}";
    }
}
