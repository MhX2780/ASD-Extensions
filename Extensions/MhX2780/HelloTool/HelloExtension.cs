using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HelloTool;

/// <summary>The smallest possible extension: one page with a text and a button that opens its own window.</summary>
public sealed class HelloExtension : IAsdExtension
{
    public string Id => "MhX2780.HelloTool";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage
        {
            Tag = "hello",
            Title = "Hello",
            Glyph = "",
            Create = () => BuildPage(host),
        });
    }

    private static FrameworkElement BuildPage(IAsdHost host)
    {
        var open = new Button { Content = "Open a window" };
        open.Click += (_, _) =>
        {
            var w = new Window
            {
                Title = "Hello from an extension",
                Content = new TextBlock { Text = $"This window belongs to the extension. ASD {host.AsdVersion}, API {host.ApiVersion}.", Margin = new Thickness(24) },
            };
            w.Activate();
        };

        var notify = new Button { Content = "Send a notification" };
        notify.Click += (_, _) => host.Notify("Hello", "The extension says hello.");

        return new StackPanel
        {
            Spacing = 12, Padding = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "Hello", FontSize = 28 },
                new TextBlock { Text = "This page was added by an extension (a separate DLL).", Opacity = 0.8 },
                open, notify,
            },
        };
    }
}
