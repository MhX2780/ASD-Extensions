using System.Globalization;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ColorTools;

public sealed class ColorToolsExtension : IAsdExtension
{
    public string Id => "MhX2780.ColorTools";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "colors", Title = "Color Tools", Glyph = "", Create = () => new ColorView() });
    }
}

internal sealed class ColorView : StackPanel
{
    private readonly TextBox _hex = new() { Header = "HEX", Text = "#3B82F6", Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _r = new() { Header = "Red", Minimum = 0, Maximum = 255, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _g = new() { Header = "Green", Minimum = 0, Maximum = 255, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _b = new() { Header = "Blue", Minimum = 0, Maximum = 255, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _swatch = new() { Width = 140, Height = 140, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 128, 128, 128)) };
    private readonly TextBlock _rgb = new() { IsTextSelectionEnabled = true, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _hsl = new() { IsTextSelectionEnabled = true, FontFamily = new FontFamily("Consolas") };
    private bool _updating;

    public ColorView()
    {
        Spacing = 12;
        var texts = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _rgb, _hsl, CopyRow() } };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Children = { _swatch, texts } };
        Children.Add(top);
        Children.Add(_hex);
        Children.Add(_r);
        Children.Add(_g);
        Children.Add(_b);

        _hex.TextChanged += (_, _) => FromHex();
        _r.ValueChanged += (_, _) => FromSliders();
        _g.ValueChanged += (_, _) => FromSliders();
        _b.ValueChanged += (_, _) => FromSliders();
        FromHex();
    }

    private StackPanel CopyRow()
    {
        var copyHex = new Button { Content = "Copy HEX" };
        var copyRgb = new Button { Content = "Copy RGB" };
        var copyHsl = new Button { Content = "Copy HSL" };
        copyHex.Click += (_, _) => Copy(_hex.Text.Trim());
        copyRgb.Click += (_, _) => Copy(_rgb.Text);
        copyHsl.Click += (_, _) => Copy(_hsl.Text);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyHex, copyRgb, copyHsl } };
    }

    private static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void FromHex()
    {
        if (_updating) return;
        var h = _hex.Text.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return;
        _updating = true;
        _r.Value = (n >> 16) & 255;
        _g.Value = (n >> 8) & 255;
        _b.Value = n & 255;
        _updating = false;
        Show();
    }

    private void FromSliders()
    {
        if (_updating) return;
        _updating = true;
        _hex.Text = $"#{(int)_r.Value:X2}{(int)_g.Value:X2}{(int)_b.Value:X2}";
        _updating = false;
        Show();
    }

    private void Show()
    {
        var r = (byte)_r.Value; var g = (byte)_g.Value; var b = (byte)_b.Value;
        _swatch.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        _rgb.Text = $"rgb({r}, {g}, {b})";
        var (h, s, l) = ToHsl(r, g, b);
        _hsl.Text = $"hsl({h:0}, {s:0}%, {l:0}%)";
    }

    private static (double H, double S, double L) ToHsl(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l * 100);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == rf) h = (gf - bf) / d + (gf < bf ? 6 : 0);
        else if (max == gf) h = (bf - rf) / d + 2;
        else h = (rf - gf) / d + 4;
        return (h * 60, s * 100, l * 100);
    }
}
