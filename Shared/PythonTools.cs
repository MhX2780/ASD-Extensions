using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AsdPython;

// Shared by the Python extensions: this file is linked into each of them (see their .csproj), so every DLL stays on its own.

internal sealed record PyInfo(string File, string[] Prefix, string Version)
{
    public string Command => (File + " " + string.Join(' ', Prefix)).Trim();
}

/// <summary>Finds Python 3 on this PC and runs it.</summary>
internal static class Py
{
    private static PyInfo? _found;
    private static bool _searched;

    /// <summary>The "py" launcher, python or python3. Null when there is no Python 3. Slow the first time (call it with Task.Run).</summary>
    public static PyInfo? Find()
    {
        if (_searched) return _found;
        var options = new (string File, string[] Prefix)[] { ("py", new[] { "-3" }), ("python", Array.Empty<string>()), ("python3", Array.Empty<string>()) };
        foreach (var (file, prefix) in options)
        {
            try
            {
                var (code, text) = RunQuick(file, prefix.Append("--version"), 8000);
                if (code == 0 && text.TrimStart().StartsWith("Python 3", StringComparison.OrdinalIgnoreCase))
                {
                    _found = new PyInfo(file, prefix, text.Trim());
                    break;
                }
            }
            catch { /* not installed under this name */ }
        }
        _searched = true;
        return _found;
    }

    public static void Forget() { _searched = false; _found = null; }

    private static Process Start(string file, IEnumerable<string> args, string? workDir)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
    }

    /// <summary>Runs a short command and waits for it (blocks: call it with Task.Run).</summary>
    public static (int Code, string Text) RunQuick(string file, IEnumerable<string> args, int timeoutMs, string stdin = "", string? workDir = null)
    {
        using var p = Start(file, args, workDir);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        try { p.StandardInput.Write(stdin); p.StandardInput.Close(); } catch { }
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        Task.WaitAll(output, error);
        return (p.ExitCode, output.Result + error.Result);
    }

    /// <summary>
    /// Runs a command and reports every output line (isError = it came from stderr; called on a background thread).
    /// Returns the exit code, -2 when stopped through <paramref name="ct"/>, -3 when it ran longer than <paramref name="timeoutSeconds"/> (0 = no limit).
    /// </summary>
    public static async Task<int> RunAsync(string file, IEnumerable<string> args, string? workDir, string stdin,
        Action<bool, string> onLine, CancellationToken ct, int timeoutSeconds = 0)
    {
        using var p = Start(file, args, workDir);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0) guard.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var registration = guard.Token.Register(() => { try { p.Kill(true); } catch { } });

        var pumps = new[] { Pump(p.StandardOutput, false, onLine), Pump(p.StandardError, true, onLine) };
        try { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); } catch { }
        await p.WaitForExitAsync();
        await Task.WhenAll(pumps);
        if (guard.IsCancellationRequested) return ct.IsCancellationRequested ? -2 : -3;
        return p.ExitCode;
    }

    private static async Task Pump(StreamReader reader, bool isError, Action<bool, string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null) onLine(isError, line);
    }
}

/// <summary>File pickers that work from an extension (they need the window that is in front).</summary>
internal static class FilePick
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static async Task<string?> OpenAsync(params string[] extensions)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> SaveAsync(string suggestedName, string description, string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggestedName };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        picker.FileTypeChoices.Add(description, new List<string> { extension });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

/// <summary>A read-only monospace text area that scrolls to the end; error lines are red.</summary>
internal sealed class OutputView : Grid
{
    private static readonly SolidColorBrush Red = new(Windows.UI.Color.FromArgb(255, 235, 87, 87));
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _text = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12.5, IsTextSelectionEnabled = true };
    private readonly StringBuilder _all = new();
    private bool _scrollQueued;

    public OutputView()
    {
        _scroll.Content = _text;
        Children.Add(new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.4 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.08 },
            Child = _scroll,
        });
    }

    public string Text => _all.ToString();

    public void Clear() { _all.Clear(); _text.Inlines.Clear(); }

    /// <summary>UI thread only.</summary>
    public void Add(string line, bool error = false)
    {
        _all.Append(line).Append('\n');
        var run = new Run { Text = line + "\n" };
        if (error) run.Foreground = Red;
        _text.Inlines.Add(run);
        if (_text.Inlines.Count > 6000) for (var i = 0; i < 1000; i++) _text.Inlines.RemoveAt(0);
        if (_scrollQueued) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            _scroll.UpdateLayout();
            _scroll.ChangeView(null, _scroll.ScrollableHeight, null);
        });
    }

    public void AddFromAnyThread(string line, bool error = false) => DispatcherQueue.TryEnqueue(() => Add(line, error));
}
