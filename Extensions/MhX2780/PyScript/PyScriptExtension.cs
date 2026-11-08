using ASD.Extensibility;
using AsdPython;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PyScript;

public sealed class PyScriptExtension : IAsdExtension
{
    public string Id => "MhX2780.PyScript";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "pyscript", Title = "PyScript", Glyph = "", Create = () => new Launcher(host) });
    }
}

/// <summary>The page inside ASD: a short description and the button that opens the editor window.</summary>
internal sealed class Launcher : StackPanel
{
    public Launcher(IAsdHost host)
    {
        Spacing = 14;
        var open = new Button { Content = "Open PyScript", Padding = new Thickness(18, 8, 18, 8) };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) open.Style = accent;
        var python = new TextBlock { Opacity = 0.7, Text = "Looking for Python…" };

        Children.Add(new TextBlock { Text = "PyScript is a Python editor that opens in its own window.", TextWrapping = TextWrapping.Wrap });
        Children.Add(new TextBlock
        {
            Text = "• Run your code and see the output, with input for input()\n• Problems: syntax errors, and more when pyflakes is installed\n• Suggestions while you type: keywords, built-ins, snippets, names in your file, and the members of imported modules\n• A log of what you did\n• AI analysis: Claude, Gemini, DeepSeek, api.hcnsec.cn or a provider of your own explains your program, finds bugs and shows fixes (needs your own API key)",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });
        Children.Add(open);
        Children.Add(python);

        open.Click += (_, _) => PyScriptWindow.ShowWindow(host);
        Loaded += async (_, _) =>
        {
            var py = await Task.Run(Py.Find);
            python.Text = py != null ? py.Version + " found." : "Python 3 was not found. Install it from python.org (tick 'Add python.exe to PATH'). You can still open PyScript and write code.";
        };
    }
}
